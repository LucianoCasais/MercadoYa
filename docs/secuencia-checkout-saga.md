# MercadoYa — Secuencia del checkout con SAGA y compensaciones

> **Estado:** propuesta de diseño (borrador v2, alineada con el Acta de Constitución).
> **Cambios v2:** se agrega el escenario de falla de la reserva de stock (`StockNoDisponible`), el de falla al confirmar tras cobrar y las pruebas 9 y 10.
> **Documentos relacionados:** [`arquitectura-general.md`](./arquitectura-general.md) · carpeta `contracts/`.

## 1. Qué resuelve este flujo

Comprar en MercadoYa involucra tres recursos que viven en lugares distintos: el **pedido** (módulo Pedidos), el **stock** (módulo Inventario) y el **cobro** (servicio `payments`, con su propia base de datos). No hay una transacción de base de datos que abarque los tres, así que la consistencia se logra con una **SAGA**: una secuencia de pasos locales, cada uno con una acción de **compensación** que deshace sus efectos si un paso posterior falla.

**Tipo de SAGA: orquestada.** El `worker` es el orquestador: conoce el orden de los pasos y decide qué compensar. Se eligió orquestación (y no coreografía por eventos) porque el flujo es corto y lineal, el estado queda centralizado y es más fácil de probar y de explicar en la defensa.

## 2. Participantes

| Participante | Rol en la SAGA |
|---|---|
| **Cliente (web)** | Inicia la compra y consulta el estado por polling |
| **api** | Crea el pedido Pendiente, publica el mensaje y expone el stock interno |
| **BD pedidos** | Guarda el pedido y su estado (esquema `pedidos`) |
| **RabbitMQ** | Entrega `OrderRequested` de forma durable |
| **worker** | Orquestador: ejecuta pasos y compensaciones |
| **payments** | Cobro simulado idempotente por gRPC |

## 3. Pasos, compensaciones e idempotencia

| Paso | Acción | Compensación | Idempotencia |
|---|---|---|---|
| 1 | Crear el pedido en estado Pendiente y publicar `OrderRequested` | Marcar el pedido Rechazado | Header `Idempotency-Key` (índice único por usuario y clave) |
| 2 | Reservar stock (todos los ítems en una sola transacción) | Liberar la reserva | `StockReservation` única por `orderId` y producto: repetir devuelve 200 sin descontar de nuevo |
| 3 | Cobrar por gRPC (**punto de no retorno**) | No hay reembolso en el pago simulado | `PaymentRecord` único por `orderId`: repetir devuelve el mismo resultado |
| 4 | Confirmar el pedido | — | La actualización de estado solo aplica si el pedido sigue Pendiente |

Una vez que el pago está aprobado se pasa el *pivote* de la SAGA: a partir de ahí ya no se compensa hacia atrás, solo se reintenta hacia adelante hasta confirmar. Como el cobro es idempotente, reintentar es seguro.

## 4. Diagrama de secuencia completo

Incluye el camino feliz y las compensaciones posibles dentro del worker (sin stock, `api` de stock no disponible, pago rechazado y `payments` no disponible).

```mermaid
sequenceDiagram
    autonumber
    actor C as Cliente (web)
    participant API as api
    participant DB as BD pedidos
    participant MQ as RabbitMQ
    participant W as worker (orquestador)
    participant STK as api (stock interno)
    participant PAY as payments (gRPC)

    C->>API: POST /api/v1/orders (Idempotency-Key)
    API->>DB: crear pedido Pendiente y commit
    API->>MQ: publicar OrderRequested (publisher confirms)
    API-->>C: 202 Accepted (orderId)

    loop polling cada 1 s (hasta estado final o 30 s)
        C->>API: GET /api/v1/orders/orderId
        API-->>C: estado actual
    end

    MQ->>W: entregar OrderRequested (prefetch 1)
    W->>DB: leer pedido
    Note over W,DB: Si el pedido ya no esta Pendiente - ack y fin (mensaje duplicado)

    W->>STK: POST reservar stock (orderId, items) con token de servicio
    alt Sin stock suficiente
        STK-->>W: 409 Conflict
        W->>DB: pedido Rechazado (SinStock)
        W->>MQ: ack
        Note over W,PAY: No se cobra y no hay nada que compensar
    else api de stock no disponible (timeout o caida)
        W--xSTK: timeout y reintentos agotados
        Note over W,STK: Se intenta liberar por si la reserva se aplico (idempotente). Si tambien falla, aplica 6.3
        W->>STK: POST liberar reserva (orderId)
        W->>DB: pedido Rechazado (StockNoDisponible)
        W->>MQ: ack
    else Stock reservado
        STK-->>W: 200 OK
        W->>PAY: ChargeOrder (orderId, amountCents) con deadline 5 s y Polly
        alt Pago aprobado
            PAY-->>W: Aprobado
            W->>DB: pedido Confirmado
            W->>MQ: ack
        else Pago rechazado
            PAY-->>W: Rechazado (motivo)
            Note over W,STK: COMPENSACION - liberar stock
            W->>STK: POST liberar reserva (orderId)
            STK-->>W: 200 OK
            W->>DB: pedido Rechazado (PagoFallido)
            W->>MQ: ack
        else Payments caido o lento
            W--xPAY: timeout y reintentos agotados o circuit breaker abierto
            Note over W,STK: COMPENSACION - liberar stock
            W->>STK: POST liberar reserva (orderId)
            STK-->>W: 200 OK
            W->>DB: pedido Rechazado (PagoNoDisponible)
            W->>MQ: ack
        end
    end

    C->>API: GET /api/v1/orders/orderId
    API-->>C: Confirmado o Rechazado (con motivo)
```

## 5. Estados del pedido

```mermaid
stateDiagram-v2
    [*] --> Pendiente: POST /orders
    Pendiente --> Confirmado: pago aprobado
    Pendiente --> Rechazado: SinStock / StockNoDisponible / PagoFallido / PagoNoDisponible / BrokerNoDisponible
    Confirmado --> Entregado: admin
    Confirmado --> Cancelado: admin (libera el stock)
    Rechazado --> [*]
    Entregado --> [*]
    Cancelado --> [*]
```

La cancelación por parte del administrador no pasa por la SAGA: la `api` libera el stock directamente con la misma operación idempotente de liberación.

## 6. Fallos de infraestructura

### 6.1 RabbitMQ caído al crear el pedido

```mermaid
sequenceDiagram
    autonumber
    actor C as Cliente (web)
    participant API as api
    participant DB as BD pedidos
    participant MQ as RabbitMQ

    C->>API: POST /api/v1/orders
    API->>DB: crear pedido Pendiente y commit
    API--xMQ: publicar OrderRequested (falla)
    API->>DB: pedido Rechazado (BrokerNoDisponible)
    API-->>C: 503 Service Unavailable
```

No queda un pedido huérfano: se marca Rechazado y el cliente recibe un error claro para reintentar.

### 6.2 El worker cae a mitad del procesamiento (reentrega)

```mermaid
sequenceDiagram
    autonumber
    participant MQ as RabbitMQ
    participant W1 as worker 1
    participant W2 as worker 2
    participant DB as BD pedidos
    participant STK as api (stock interno)
    participant PAY as payments (gRPC)

    MQ->>W1: entregar OrderRequested
    W1->>STK: reservar stock (orderId)
    STK-->>W1: 200 OK
    Note over W1: El worker cae antes de enviar el ack
    MQ->>W2: reentregar OrderRequested (sin ack previo)
    W2->>DB: leer pedido (sigue Pendiente)
    W2->>STK: reservar stock (orderId)
    STK-->>W2: 200 OK (ya estaba reservado, no descuenta de nuevo)
    W2->>PAY: ChargeOrder (orderId)
    PAY-->>W2: Aprobado (cobro unico por orderId)
    W2->>DB: pedido Confirmado
    W2->>MQ: ack
```

Si el mismo mensaje llegara de nuevo con el pedido ya Confirmado, el worker lo reconoce por el estado, envía el ack y termina sin repetir efectos.

### 6.3 Falla de la propia compensación

Si la liberación de stock falla (por ejemplo, `api` no disponible momentáneamente), el worker **no confirma** el mensaje: Polly reintenta y, si persiste, el mensaje se reencola. Como la liberación es idempotente, repetirla es seguro. Tras un número máximo de reentregas el mensaje va al DLX y a la cola `.dead` para intervención manual, y el pedido permanece Pendiente (nunca queda en un estado final falso).

## 7. Tabla resumen de escenarios

| Escenario | Qué ocurre | Compensación | Estado final |
|---|---|---|---|
| Camino feliz | Reserva y cobro correctos | — | **Confirmado** |
| Sin stock | La reserva responde 409 | Nada (no se reservó ni cobró) | **Rechazado** (SinStock) |
| `api` de stock caída o lenta | Timeout y reintentos agotados al reservar | Liberar (idempotente) por si la reserva se aplicó | **Rechazado** (StockNoDisponible) |
| Pago rechazado | `payments` devuelve Rechazado | Liberar stock | **Rechazado** (PagoFallido) |
| `payments` caído o lento | Timeout, reintentos y circuit breaker | Liberar stock | **Rechazado** (PagoNoDisponible) |
| RabbitMQ caído al crear | La publicación falla | Marcar el pedido Rechazado | **Rechazado** (BrokerNoDisponible), 503 al cliente |
| Worker o RabbitMQ caídos tras el 202 | Cola durable conserva el mensaje | — | **Pendiente**, luego se resuelve |
| Falla al confirmar tras cobrar | La actualización a Confirmado falla (BD no disponible) | Ninguna: se reintenta hacia adelante | **Pendiente** y luego **Confirmado**: el mensaje se reentrega y el cobro idempotente devuelve el mismo resultado |
| Mensaje duplicado | Idempotencia por estado, reserva y pago | — | Se procesa una sola vez |
| Dos compras del último stock | Reserva atómica: una gana | Ninguna para la ganadora | Una **Confirmado**, la otra **Rechazado** (SinStock) |
| Falla la compensación | Reintentos y, luego, DLX | — | **Pendiente** hasta intervención |

## 8. Resiliencia aplicada a las llamadas del worker

| Parámetro | Valor propuesto |
|---|---|
| Timeout / deadline por llamada | 5 s |
| Reintentos | 3, con backoff exponencial y jitter |
| Circuit breaker | Se abre tras 5 fallos en 30 s; permanece abierto 30 s |
| Ack del mensaje | Manual, solo al llegar a un estado final del flujo |
| Consumo | `prefetch 1`; réplicas del worker como consumidores competidores |

## 9. Consistencia

- **Stock:** consistente. La reserva usa una actualización atómica del tipo `UPDATE ... SET stock = stock - n WHERE stock >= n` dentro de una transacción, de modo que nunca se vende dos veces el mismo stock.
- **Resultado del pedido:** eventualmente consistente. Entre el `202 Accepted` (paso 4) y el estado final hay una ventana en la que el pedido figura Pendiente. La web la cubre con polling.
- **Prioridad ante particiones:** el diseño prefiere la **integridad del stock y la coherencia final del pedido** antes que dar una respuesta inmediata; por eso el checkout es asíncrono y ante fallos rechaza o espera en lugar de aceptar ventas sin respaldo.

## 10. Limitaciones conocidas y mejoras

| Limitación | Detalle | Mejora posible |
|---|---|---|
| Ventana entre commit y publicación | Si la `api` cae justo después de guardar el pedido y antes de publicar, el pedido queda Pendiente sin mensaje | Patrón **Outbox** o un job que republique pedidos Pendiente antiguos |
| Sin reembolso | El pago es simulado, así que no existe compensación del cobro | Paso de reembolso en `payments` si el pago fuese real |
| Orquestador único lógico | Todo el conocimiento del flujo está en el worker | Aceptable por la simplicidad; es la contracara de elegir orquestación |

## 11. Pruebas sugeridas (para el día 12 del plan)

1. Compra exitosa de punta a punta.
2. Compra de un producto sin stock.
3. Pago rechazado: verificar que el stock vuelve a su valor original.
4. Detener `payments` durante una compra: verificar reintentos, circuit breaker, compensación y estado `PagoNoDisponible`.
5. Detener RabbitMQ y crear un pedido: verificar el 503 y el estado `BrokerNoDisponible`.
6. Detener el worker, crear pedidos y volver a levantarlo: verificar que se procesan.
7. Dos compras simultáneas del último stock: solo una se confirma.
8. Reenviar el mismo mensaje: verificar que no hay doble descuento ni doble cobro.
9. Detener `api` (o bloquear `/internal/v1/stock`) durante el procesamiento: verificar reintentos y estado `StockNoDisponible` sin stock descontado.
10. Provocar un fallo al confirmar el pedido tras el cobro: verificar que el mensaje se reentrega y termina Confirmado sin doble cobro.
