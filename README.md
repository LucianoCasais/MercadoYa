# MercadoYa

Sistema de gestión y catálogo web de ventas (.NET 10 + Angular + Keycloak). Este repositorio contiene la **base de la entrega del 24/11**: infraestructura de datos, base de datos, realm de Keycloak y esqueleto de módulos. Diseño en `docs/`.

Bocetos de pantallas (HTML interactivo): [`docs/boceto-pantallas.html`](docs/boceto-pantallas.html)

## Levantar la infraestructura

```bash
cp .env.example .env
docker compose up -d          # postgres, keycloak, rabbitmq
```

| Servicio | URL | Acceso de desarrollo |
|---|---|---|
| Keycloak (consola) | http://localhost:8081 | `admin` / `admin_dev` |
| RabbitMQ (consola) | http://localhost:15672 | `mercadoya` / `mercadoya_dev` |
| PostgreSQL | `localhost:5432` | `mercadoya` / `mercadoya_dev` |

Usuarios de prueba del realm `mercadoya`: `admin@mercadoya.local` / `Admin123!` (rol `admin`) y `cliente@mercadoya.local` / `Cliente123!` (rol `cliente`). Los clientes que se registren por su cuenta reciben el rol `cliente`.

Para reiniciar todo desde cero (scripts SQL, realm y topología se aplican solo en el primer arranque): `docker compose down -v && docker compose up -d`.

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
| `Modules/Catalogo` | Productos y árbol de categorías; listado con búsqueda, filtro por `categoryId` (incluye descendientes), paginación y detalle públicos | Lectura y ABM admin (`/api/v1/admin/products`, baja lógica) listos; falta caché (día 9) |
| `Modules/Inventario` | Reserva atómica e idempotente, liberación, endpoints internos de stock y ajuste del admin (`PATCH /api/v1/admin/products/{id}/stock`, registra `StockMovement`) | Listo |
| `Modules/Pedidos` | Entidades, máquina de estados y `DbContext` | Falta POST/GET de pedidos |
| `MercadoYa.Api` | Host REST `/api/v1`, Swagger, health checks, CORS | Esqueleto |
| `MercadoYa.Worker` | Orquestador de la SAGA | Solo documentado (días 8 a 10) |
| `MercadoYa.Payments` | Servicio gRPC de cobro simulado e idempotente, con BD propia; solo acepta el token de servicio de `orders-worker` (client credentials) | Listo. gRPC en `localhost:5003` (local y Docker); health checks en `localhost:5004` con `dotnet run` (en Docker, solo dentro del contenedor). Rechaza importes con `amount_cents % 1000 == 999` (por ejemplo un producto de $ 9,99) |
| `tests/MercadoYa.Tests` | xUnit: máquina de estados, checkout, validaciones y `StockService` contra PostgreSQL real (Testcontainers) | En curso |

Contratos en `contracts/` (`payments.proto`, `order-requested.json`).

## Tests

```bash
dotnet test                                        # todos (los de stock levantan un PostgreSQL 16 descartable: requiere Docker)
dotnet test --filter "Category!=Integracion"       # solo los que no necesitan Docker
```

## Pendiente

- **Angular (`web/`)**: crear con `ng new web` y seguir las pantallas de los bocetos (`/tienda`, `/admin`, seguimiento en `/tienda/pedido/:id`).
- **No se compiló nada**: este entorno no tiene el SDK de .NET. Ejecutar `dotnet restore && dotnet build` y ajustar las versiones de paquetes (están como `10.*`).
- Los servicios `api`, `worker`, `payments` y `web` del `docker-compose.yml` están en el perfil `app` hasta que haya código para levantarlos: `docker compose --profile app up -d --build`.
- Cambiar los secretos de desarrollo antes de compartir o desplegar el proyecto.
