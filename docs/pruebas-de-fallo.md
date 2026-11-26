# Pruebas de fallo

Qué pasa cuando algo se cae o dos clientes compiten por lo mismo, cómo comprobarlo a mano y qué test automático lo cubre.
El comportamiento esperado de cada escenario está en [secuencia-checkout-saga.md](secuencia-checkout-saga.md) (§6, §7 y §8).

## 1. Qué está automatizado

| Escenario | Test | Qué demuestra |
|---|---|---|
| Doble compra del último stock | `LastStockSagaTests.Dos_compras_del_ultimo_stock_...` | Dos pedidos procesados a la vez por la SAGA real sobre PostgreSQL: uno Confirmado, otro Rechazado (SinStock), stock en 0 |
| Muchos compradores, poco stock | `LastStockSagaTests.Veinte_pedidos_sobre_cinco_unidades_...` | 20 pedidos sobre 5 unidades: exactamente 5 Confirmados y el stock nunca queda negativo |
| Pago rechazado | `LastStockSagaTests.El_pago_rechazado_devuelve_el_stock_reservado` | La compensación devuelve el stock y la reserva queda `Liberada` |
| Payments caído | `LastStockSagaTests.Con_Payments_caido_...` | Pedido Rechazado (PagoNoDisponible) y stock liberado |
| Mensaje duplicado | `LastStockSagaTests.El_mismo_mensaje_procesado_a_la_vez_...` y `..._reentregado_despues_de_confirmar_...` | El stock se descuenta una sola vez, aunque dos workers procesen el mismo pedido |
| Worker apagado | `OrderSagaRabbitTests.Un_pedido_publicado_con_el_worker_apagado_...` | La cola durable conserva el mensaje y se procesa al arrancar |
| Worker que cae a mitad de un pedido | `OrderSagaRabbitTests.Si_el_worker_se_apaga_a_mitad_...` | Sin ack, RabbitMQ devuelve el mensaje a la cola y otra réplica lo termina |
| Dos réplicas del worker | `OrderSagaRabbitTests.Dos_workers_reparten_...` | Consumidores competidores: cada pedido se procesa una sola vez |
| Mensaje ilegible | `OrderSagaRabbitTests.Un_mensaje_ilegible_...` | Va directo a `orders.dead`, sin reintentos |
| Compensación que nunca funciona | `OrderSagaRabbitTests.Si_la_compensacion_nunca_funciona_...` | Reintentos hasta `x-delivery-limit` y después `orders.dead` |
| RabbitMQ caído al crear el pedido | `OrderSagaRabbitTests.Con_RabbitMQ_caido_...` y `CheckoutAsyncTests` | El publisher falla enseguida (sin colgar la request); el checkout responde 503 y deja el pedido Rechazado (BrokerNoDisponible) |
| Stock o Payments lentos o caídos | `ResilienceTests`, `OrderSagaProcessorTests` | Reintentos, circuit breaker y mapeo a `StockNoDisponible` / `PagoNoDisponible` |

Los tests con `Category=Integracion` usan contenedores descartables (PostgreSQL y RabbitMQ con Testcontainers), así que necesitan Docker.
Para correr solo los que no lo necesitan: `dotnet test --filter "Category!=Integracion"`.

## 2. Pruebas manuales con todo el sistema levantado

Requisito: `docker compose up -d --build` con el sistema sano (`docker compose ps` muestra todo `healthy`).
Los pedidos se crean desde la web (`http://localhost:4200`, usuario `cliente@mercadoya.local` / `Cliente123!`); el stock y los estados se ven con el panel admin (`admin@mercadoya.local` / `Admin123!`) o con SQL:

```powershell
"SELECT left(""Id""::text,8) AS pedido, ""Status"", ""Reason"" FROM pedidos.""Order"" ORDER BY ""CreatedAt"" DESC LIMIT 5;" | docker compose exec -T postgres psql -U mercadoya -d mercadoya
```

RabbitMQ Management (`http://localhost:15672`, `mercadoya` / `mercadoya_dev`) muestra las colas `orders` y `orders.dead`.
Los logs de los workers: `docker compose logs -f worker`.

Después de cada prueba, dejar el sistema sano antes de la siguiente (`docker compose up -d`).

### 2.1 RabbitMQ caído

1. `docker compose stop rabbitmq`
2. En la web, confirmar un pedido.
3. **Esperado:** error claro ("No pudimos enviar el pedido a procesar"), la API responde `503`, el pedido figura `Rechazado / BrokerNoDisponible`, el stock no cambia y el catálogo se sigue viendo (la lectura no depende de RabbitMQ).
4. En los logs del worker aparece "No se pudo consumir la cola ... se reintenta en 5 s".
5. `docker compose start rabbitmq`
6. **Esperado:** el worker vuelve a conectarse solo ("consumiendo la cola orders") y un pedido nuevo se confirma, sin reiniciar nada.

### 2.2 Payments caído

1. `docker compose stop payments`
2. En la web, confirmar un pedido.
3. **Esperado:** el pedido queda `Pendiente` unos segundos (reintentos con espera creciente) y termina `Rechazado / PagoNoDisponible`; el stock vuelve a su valor original; la pantalla de seguimiento muestra el rechazo y el carrito recupera los productos.
4. Repetir con 5 o más pedidos seguidos: en los logs aparece el circuit breaker abierto (`BrokenCircuitException`) y los pedidos siguientes se rechazan al instante, sin esperar los timeouts.
5. `docker compose start payments`
6. **Esperado:** pasados unos 30 s (el breaker prueba de nuevo) los pedidos vuelven a confirmarse.

### 2.3 Worker caído

1. `docker compose stop worker`
2. Crear 3 pedidos desde la web: quedan `Pendiente` y la pantalla de seguimiento avisa que sigue en proceso. En Management, la cola `orders` muestra 3 mensajes.
3. `docker compose up -d worker`
4. **Esperado:** los 3 pedidos pasan a `Confirmado` y la cola queda en 0.

### 2.4 Stock (API interna) caído

1. `docker compose stop worker` y crear un pedido desde la web: queda `Pendiente` en la cola.
2. `docker compose stop api` y después `docker compose up -d worker` (si la API no está, el worker espera a que esté sana: levantarlo igual con `docker compose up -d --no-deps worker`).
3. **Esperado:** el worker no puede reservar stock; en los logs se ve el reintento con espera creciente, el circuit breaker del stock y el mensaje que vuelve a la cola entre intentos.
4. Si la API vuelve a tiempo (`docker compose start api`, antes de unos 50 s) el pedido se confirma. Si no, termina `Rechazado / StockNoDisponible` sin stock descontado.

### 2.5 Doble compra del último stock

1. En el panel admin dejar un producto con **stock 1**.
2. Abrir dos sesiones del navegador (una normal y otra privada) con el mismo producto en el carrito y confirmar los dos pedidos casi a la vez.
3. **Esperado:** un pedido `Confirmado` y el otro `Rechazado / SinStock`; el stock del producto queda en 0 (nunca negativo).

### 2.6 Dos réplicas del worker

1. `docker compose ps worker` muestra dos contenedores.
2. En Management, en la pestaña *Consumers* de la cola `orders` aparecen dos consumidores (`mercadoya-worker-<id>`).
3. Crear varios pedidos seguidos: los logs de ambos workers muestran pedidos distintos y ninguno se procesa dos veces.

## 3. Cómo leer `orders.dead`

La cola `orders.dead` debería estar siempre en 0. Si aparece un mensaje, es un caso que la SAGA no pudo resolver sola:

- JSON ilegible o pedido inexistente: se descarta tras revisar el contenido en Management (*Get messages*).
- Compensación que falló 5 veces: el pedido figura `Rechazado`, pero puede haber stock reservado sin devolver. Se corrige liberando la reserva con el endpoint interno de stock (`POST /internal/v1/stock/reservations/{orderId}/release`, requiere el token de servicio `orders-worker`) y se descarta el mensaje.
