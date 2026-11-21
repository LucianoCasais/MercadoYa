using Grpc.Core;
using MercadoYa.Payments.Grpc;
using MercadoYa.Shared;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace MercadoYa.Payments;

/// Adaptador gRPC de contracts/payments.proto. Solo traduce: valida la request, llama a PaymentProcessor y convierte
/// cada situacion en un codigo gRPC que el worker pueda distinguir (reintentar o no):
/// - InvalidArgument: order_id no es un GUID o amount_cents no es positivo (no tiene sentido reintentar).
/// - FailedPrecondition: el pedido ya se cobro por otro importe (no reintentar).
/// - Unavailable: la base de pagos no responde (transitorio: Polly puede reintentar).
/// - Unauthenticated / PermissionDenied: sin token valido / token que no es del client orders-worker.
[Authorize(Policy = Policies.Worker)]
public sealed class PaymentsService(PaymentProcessor processor, ILogger<PaymentsService> logger) : Grpc.Payments.PaymentsBase
{
    public override async Task<ChargeOrderReply> ChargeOrder(ChargeOrderRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.OrderId, out var orderId) || orderId == Guid.Empty)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "order_id debe ser un GUID valido."));
        if (request.AmountCents <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "amount_cents debe ser mayor a 0."));

        try
        {
            var r = await processor.ChargeAsync(orderId, request.AmountCents, context.CancellationToken);
            return new ChargeOrderReply { Status = r.Approved ? PaymentStatus.Aprobado : PaymentStatus.Rechazado, Reason = r.Reason ?? "" };
        }
        catch (PaymentConflictException ex)
        {
            logger.LogWarning("Cobro rechazado por importe distinto: {Message}", ex.Message);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (Exception ex) when (IsDatabaseUnavailable(ex) && !context.CancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Base de pagos no disponible al cobrar el pedido {OrderId}", orderId);
            throw new RpcException(new Status(StatusCode.Unavailable, "La base de pagos no esta disponible. Reintentar."));
        }
    }

    /// Falla de conexion o timeout con PostgreSQL (directa o envuelta por EF Core). Un error de datos no cuenta.
    private static bool IsDatabaseUnavailable(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is NpgsqlException { IsTransient: true } or TimeoutException) return true;
        return false;
    }
}
