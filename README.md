# MercadoYa

Sistema de gestión y catálogo web de ventas (.NET 10 + Angular + Keycloak). Diseño en `docs/`; verificación de los indicadores del acta en [`docs/verificacion.md`](docs/verificacion.md) y pruebas de caída en [`docs/pruebas-de-fallo.md`](docs/pruebas-de-fallo.md).

Bocetos de pantallas (HTML interactivo): [`docs/boceto-pantallas.html`](docs/boceto-pantallas.html)

## Documentación

| Documento | Contenido |
|---|---|
| [`docs/arquitectura-general.md`](docs/arquitectura-general.md) | Componentes, conexiones, seguridad y fallos |
| [`docs/secuencia-checkout-saga.md`](docs/secuencia-checkout-saga.md) | SAGA del checkout: pasos, compensaciones y diagramas de secuencia |
| [`docs/consistencia-eventual.md`](docs/consistencia-eventual.md) | Qué es consistente, la zona de consistencia eventual y sus garantías |
| [`docs/mensajeria.md`](docs/mensajeria.md) | RabbitMQ: topología, mensaje, reintentos y cola de mensajes muertos |
| [`docs/pruebas-de-fallo.md`](docs/pruebas-de-fallo.md) | Pruebas automáticas y manuales de caídas y concurrencia |
| [`docs/verificacion.md`](docs/verificacion.md) | Verificación de los indicadores del acta |
| [`docs/manual-de-usuario.md`](docs/manual-de-usuario.md) | Guía de uso para clientes y administradores |

## Levantar el sistema

```bash
cp .env.example .env
docker compose up -d --build   # infraestructura + api, payments, 2 réplicas del worker y web
docker compose ps              # esperar a que todo figure healthy (Keycloak es el más lento)
```

La primera vez tarda unos minutos (restauración de paquetes y build de Angular). La tienda queda en http://localhost:3000.

| Servicio | URL | Acceso de desarrollo |
|---|---|---|
| Web (Angular + nginx) | http://localhost:3000 | Usuarios de prueba (abajo) |
| API (Swagger) | http://localhost:5001/swagger | Token de Keycloak |
| Keycloak (consola) | http://localhost:8081 | `admin` / `admin_dev` |
| RabbitMQ (consola) | http://localhost:15672 | `mercadoya` / `mercadoya_dev` |
| PostgreSQL | `localhost:5432` | `mercadoya` / `mercadoya_dev` |
| Payments (gRPC) | `localhost:5003` | Token de servicio de `orders-worker` |

El checkout es asíncrono por defecto (la API publica el pedido en RabbitMQ y el worker ejecuta la SAGA). Para que la API procese la compra dentro de la request, sin RabbitMQ ni worker, levantar con `CHECKOUT_MODE=Sync`.

### Desarrollar con `dotnet run`

Para correr la API, Payments o el worker desde el IDE no tienen que estar levantados los contenedores de esos servicios: ocupan los puertos 5001 y 5003, y un worker en contenedor consumiría los pedidos de la cola en lugar del que se está depurando. Levantar solo la infraestructura:

```bash
docker compose up -d postgres keycloak rabbitmq
```

Si el sistema completo ya estaba levantado: `docker compose stop api payments worker web`.

Usuarios de prueba del realm `mercadoya`: `admin@mercadoya.local` / `Admin123!` (rol `admin`) y `cliente@mercadoya.local` / `Cliente123!` (rol `cliente`). Los clientes que se registren por su cuenta reciben el rol `cliente`.

Para reiniciar todo desde cero (scripts SQL, realm y topología se aplican solo en el primer arranque): `docker compose down -v && docker compose up -d --build`.

### Documentación de la API (Swagger)

La especificación OpenAPI se publica en http://localhost:5001/swagger (JSON en `/swagger/v1/swagger.json`). Cada endpoint tiene resumen, respuestas posibles (incluidos `401`, `403` y `503` donde corresponde) y su tag: Catálogo, Pedidos, Admin - Productos, Admin - Pedidos e Interno - Stock. `POST /api/v1/orders` muestra el header obligatorio `Idempotency-Key`. Los endpoints protegidos llevan un candado e indican qué rol necesitan.

Para probarlos desde Swagger hace falta un token: el realm no habilita el flujo con contraseña, así que se obtiene con *Get New Access Token* de Postman, igual que en las carpetas de cliente y admin de la colección (usuarios de prueba de arriba) y se pega en **Authorize** (solo el token, sin la palabra `Bearer`). Para una revisión rápida de la documentación no hace falta token: `Authorize` solo es necesario para ejecutar endpoints protegidos.

El contrato versionado en git vive en `contracts/openapi-v1.json`. Se regenera con el sistema levantado y también sirve para detectar cambios no documentados:

```bash
python tests/e2e/exportar_openapi.py            # escribe contracts/openapi-v1.json
python tests/e2e/exportar_openapi.py --check    # sale con código 1 si el archivo no coincide con la API
```

### Probar la API con Postman

Importar [`docs/postman/MercadoYa.postman_collection.json`](docs/postman/MercadoYa.postman_collection.json) y el entorno [`docs/postman/MercadoYa.local.postman_environment.json`](docs/postman/MercadoYa.local.postman_environment.json). Las carpetas se corren en orden (salud, catálogo, 401, cliente, admin y worker) y cada request valida su código HTTP. Las de cliente y admin piden el token desde la pestaña Authorization de la carpeta (*Get New Access Token*, con los usuarios de prueba de arriba).

Postman y Swagger se complementan: Swagger documenta el contrato (qué recibe y devuelve cada endpoint) y Postman es el cliente que ejecuta las pruebas. `contracts/openapi-v1.json` se puede importar en Postman (*Import > File*) para generar una colección con todos los endpoints.

## Base de datos

Los scripts de `infra/postgres/init/` se ejecutan en orden al crear el volumen:

| Script | Contenido |
|---|---|
| `01-databases.sql` | BD `payments` (propia del servicio de pagos) y `keycloak` |
| `02-mercadoya-schema.sql` | Esquemas `catalogo` (Category jerárquica con `ParentId`, Product, StockMovement, StockReservation) y `pedidos` (Order, OrderItem) |
| `03-seed.sql` | Árbol del menú (6 categorías, 24 subcategorías, 96 hojas) y 24 productos con stock inicial |
| `04-payments-schema.sql` | `PaymentRecord` con `OrderId` único |

Importes en centavos. `Stock >= 0` está garantizado por un `CHECK`, y la unicidad de `(UserId, IdempotencyKey)`, `(OrderId, ProductId)` de la reserva y `OrderId` del pago sostiene la idempotencia. Por ahora **el SQL es la fuente de verdad del esquema**; los `DbContext` lo mapean sin migraciones.

## Keycloak (`infra/keycloak/mercadoya-realm.json`)

| Elemento | Configuración |
|---|---|
| Realm | `mercadoya`, auto-registro habilitado, email como usuario |
| Roles | `admin` y `cliente` (rol por defecto: `cliente`) |
| Client `web` | Público, Authorization Code + PKCE (S256), redirect `http://localhost:3000/*` |
| Client `orders-worker` | Confidencial, client credentials (secret de desarrollo en el realm) |
| Audience | Mapper que agrega `mercadoya-api` al token de ambos clients |
| Issuer | Fijo en `http://localhost:8081/realms/mercadoya` (`KC_HOSTNAME`); los servicios descubren las claves por `http://keycloak:8080` |

## RabbitMQ (`infra/rabbitmq/definitions.json`)

Exchange `orders` → cola `orders` (quorum, `x-delivery-limit` 5) con DLX `orders.dlx` → cola `orders.dead`. Routing key `order.requested`.

## Módulos

| Proyecto | Responsabilidad | Estado |
|---|---|---|
| `MercadoYa.Shared` | Roles, políticas, contratos del mensaje y validación JWT con Keycloak | Listo |
| `Modules/Catalogo` | Productos y árbol de categorías; listado con búsqueda, filtro por `categoryId` (incluye descendientes), paginación y detalle públicos | Listo: lectura con caché en memoria (header `X-Cache: HIT/MISS`, se invalida con el ABM y con cada cambio de stock; TTL 60 s) y ABM admin (`/api/v1/admin/products`, baja lógica) |
| `Modules/Inventario` | Reserva atómica e idempotente, liberación, endpoints internos de stock y ajuste del admin (`PATCH /api/v1/admin/products/{id}/stock`, registra `StockMovement`) | Listo |
| `Modules/Pedidos` | Entidades, máquina de estados, checkout (Sync y Async con RabbitMQ), pedidos del cliente y del admin, SAGA con compensación y resiliencia con Polly | Listo |
| `MercadoYa.Api` | Host REST versionado (`/api/v1`, header `api-supported-versions`), Swagger, CORS, errores en ProblemDetails y health checks (`/health/live`, `/health/ready` en JSON) | Listo |
| `MercadoYa.Worker` | Consumidor de la cola `orders` que ejecuta la SAGA (reservar stock, cobrar, confirmar o compensar); 2 réplicas en Docker | Listo |
| `MercadoYa.Payments` | Servicio gRPC de cobro simulado e idempotente, con BD propia; solo acepta el token de servicio de `orders-worker` (client credentials) | Listo. gRPC en `localhost:5003` (local y Docker); health checks en `localhost:5004` con `dotnet run` (en Docker, solo dentro del contenedor). Rechaza importes con `amount_cents % 1000 == 999` (por ejemplo un producto de $ 9,99) |
| `tests/MercadoYa.Tests` | xUnit: dominio, checkout, SAGA, resiliencia, API y seguridad; con PostgreSQL y RabbitMQ reales (Testcontainers) | Listo (300 tests, 76 % de cobertura) |

Contratos en `contracts/` (`payments.proto`, `order-requested.json` y `openapi-v1.json`, este último generado con el script de la sección Swagger).

## Tests

```bash
dotnet test                                        # todos (los de integración levantan PostgreSQL y RabbitMQ descartables: requiere Docker)
dotnet test --filter "Category!=Integracion"       # solo los que no necesitan Docker
```

## Pendiente

- Cambiar los secretos de desarrollo antes de compartir o desplegar el proyecto.
