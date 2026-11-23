using System.Text;
using MercadoYa.Modules.Pedidos;
using MercadoYa.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MercadoYa.Tests;

/// Dia 9: SAGA del worker (reservar stock, cobrar, confirmar o compensar), idempotencia y mensajes a la DLQ. Sin RabbitMQ ni BD:
/// el consumidor solo traduce MessageDisposition a ack / nack, la logica esta en OrderSagaProcessor.
public class OrderSagaProcessorTests
{
    // ---- dobles de prueba ----
    private sealed class InMemoryStore : ISagaOrderStore
    {
        public Dictionary<Guid, Order> Orders { get; } = [];
        public bool FailFind { get; set; }
        public int FinalizeCalls { get; private set; }

        public Task<Order?> FindForSagaAsync(Guid orderId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailFind) throw new InvalidOperationException("BD caida");
            return Task.FromResult(Orders.GetValueOrDefault(orderId));
        }

        // Misma regla que el UPDATE ... WHERE Status = 'Pendiente' de EfOrderRepository.
        public Task<bool> TryFinalizeAsync(Guid orderId, OrderStatus to, RejectionReason? reason, CancellationToken ct)
        {
            FinalizeCalls++;
            return Task.FromResult(Orders.TryGetValue(orderId, out var o) && o.TryTransition(to, reason));
        }
    }

    private sealed class ScriptedStock : IStockClient
    {
        public StockReserveOutcome Reserve { get; set; } = StockReserveOutcome.Reserved;
        public bool ReserveThrows { get; set; }
        public bool ReleaseThrows { get; set; }
        public List<Guid> Reserved { get; } = [];
        public List<Guid> Released { get; } = [];
        public IReadOnlyList<OrderLineRequest>? LastItems { get; private set; }

        public Task<StockReserveOutcome> ReserveAsync(Guid orderId, IReadOnlyList<OrderLineRequest> items, CancellationToken ct)
        {
            if (ReserveThrows) throw new HttpRequestException("stock caido");
            Reserved.Add(orderId); LastItems = items;
            return Task.FromResult(Reserve);
        }

        public Task ReleaseAsync(Guid orderId, CancellationToken ct)
        {
            if (ReleaseThrows) throw new HttpRequestException("stock caido");
            Released.Add(orderId);
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedPayments : IPaymentGateway
    {
        public PaymentOutcome Outcome { get; set; } = PaymentOutcome.Approved;
        public bool Throws { get; set; }
        public List<(Guid OrderId, long Amount)> Charges { get; } = [];

        public Task<PaymentResult> ChargeAsync(Guid orderId, long amountCents, CancellationToken ct)
        {
            if (Throws) throw new InvalidOperationException("payments caido");
            Charges.Add((orderId, amountCents));
            return Task.FromResult(new PaymentResult(Outcome));
        }
    }

    private static Order NewOrder(InMemoryStore store, long unitPrice = 480000, int qty = 2)
    {
        var o = new Order { UserId = "u1", IdempotencyKey = Guid.NewGuid().ToString() };
        o.Items.Add(new OrderItem { OrderId = o.Id, ProductId = 1, ProductName = "Yerba", UnitPriceCents = unitPrice, Quantity = qty });
        o.TotalCents = unitPrice * qty;
        store.Orders[o.Id] = o;
        return o;
    }

    private static byte[] Message(Order o) => OrderMessages.ToBytes(o.ToRequestedMessage());

    private static (OrderSagaProcessor Saga, InMemoryStore Store, ScriptedStock Stock, ScriptedPayments Pay) Build() =>
        Build(new InMemoryStore(), new ScriptedStock(), new ScriptedPayments());

    private static (OrderSagaProcessor, InMemoryStore, ScriptedStock, ScriptedPayments) Build(InMemoryStore s, ScriptedStock st, ScriptedPayments p) =>
        (new OrderSagaProcessor(s, st, p, NullLogger<OrderSagaProcessor>.Instance), s, st, p);

    // ---- camino feliz ----

    [Fact]
    public async Task Reserva_cobra_y_confirma()
    {
        var (saga, store, stock, pay) = Build();
        var order = NewOrder(store);

        var result = await saga.HandleAsync(Message(order), CancellationToken.None);

        Assert.Equal(MessageDisposition.Ack, result);
        Assert.Equal(OrderStatus.Confirmado, order.Status);
        Assert.Null(order.Reason);
        Assert.Single(stock.Reserved);
        Assert.Empty(stock.Released);
        Assert.Single(pay.Charges);
    }

    [Fact]
    public async Task Usa_los_items_y_el_total_de_la_BD_no_los_del_mensaje()
    {
        var (saga, store, stock, pay) = Build();
        var order = NewOrder(store, unitPrice: 100000, qty: 3);
        // Mensaje manipulado: otro producto, otra cantidad y un total de 1 centavo.
        var tampered = new OrderRequested(order.Id, "u1", [new OrderItemMessage(99, 50)], 1);

        await saga.HandleAsync(OrderMessages.ToBytes(tampered), CancellationToken.None);

        Assert.Single(stock.LastItems!);
        Assert.Equal(1, stock.LastItems![0].ProductId);
        Assert.Equal(3, stock.LastItems![0].Quantity);
        Assert.Equal(300000L, pay.Charges[0].Amount);
    }

    // ---- compensaciones ----

    [Fact]
    public async Task Sin_stock_rechaza_sin_cobrar_ni_liberar()
    {
        var (saga, store, stock, pay) = Build();
        stock.Reserve = StockReserveOutcome.InsufficientStock;
        var order = NewOrder(store);

        var result = await saga.HandleAsync(Message(order), CancellationToken.None);

        Assert.Equal(MessageDisposition.Ack, result);
        Assert.Equal(OrderStatus.Rechazado, order.Status);
        Assert.Equal(RejectionReason.SinStock, order.Reason);
        Assert.Empty(pay.Charges);
        Assert.Empty(stock.Released);
    }

    [Fact]
    public async Task Stock_caido_rechaza_y_libera_por_si_la_reserva_se_aplico()
    {
        var (saga, store, stock, pay) = Build();
        stock.ReserveThrows = true;
        var order = NewOrder(store);

        var result = await saga.HandleAsync(Message(order), CancellationToken.None);

        // La reserva fallo (quiza se aplico): se rechaza y se intenta liberar; la liberacion si responde, asi que hay ack.
        Assert.Equal(OrderStatus.Rechazado, order.Status);
        Assert.Equal(RejectionReason.StockNoDisponible, order.Reason);
        Assert.Equal(MessageDisposition.Ack, result);
        Assert.Single(stock.Released);
        Assert.Empty(pay.Charges);
    }

    [Fact]
    public async Task Pago_rechazado_libera_el_stock_y_rechaza()
    {
        var (saga, store, stock, pay) = Build();
        pay.Outcome = PaymentOutcome.Rejected;
        var order = NewOrder(store);

        var result = await saga.HandleAsync(Message(order), CancellationToken.None);

        Assert.Equal(MessageDisposition.Ack, result);
        Assert.Equal(OrderStatus.Rechazado, order.Status);
        Assert.Equal(RejectionReason.PagoFallido, order.Reason);
        Assert.Equal(new[] { order.Id }, stock.Released.ToArray());
    }

    [Fact]
    public async Task Payments_caido_libera_el_stock_y_rechaza_por_pago_no_disponible()
    {
        var (saga, store, stock, pay) = Build();
        pay.Throws = true;
        var order = NewOrder(store);

        var result = await saga.HandleAsync(Message(order), CancellationToken.None);

        Assert.Equal(MessageDisposition.Ack, result);
        Assert.Equal(OrderStatus.Rechazado, order.Status);
        Assert.Equal(RejectionReason.PagoNoDisponible, order.Reason);
        Assert.Single(stock.Released);
    }

    [Fact]
    public async Task Si_falla_la_compensacion_no_se_hace_ack_y_se_reintenta()
    {
        var (saga, store, stock, pay) = Build();
        pay.Outcome = PaymentOutcome.Rejected;
        stock.ReleaseThrows = true;
        var order = NewOrder(store);

        var first = await saga.HandleAsync(Message(order), CancellationToken.None);

        Assert.Equal(MessageDisposition.Requeue, first);
        Assert.Equal(OrderStatus.Rechazado, order.Status);   // el pedido ya quedo finalizado...
        Assert.Empty(stock.Released);                         // ...pero el stock sigue reservado

        // RabbitMQ reentrega el mensaje, el stock ya responde: se completa la compensacion, sin reservar ni cobrar de nuevo.
        stock.ReleaseThrows = false;
        var second = await saga.HandleAsync(Message(order), CancellationToken.None);

        Assert.Equal(MessageDisposition.Ack, second);
        Assert.Equal(new[] { order.Id }, stock.Released.ToArray());
        Assert.Single(stock.Reserved);
        Assert.Single(pay.Charges);
        Assert.Equal(RejectionReason.PagoFallido, order.Reason);
    }

    [Fact]
    public async Task Reentrega_de_un_rechazo_por_SinStock_no_toca_el_stock()
    {
        var (saga, store, stock, _) = Build();
        stock.Reserve = StockReserveOutcome.InsufficientStock;
        var order = NewOrder(store);
        await saga.HandleAsync(Message(order), CancellationToken.None);

        var again = await saga.HandleAsync(Message(order), CancellationToken.None);

        Assert.Equal(MessageDisposition.Ack, again);
        Assert.Single(stock.Reserved);
        Assert.Empty(stock.Released);
    }

    // ---- idempotencia ----

    [Fact]
    public async Task Mensaje_duplicado_de_un_pedido_confirmado_no_reserva_ni_cobra_de_nuevo()
    {
        var (saga, store, stock, pay) = Build();
        var order = NewOrder(store);
        await saga.HandleAsync(Message(order), CancellationToken.None);

        var again = await saga.HandleAsync(Message(order), CancellationToken.None);

        Assert.Equal(MessageDisposition.Ack, again);
        Assert.Equal(OrderStatus.Confirmado, order.Status);
        Assert.Single(stock.Reserved);
        Assert.Single(pay.Charges);
        Assert.Empty(stock.Released);
    }

    [Theory]
    [InlineData(OrderStatus.Entregado)]
    [InlineData(OrderStatus.Cancelado)]
    public async Task Pedidos_ya_entregados_o_cancelados_se_ignoran(OrderStatus status)
    {
        var (saga, store, stock, pay) = Build();
        var order = NewOrder(store);
        order.TryTransition(OrderStatus.Confirmado);
        order.TryTransition(status);

        var result = await saga.HandleAsync(Message(order), CancellationToken.None);

        Assert.Equal(MessageDisposition.Ack, result);
        Assert.Equal(status, order.Status);
        Assert.Empty(stock.Reserved);
        Assert.Empty(pay.Charges);
        Assert.Empty(stock.Released);
    }

    [Fact]
    public async Task Si_otro_worker_gano_la_carrera_no_se_libera_stock_de_un_pedido_confirmado()
    {
        // Este worker lee el pedido Pendiente, pero mientras tanto otro lo confirma; despues el stock de este worker "falla".
        var store = new InMemoryStore();
        var order = NewOrder(store);
        var racing = new RacingStock(store);
        var saga = new OrderSagaProcessor(store, racing, new ScriptedPayments(), NullLogger<OrderSagaProcessor>.Instance);

        var result = await saga.HandleAsync(Message(order), CancellationToken.None);

        Assert.Equal(MessageDisposition.Ack, result);
        Assert.Equal(OrderStatus.Confirmado, order.Status);   // lo dejo el otro worker
        Assert.Equal(0, racing.Releases);                     // y este no compenso nada
    }

    private sealed class RacingStock(InMemoryStore store) : IStockClient
    {
        public int Releases { get; private set; }
        public Task<StockReserveOutcome> ReserveAsync(Guid orderId, IReadOnlyList<OrderLineRequest> items, CancellationToken ct)
        {
            store.Orders[orderId].TryTransition(OrderStatus.Confirmado);   // el otro worker termina antes
            throw new HttpRequestException("timeout");
        }
        public Task ReleaseAsync(Guid orderId, CancellationToken ct) { Releases++; return Task.CompletedTask; }
    }

    // ---- fallos transitorios y mensajes invalidos (DLQ) ----

    [Fact]
    public async Task Si_la_BD_falla_se_devuelve_a_la_cola()
    {
        var (saga, store, stock, _) = Build();
        var order = NewOrder(store);
        store.FailFind = true;

        var result = await saga.HandleAsync(Message(order), CancellationToken.None);

        Assert.Equal(MessageDisposition.Requeue, result);
        Assert.Empty(stock.Reserved);
        Assert.Equal(OrderStatus.Pendiente, order.Status);
    }

    [Fact]
    public async Task Pedido_inexistente_va_directo_a_la_cola_de_mensajes_muertos()
    {
        var (saga, _, stock, _) = Build();
        var ghost = new OrderRequested(Guid.NewGuid(), "u1", [new OrderItemMessage(1, 1)], 100);

        var result = await saga.HandleAsync(OrderMessages.ToBytes(ghost), CancellationToken.None);

        Assert.Equal(MessageDisposition.DeadLetter, result);
        Assert.Empty(stock.Reserved);
    }

    [Theory]
    [InlineData("esto no es json")]
    [InlineData("{\"orderId\": \"no-es-un-guid\"}")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task Mensajes_ilegibles_van_a_la_cola_de_mensajes_muertos(string body)
    {
        var (saga, _, stock, pay) = Build();

        var result = await saga.HandleAsync(Encoding.UTF8.GetBytes(body), CancellationToken.None);

        Assert.Equal(MessageDisposition.DeadLetter, result);
        Assert.Empty(stock.Reserved);
        Assert.Empty(pay.Charges);
    }

    [Fact]
    public async Task Si_se_apaga_el_worker_se_propaga_la_cancelacion_sin_decidir_nada()
    {
        var (saga, store, _, _) = Build();
        var order = NewOrder(store);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Ni Ack ni Requeue: el consumidor no confirma el mensaje y RabbitMQ lo reentrega al volver a arrancar.
        await Assert.ThrowsAsync<OperationCanceledException>(() => saga.HandleAsync(Message(order), cts.Token));
        Assert.Equal(OrderStatus.Pendiente, order.Status);
    }
}
