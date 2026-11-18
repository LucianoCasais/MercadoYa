namespace MercadoYa.Shared;

public static class Roles { public const string Admin = "admin"; public const string Cliente = "cliente"; }
public static class Policies { public const string Admin = "Admin"; public const string Cliente = "Cliente"; public const string Worker = "Worker"; }
public static class KeycloakClients { public const string OrdersWorker = "orders-worker"; }
public static class Headers { public const string CorrelationId = "X-Correlation-Id"; public const string IdempotencyKey = "Idempotency-Key"; }

public enum OrderStatus { Pendiente, Confirmado, Rechazado, Entregado, Cancelado }
public enum RejectionReason { SinStock, StockNoDisponible, PagoFallido, PagoNoDisponible, BrokerNoDisponible }

// Mensaje OrderRequested (ver contracts/order-requested.json)
public sealed record OrderItemMessage(int ProductId, int Quantity);
public sealed record OrderRequested(Guid OrderId, string UserId, IReadOnlyList<OrderItemMessage> Items, long TotalCents);
