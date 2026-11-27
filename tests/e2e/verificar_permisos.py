"""Verifica 401/403 en todos los endpoints de administracion (y los internos del worker) contra la API levantada.

    python tests/e2e/verificar_permisos.py

Los endpoints se leen del Swagger de la API (/swagger/v1/swagger.json): si se agrega un endpoint de admin, el script lo
prueba sin tocar nada. Para cada endpoint de /admin/:
  sin token -> 401 | token falsificado -> 401 | cliente -> 403 | worker (orders-worker) -> 403 | admin -> permitido
Para los internos (/internal/), al reves: solo el worker entra; un admin o un cliente reciben 403.
Las llamadas usan ids que no existen y cuerpos vacios: las permitidas responden 400/404 y no modifican datos.
Indicador del acta: "100 % de las funciones del panel de administracion restringidas al rol administrador".
Solo usa Python 3 sin paquetes extra. Sale con codigo 1 si algo no cumple.
"""
import base64, hashlib, json, os, re, sys, urllib.error, urllib.parse, urllib.request, uuid

API = os.environ.get("API_URL", "http://localhost:5001")
WEB = os.environ.get("WEB_URL", "http://localhost:3000")
KC = os.environ.get("KEYCLOAK_REALM", "http://localhost:8081/realms/mercadoya") + "/protocol/openid-connect"
WORKER_SECRET = os.environ.get("WORKER_SECRET", "dev-only-change-me")


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a, **k):
        return None


def login(user, password):
    """Mismo flujo que la web: Authorization Code + PKCE (las cookies Secure de Keycloak se pasan a mano)."""
    opener = urllib.request.build_opener(NoRedirect)
    verifier = base64.urlsafe_b64encode(os.urandom(32)).rstrip(b"=").decode()
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
    redirect = WEB + "/"
    q = urllib.parse.urlencode({"client_id": "web", "response_type": "code", "scope": "openid", "redirect_uri": redirect,
                                "code_challenge": challenge, "code_challenge_method": "S256"})
    first = opener.open(f"{KC}/auth?{q}")
    cookies = "; ".join(c.split(";", 1)[0] for c in first.headers.get_all("Set-Cookie") or [])
    action = re.search(r'action="([^"]+)"', first.read().decode()).group(1).replace("&amp;", "&")
    try:
        opener.open(urllib.request.Request(action, urllib.parse.urlencode({"username": user, "password": password}).encode(),
                                           headers={"Cookie": cookies}))
        raise RuntimeError(f"Keycloak no acepto el login de {user}")
    except urllib.error.HTTPError as e:
        location = e.headers["Location"]
    code = urllib.parse.parse_qs(urllib.parse.urlparse(location).query)["code"][0]
    body = urllib.parse.urlencode({"grant_type": "authorization_code", "client_id": "web", "code": code,
                                   "redirect_uri": redirect, "code_verifier": verifier}).encode()
    return json.loads(urllib.request.urlopen(f"{KC}/token", body).read())["access_token"]


def worker_token():
    body = urllib.parse.urlencode({"grant_type": "client_credentials", "client_id": "orders-worker", "client_secret": WORKER_SECRET}).encode()
    return json.loads(urllib.request.urlopen(f"{KC}/token", body).read())["access_token"]


def forged(token):
    """El mismo token con la firma cambiada: dice ser quien es, pero no lo firmo Keycloak."""
    head, payload, sig = token.split(".")
    return f"{head}.{payload}.{sig[::-1]}"


def call(method, path, token):
    headers = {"Content-Type": "application/json"}
    if token: headers["Authorization"] = f"Bearer {token}"
    data = b"{}" if method in ("POST", "PUT", "PATCH") else None
    try:
        return urllib.request.urlopen(urllib.request.Request(API + path, data=data, method=method, headers=headers), timeout=30).status
    except urllib.error.HTTPError as e:
        return e.code


def concrete(path, params):
    """Reemplaza {id} por un valor que no existe del tipo que pide el endpoint (entero o GUID)."""
    def value(m):
        schema = params.get(m.group(1), {})
        return str(uuid.uuid4()) if schema.get("format") == "uuid" else "999999999"
    return re.sub(r"\{(\w+)\}", value, path)


def main():
    spec = json.loads(urllib.request.urlopen(API + "/swagger/v1/swagger.json", timeout=30).read())
    endpoints = []
    for path, ops in spec["paths"].items():
        for method, op in ops.items():
            params = {p["name"]: p.get("schema", {}) for p in op.get("parameters", []) if p.get("in") == "path"}
            kind = "admin" if "/admin/" in path else "interno" if path.startswith("/internal/") else None
            if kind: endpoints.append((kind, method.upper(), path, concrete(path, params)))

    print("Obteniendo tokens (cliente, admin y worker)...", flush=True)
    tokens = {"cliente": login("cliente@mercadoya.local", "Cliente123!"),
              "admin": login("admin@mercadoya.local", "Admin123!"),
              "worker": worker_token()}
    allowed = lambda s: s not in (401, 403) and s < 500
    expectations = {
        "admin": [("sin token", None, lambda s: s == 401, "401"), ("token falsificado", forged(tokens["admin"]), lambda s: s == 401, "401"),
                  ("cliente", tokens["cliente"], lambda s: s == 403, "403"), ("worker", tokens["worker"], lambda s: s == 403, "403"),
                  ("admin", tokens["admin"], allowed, "permitido")],
        "interno": [("sin token", None, lambda s: s == 401, "401"), ("token falsificado", forged(tokens["worker"]), lambda s: s == 401, "401"),
                    ("cliente", tokens["cliente"], lambda s: s == 403, "403"), ("admin", tokens["admin"], lambda s: s == 403, "403"),
                    ("worker", tokens["worker"], allowed, "permitido")],
    }

    failures = 0
    who_cols = ["sin token", "token falsificado", "cliente", "worker", "admin"]
    print(f"\n{'':4}{'Metodo':7}{'Endpoint':52}" + "".join(f"{c:>19}" for c in who_cols))
    for kind, method, path, url in sorted(endpoints, key=lambda e: (e[0], e[2], e[1])):
        results = {}
        ok_all = True
        for who, token, ok, expected in expectations[kind]:
            status = call(method, url, token)
            good = ok(status)
            ok_all &= good
            results[who] = f"{status}{'' if good else ' (esperado ' + expected + ')'}"
        failures += not ok_all
        print(f"{'OK ' if ok_all else 'MAL'} {method:7}{path:52}" + "".join(f"{results[c]:>19}" for c in who_cols))

    n_admin = sum(1 for e in endpoints if e[0] == "admin")
    n_internal = len(endpoints) - n_admin
    print(f"\nEndpoints de admin: {n_admin} | internos: {n_internal} | con problemas: {failures}")
    if n_admin == 0:
        print("No se encontro ningun endpoint de admin en el Swagger: revisar la API.")
        return 1
    print("RESULTADO: " + ("100 % de los endpoints de admin restringidos al rol admin" if failures == 0 else "HAY ENDPOINTS SIN PROTEGER"))
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
