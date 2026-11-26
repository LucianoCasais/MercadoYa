# Verificación de los indicadores del acta

Evidencia de que el sistema cumple los indicadores del acta. Cada fila dice cómo se comprobó y qué resultó.
El procedimiento de las pruebas de caída está en [pruebas-de-fallo.md](pruebas-de-fallo.md).

**Verificación desde cero — 25/11/2026**

## 1. Indicadores

| Indicador | Cómo se verificó | Resultado |
|---|---|---|
| 100 % de los entregables principales | Checklist E2E (sección 3) | 31 de 31 comprobaciones OK por script (API y base) y los 4 puntos visuales OK en el navegador (sección 3.1) |
| Al menos 20 productos en el catálogo de prueba | Seed de la base al arrancar de cero | 24 productos, 126 categorías |
| Catálogo en menos de 3 s | DevTools / medición de `GET /api/v1/products` | 887 ms recién levantado, 35 ms desde la caché |
| Compra en no más de 5 pasos | Recorrido del checkout en el navegador | 5 pasos o menos, comprobado en el navegador (sección 3.1) |
| 100 % de las funciones admin restringidas al rol admin | `SecurityTests`: recorre los 18 endpoints con cada rol | OK (corre con `dotnet test`) |
| Cobertura de backend ≥ 40 % | `dotnet test` con coverlet y reportgenerator | **76,2 %** de líneas (meta 50 %) |
| 0 errores críticos abiertos | Pruebas de fallo y E2E | Ninguno crítico; un hallazgo menor abierto (sección 4) |

### Cobertura por proyecto

| Proyecto | Cobertura |
|---|---|
| Inventario / Shared | 100 % |
| Catálogo | 93,6 % |
| API | 81,1 % |
| Pedidos | 76,7 % |
| Worker | 68,8 % |
| Payments | 49,1 % (cuenta el código generado de gRPC) |

Total: 300 tests, todos correctos. El reporte HTML se genera en `coverage/report/index.html` (la carpeta `coverage/` está en `.gitignore`).

## 2. Arranque desde cero

`docker compose down -v` y `docker compose up -d --build`: todo levantó sano en 53 s, con las 2 réplicas del worker y la web en `http://localhost:3000`. Nació solo:

- Base `mercadoya`: 6 tablas, 24 productos y 126 categorías. Base `payments`: su tabla.
- Keycloak: realm `mercadoya`.
- RabbitMQ: cola `orders` con DLX y `orders.dead`, y 2 consumidores.

## 3. Checklist E2E

Se corrió con un script que inicia sesión igual que la web (Authorization Code + PKCE con redirect a `:3000`) y verifica cada resultado contra la API y la base.

Para repetirla a mano conviene este orden, porque algunos puntos dependen de datos de los anteriores:

1. Crear el producto de **$ 9,99** (admin) antes de la prueba del pago rechazado, y comprar **una sola unidad** (dos suman $ 19,98 y se aprueban).
2. Hacer **dos compras normales** antes de probar Entregado y Cancelado (hacen falta dos pedidos Confirmados).
3. Después de volver a levantar Payments, **esperar unos 30 s**: el circuit breaker queda abierto ese tiempo y un pedido inmediato saldría `PagoNoDisponible`.

| # | Punto | Resultado esperado y observado |
|---|---|---|
| 1 | Catálogo sin sesión, búsqueda, paginación, detalle (precio, stock, descripción) y web en `:3000` | 24 productos; las rutas de Angular responden |
| 1b | Carga del catálogo < 3 s (acta) | 887 ms recién levantado, 35 ms desde la caché |
| 2 | Comprar sin sesión | 401 |
| 3 | Compra normal | 202 Pendiente → Confirmado; el stock baja 1 |
| 4 | Cliente en el admin | 403 |
| 5 | Compra de $ 9,99 (una unidad) | Rechazado (PagoFallido); el stock vuelve |
| 6 | El admin ve los pedidos | Con su estado |
| 7 | Alta, edición y baja de un producto | La tienda refleja cada cambio |
| 8 | Ajuste de stock +5 | Cambia el stock y queda el movimiento `Ajuste` |
| 9 | Entregado y Cancelado | Cancelar libera el stock; un pedido Entregado ya no se puede cancelar (409) |
| 10 | Dos compras del último stock | Una Confirmada y una Rechazada (SinStock); stock 0 |
| 11 | Payments caído | Rechazado (PagoNoDisponible); el stock vuelve |
| 12 | RabbitMQ caído | 503 y el pedido queda Rechazado (BrokerNoDisponible) |
| 13 | Catálogo con los dos caídos | Responde, y `/health/ready` sigue en 200 (RabbitMQ no entra en el health a propósito: el catálogo y el admin no dependen de él) |
| 14 | Al volver a levantar todo | Una compra se confirma otra vez; `/health/ready` 200 |
| 15 | CorrelationId | El `X-Correlation-Id` de la compra 3 aparece en `api`, `worker` y `payments` (`docker compose logs api worker payments`, buscar el id) |

### 3.1 Puntos que se confirman en el navegador

Lo visual que el script no puede mostrar, comprobado a mano en `http://localhost:3000`:

| # | Qué mirar | Resultado | Cuándo |
|---|---|---|---|
| 2 | El checkout sin sesión redirige al login de Keycloak | OK | 25/11 |
| 3 | La compra se completa en 5 pasos o menos | OK | 25/11 |
| 4 | `/admin` con un cliente muestra la pantalla 403 | OK | 25/11 |
| 5 | Tras el rechazo, el carrito recupera los productos | OK | 25/11 |

## 4. Hallazgos abiertos

| Hallazgo | Gravedad | Detalle |
|---|---|---|
| La búsqueda no ignora las tildes | Menor | "café" encuentra el café, pero "cafe" da 0 resultados. Módulo Catálogo. Se arregla normalizando las tildes en la consulta, sin tocar el esquema |

## 5. Nota para demos

La base queda con los productos y pedidos de prueba de la checklist. Antes de una demo conviene otro `docker compose down -v`.
