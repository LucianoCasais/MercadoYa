"""Checklist E2E de MercadoYa contra el sistema levantado con `docker compose up -d --build`.

Recorre los 15 puntos de docs/verificacion.md (31 comprobaciones) por la API y la base, iniciando sesion igual que la web
(Authorization Code + PKCE con redirect a http://localhost:3000). Solo usa la biblioteca estandar de Python 3.

    python tests/e2e/checklist_e2e.py

Atencion: crea productos y pedidos de prueba, y detiene unos segundos Payments y RabbitMQ para probar las caidas
(al final los vuelve a levantar). Para dejar la base limpia: docker compose down -v && docker compose up -d --build.
Sale con codigo 1 si alguna comprobacion falla.
"""
import base64, hashlib, json, os, re, subprocess, sys, threading, time, urllib.error, urllib.parse, urllib.request, uuid
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
API, WEB = "http://localhost:5001", "http://localhost:3000"
KC = "http://localhost:8081/realms/mercadoya/protocol/openid-connect"
USERS = {"cliente": ("cliente@mercadoya.local", "Cliente123!"), "admin": ("admin@mercadoya.local", "Admin123!")}
RESULTS = []


def check(n, desc, ok, detail=""):
    RESULTS.append((n, desc, ok, detail))
    print(f"[{'OK ' if ok else 'MAL'}] {n:>4} {desc}" + (f"  ->  {detail}" if detail else ""), flush=True)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a, **k):
        return None


def login(user, password):
    """Mismo flujo que la web: Authorization Code + PKCE, con redirect a la web en :3000."""
    # Las cookies de Keycloak son Secure y urllib no las reenvia por http://localhost (el navegador si): se pasan a mano.
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
        raise RuntimeError("Keycloak no redirigio (usuario o clave incorrectos?)")
    except urllib.error.HTTPError as e:
        location = e.headers["Location"]
    code = urllib.parse.parse_qs(urllib.parse.urlparse(location).query)["code"][0]
    body = urllib.parse.urlencode({"grant_type": "authorization_code", "client_id": "web", "code": code,
                                   "redirect_uri": redirect, "code_verifier": verifier}).encode()
    return json.loads(urllib.request.urlopen(f"{KC}/token", body).read())["access_token"], location


_tokens = {}
def token(who):
    """Token fresco cada 2 minutos (los de Keycloak duran 5)."""
    t = _tokens.get(who)
    if not t or time.time() - t[1] > 120:
        _tokens[who] = (login(*USERS[who])[0], time.time())
    return _tokens[who][0]


def call(method, path, who=None, body=None, headers=None, base=API):
    h = {"Content-Type": "application/json", **(headers or {})}
    if who: h["Authorization"] = f"Bearer {token(who)}"
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(base + path, data=data, method=method, headers=h)
    start = time.perf_counter()
    try:
        r = urllib.request.urlopen(req, timeout=30)
        status, raw, rh = r.status, r.read(), r.headers
    except urllib.error.HTTPError as e:
        status, raw, rh = e.code, e.read(), e.headers
    elapsed = time.perf_counter() - start
    try: payload = json.loads(raw) if raw else None
    except ValueError: payload = raw.decode(errors="replace")
    return status, payload, rh, elapsed


def stock(pid):
    return call("GET", f"/api/v1/admin/products/{pid}", "admin")[1]["stock"]


def buy(items, cid=None):
    h = {"Idempotency-Key": uuid.uuid4().hex}
    if cid: h["X-Correlation-Id"] = cid
    return call("POST", "/api/v1/orders", "cliente", {"items": [{"productId": p, "quantity": q} for p, q in items]}, h)


def wait_final(order_id, timeout=45):
    end = time.time() + timeout
    while True:
        o = call("GET", f"/api/v1/orders/{order_id}", "cliente")[1]
        if o["status"] != "Pendiente" or time.time() > end: return o
        time.sleep(1)


def create_product(name, price, initial_stock):
    s, p, _, _ = call("POST", "/api/v1/admin/products", "admin",
                      {"name": name, "description": "Producto de la checklist", "priceCents": price, "categoryId": CATEGORY,
                       "imageUrl": None, "initialStock": initial_stock})
    assert s == 201, (s, p)
    return p


def compose(*args):
    return subprocess.run(["docker", "compose", *args], cwd=REPO, capture_output=True, text=True, encoding="utf-8", errors="replace")


def psql(sql):
    return compose("exec", "-T", "postgres", "psql", "-U", "mercadoya", "-d", "mercadoya", "-tAc", sql).stdout.strip()


def services_logging(cid):
    """Servicios en cuyos logs aparece el CorrelationId (sirve con logs en texto o en JSON)."""
    return [s for s in ("api", "payments", "worker") if cid in compose("logs", "--no-log-prefix", s).stdout]


# ---------------------------------------------------------------- preparacion
s, ready, _, _ = call("GET", "/health/ready")
if s != 200:
    sys.exit(f"La API no esta lista ({s}). Levantar el sistema con: docker compose up -d --build")
CATEGORY = call("GET", "/api/v1/categories")[1][0]["id"]
# Producto "normal" para las compras: el primero activo con stock de sobra (no depende de ids fijos del seed).
P_NORMAL = next(p["id"] for p in call("GET", "/api/v1/admin/products?pageSize=100", "admin")[1]["items"]
                if p["active"] and p["stock"] >= 15 and p["priceCents"] % 1000 != 999)

# ---------------------------------------------------------------- 1. catalogo sin sesion
s, page1, _, t_cold = call("GET", "/api/v1/products?pageSize=12")
_, _, h_hot, t_hot = call("GET", "/api/v1/products?pageSize=12")
check("1a", "Catalogo sin sesion: 20 o mas productos", s == 200 and page1["total"] >= 20, f"{page1['total']} productos")
check("1b", "Carga del catalogo < 3 s (indicador del acta)", t_cold < 3, f"primera {t_cold*1000:.0f} ms, desde cache {t_hot*1000:.0f} ms (X-Cache {h_hot.get('X-Cache')})")
s, found, _, _ = call("GET", "/api/v1/products?search=yerba")
check("1c", "Busqueda ('yerba')", s == 200 and found["total"] >= 1 and all("yerba" in p["name"].lower() for p in found["items"]), ", ".join(p["name"] for p in found["items"]))
s, page2, _, _ = call("GET", "/api/v1/products?pageSize=12&page=2")
check("1d", "Paginacion (pagina 2 distinta de la 1)", s == 200 and len(page2["items"]) > 0 and {p["id"] for p in page2["items"]}.isdisjoint({p["id"] for p in page1["items"]}), f"{len(page2['items'])} productos en la pagina 2")
pid = page1["items"][0]["id"]
s, detail, _, _ = call("GET", f"/api/v1/products/{pid}")
check("1e", "Detalle de producto", s == 200 and {"name", "priceCents", "stock", "description"} <= detail.keys(), f"{detail['name']}, stock {detail['stock']}")
web = [call("GET", p, base=WEB)[0] for p in ("/", "/tienda", f"/tienda/producto/{pid}", "/admin/productos")]
check("1f", "La web sirve la aplicacion y sus rutas (:3000)", web == [200, 200, 200, 200], str(web))

# ---------------------------------------------------------------- 2. checkout sin sesion
s, _, _, _ = call("POST", "/api/v1/orders", body={"items": [{"productId": pid, "quantity": 1}]}, headers={"Idempotency-Key": "x"})
check("2", "Comprar sin sesion: la API responde 401 (la web redirige al login)", s == 401, str(s))

# ---------------------------------------------------------------- 3. compra normal
_, location = login(*USERS["cliente"])
check("3a", "Login del cliente con Keycloak (PKCE) vuelve a la web", location.startswith(WEB + "/"), location[:45] + "...")
before = stock(P_NORMAL)
cid = "checklist-" + uuid.uuid4().hex[:8]
s, o, h, _ = buy([(P_NORMAL, 1)], cid)
final = wait_final(o["id"])
check("3b", "Compra normal: 202 Pendiente y despues Confirmado", s == 202 and o["status"] == "Pendiente" and final["status"] == "Confirmado", f"{s} {o['status']} -> {final['status']}")
check("3c", "La respuesta devuelve el X-Correlation-Id enviado", h.get("X-Correlation-Id") == cid, h.get("X-Correlation-Id"))
check("3d", "El stock baja 1", stock(P_NORMAL) == before - 1, f"{before} -> {stock(P_NORMAL)}")
order_ok = o["id"]

# ---------------------------------------------------------------- 4. cliente en /admin
codes = [call("GET", p, "cliente")[0] for p in ("/api/v1/admin/products", "/api/v1/admin/orders")]
check("4", "El cliente no tiene acceso al admin (403)", codes == [403, 403], str(codes))

# ---------------------------------------------------------------- 5. pago rechazado (importe terminado en 999)
cheap = create_product("Chicle checklist $9,99", 999, 5)
s, o, _, _ = buy([(cheap["id"], 1)])
final = wait_final(o["id"])
check("5a", "Compra de $ 9,99: Rechazado (PagoFallido)", final["status"] == "Rechazado" and final["reason"] == "PagoFallido", f"{final['status']} ({final['reason']})")
check("5b", "El stock vuelve", stock(cheap["id"]) == 5, f"stock {stock(cheap['id'])} de 5")
order_rejected = o["id"]

# ---------------------------------------------------------------- 6. el admin ve los pedidos
s, orders, _, _ = call("GET", "/api/v1/admin/orders?pageSize=50", "admin")
by_id = {x["id"]: x for x in orders["items"]}
check("6", "El admin ve los pedidos con su estado", by_id.get(order_ok, {}).get("status") == "Confirmado" and by_id.get(order_rejected, {}).get("status") == "Rechazado",
      f"{len(orders['items'])} pedidos; {by_id.get(order_ok, {}).get('status')} / {by_id.get(order_rejected, {}).get('status')}")

# ---------------------------------------------------------------- 7. ABM
p = create_product("Alta checklist", 250000, 3)
check("7a", "Alta: el catalogo publico la muestra", call("GET", f"/api/v1/products/{p['id']}")[0] == 200, p["name"])
s, _, _, _ = call("PUT", f"/api/v1/admin/products/{p['id']}", "admin",
                  {"name": "Alta checklist editada", "description": "x", "priceCents": 260000, "categoryId": CATEGORY, "imageUrl": None, "active": True})
pub = call("GET", f"/api/v1/products/{p['id']}")[1]
check("7b", "Edicion: el catalogo publico refleja nombre y precio", s == 200 and pub["name"] == "Alta checklist editada" and pub["priceCents"] == 260000, f"{pub['name']} $ {pub['priceCents']/100:.2f}")
s = call("DELETE", f"/api/v1/admin/products/{p['id']}", "admin")[0]
gone = call("GET", f"/api/v1/products/{p['id']}")[0]
in_search = [x["id"] for x in call("GET", "/api/v1/products?search=checklist%20editada&pageSize=50")[1]["items"]]
check("7c", "Baja logica: desaparece de la tienda (404 y fuera de la busqueda)", s == 204 and gone == 404 and p["id"] not in in_search, f"DELETE {s}, detalle {gone}")

# ---------------------------------------------------------------- 8. ajuste de stock
before = stock(P_NORMAL)
count_sql = f'SELECT count(*) FROM catalogo."StockMovement" WHERE "ProductId"={P_NORMAL} AND "Reason"=\'Ajuste\''
movs_before = int(psql(count_sql))
s, adj, _, _ = call("PATCH", f"/api/v1/admin/products/{P_NORMAL}/stock", "admin", {"delta": 5})
movs_after = int(psql(count_sql))
check("8", "Ajuste de stock +5: cambia el stock y queda un movimiento 'Ajuste'", s == 200 and adj["stock"] == before + 5 and movs_after == movs_before + 1, f"{before} -> {adj['stock']}, movimientos {movs_before} -> {movs_after}")

# ---------------------------------------------------------------- 9. entregar y cancelar
o1 = wait_final(buy([(P_NORMAL, 1)])[1]["id"]); o2 = wait_final(buy([(P_NORMAL, 2)])[1]["id"])
s1, r1, _, _ = call("PATCH", f"/api/v1/admin/orders/{o1['id']}/status", "admin", {"status": "Entregado"})
before = stock(P_NORMAL)
s2, r2, _, _ = call("PATCH", f"/api/v1/admin/orders/{o2['id']}/status", "admin", {"status": "Cancelado"})
check("9a", "Pedido Confirmado -> Entregado", s1 == 200 and r1["status"] == "Entregado", f"{o1['status']} -> {r1.get('status')}")
check("9b", "Pedido Confirmado -> Cancelado libera el stock", s2 == 200 and r2["status"] == "Cancelado" and stock(P_NORMAL) == before + 2, f"stock {before} -> {stock(P_NORMAL)}")
s3 = call("PATCH", f"/api/v1/admin/orders/{o1['id']}/status", "admin", {"status": "Cancelado"})[0]
check("9c", "Un pedido Entregado ya no se puede cancelar (409)", s3 == 409, str(s3))

# ---------------------------------------------------------------- 10. doble compra del ultimo stock
last = create_product("Ultima unidad checklist", 150000, 1)
res = [None, None]
def worker(i): res[i] = buy([(last["id"], 1)])
threads = [threading.Thread(target=worker, args=(i,)) for i in range(2)]
[t.start() for t in threads]; [t.join() for t in threads]
finals = sorted((wait_final(r[1]["id"]) for r in res), key=lambda x: x["status"])
check("10", "Dos compras del ultimo stock: una Confirmada y una Rechazada (SinStock)",
      [f["status"] for f in finals] == ["Confirmado", "Rechazado"] and finals[1]["reason"] == "SinStock" and stock(last["id"]) == 0,
      f"{finals[0]['status']} / {finals[1]['status']} ({finals[1]['reason']}), stock {stock(last['id'])}")

# ---------------------------------------------------------------- 11. payments caido
try:
    compose("stop", "payments")
    before = stock(P_NORMAL)
    final = wait_final(buy([(P_NORMAL, 1)])[1]["id"], timeout=60)
    check("11a", "Payments caido: Rechazado (PagoNoDisponible)", final["status"] == "Rechazado" and final["reason"] == "PagoNoDisponible", f"{final['status']} ({final['reason']})")
    check("11b", "El stock vuelve", stock(P_NORMAL) == before, f"{before} -> {stock(P_NORMAL)}")

    # ------------------------------------------------------------ 12 y 13. rabbitmq caido (payments sigue caido)
    compose("stop", "rabbitmq")
    s, body, _, _ = buy([(P_NORMAL, 1)])
    newest = call("GET", "/api/v1/admin/orders?pageSize=1", "admin")[1]["items"][0]
    check("12", "RabbitMQ caido: 503 y el pedido queda Rechazado (BrokerNoDisponible)", s == 503 and newest["status"] == "Rechazado" and newest["reason"] == "BrokerNoDisponible", f"{s}; ultimo pedido {newest['status']} ({newest['reason']})")
    s, cat, _, _ = call("GET", "/api/v1/products?search=cafe")   # sin tilde: tambien prueba la busqueda que ignora tildes
    check("13a", "Con Payments y RabbitMQ caidos el catalogo sigue funcionando (busqueda 'cafe')", s == 200 and cat["total"] >= 1, f"{s}, {cat['total']} resultados")
    s, ready, _, _ = call("GET", "/health/ready")
    check("13b", "Y /health/ready sigue 200 (RabbitMQ no cuenta para listo)", s == 200, f"{s} {ready['status']}")
finally:
    # ------------------------------------------------------------ recuperacion (aunque algo falle en el medio)
    compose("up", "-d", "--wait")
time.sleep(35)   # el circuit breaker de pagos queda abierto 30 s despues de las fallas del punto 11
final = wait_final(buy([(P_NORMAL, 1)])[1]["id"], timeout=60)
check("14a", "Al levantar todo de nuevo, una compra vuelve a Confirmarse", final["status"] == "Confirmado", f"{final['status']} {final.get('reason') or ''}")
s, ready, _, _ = call("GET", "/health/ready")
check("14b", "/health/ready responde 200", s == 200, ready["status"])

# ---------------------------------------------------------------- 15. correlacion
services = services_logging(cid)
check("15", "El CorrelationId de la compra 3 aparece en api, worker y payments", services == ["api", "payments", "worker"], f"{cid} en {services}")

ok = sum(1 for r in RESULTS if r[2])
print(f"\nRESULTADO: {ok} de {len(RESULTS)} OK")
sys.exit(0 if ok == len(RESULTS) else 1)
