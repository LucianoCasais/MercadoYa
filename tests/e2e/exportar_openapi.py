"""Exporta la especificacion OpenAPI de la API levantada a contracts/openapi-v1.json.

    python tests/e2e/exportar_openapi.py            # escribe contracts/openapi-v1.json
    python tests/e2e/exportar_openapi.py --check    # no escribe: sale con codigo 1 si el archivo no coincide con la API

Con el sistema levantado (docker compose up -d --build) baja /swagger/v1/swagger.json de la API y lo guarda con formato
legible y claves en orden estable, asi un cambio de la API se ve como un diff normal en git. El mismo archivo se puede
importar en Postman (Import > File) para generar una coleccion con todos los endpoints.
Solo usa Python 3 sin paquetes extra. Variable opcional: API_URL (por defecto http://localhost:5001).
"""
import json, os, pathlib, sys, urllib.request

API = os.environ.get("API_URL", "http://localhost:5001")
DESTINO = pathlib.Path(__file__).resolve().parents[2] / "contracts" / "openapi-v1.json"


def bajar() -> str:
    with urllib.request.urlopen(f"{API}/swagger/v1/swagger.json", timeout=15) as r:
        spec = json.load(r)
    return json.dumps(spec, indent=2, ensure_ascii=False, sort_keys=True) + "\n"


def main() -> int:
    try:
        actual = bajar()
    except OSError as e:
        print(f"No se pudo leer el Swagger de {API}: {e}\nLevantar el sistema con: docker compose up -d --build")
        return 2
    if "--check" in sys.argv:
        if DESTINO.exists() and DESTINO.read_text(encoding="utf-8") == actual:
            print("contracts/openapi-v1.json coincide con la API.")
            return 0
        print("contracts/openapi-v1.json NO coincide con la API: ejecutar el script sin --check y commitear el cambio.")
        return 1
    DESTINO.parent.mkdir(exist_ok=True)
    DESTINO.write_text(actual, encoding="utf-8")
    spec = json.loads(actual)
    operaciones = sum(1 for p in spec["paths"].values() for v in p if v in ("get", "post", "put", "patch", "delete"))
    print(f"Escrito {DESTINO.relative_to(DESTINO.parents[1])}: {len(spec['paths'])} rutas, {operaciones} operaciones.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
