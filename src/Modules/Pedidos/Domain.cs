using MercadoYa.Shared;

namespace MercadoYa.Modules.Pedidos;

public sealed class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    /// Nombre de quien compra, copiado del token al crear el pedido (queda congelado, como los precios).
    public string BuyerName { get; set; } = "";
    public OrderStatus Status { get; private set; } = OrderStatus.Pendiente;
    public RejectionReason? Reason { get; private set; }
    public long TotalCents { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<OrderItem> Items { get; set; } = [];

    /// Maquina de estados: Pendiente -> Confirmado | Rechazado; Confirmado -> Entregado | Cancelado.
    public bool TryTransition(OrderStatus to, RejectionReason? reason = null)
    {
        var allowed = (Status, to) is (OrderStatus.Pendiente, OrderStatus.Confirmado) or (OrderStatus.Pendiente, OrderStatus.Rechazado)
            or (OrderStatus.Confirmado, OrderStatus.Entregado) or (OrderStatus.Confirmado, OrderStatus.Cancelado);
        if (!allowed || (to == OrderStatus.Rechazado) != reason.HasValue) return false;
        Status = to; Reason = reason; UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }
}

public sealed class OrderItem
{
    public Guid OrderId { get; set; } public int ProductId { get; set; }
    public string ProductName { get; set; } = ""; public long UnitPriceCents { get; set; } public int Quantity { get; set; }
}
