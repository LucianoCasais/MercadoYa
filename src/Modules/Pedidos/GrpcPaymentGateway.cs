using Grpc.Core;
using Grpc.Net.ClientFactory;
using MercadoYa.Payments.Grpc;
using MercadoYa.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PaymentsGrpc = MercadoYa.Payments.Grpc.Payments;

namespace MercadoYa.Modules.Pedidos;

/// El servicio de pagos respondio un error que NO se arregla reintentando (credenciales, datos o contrato mal usados).
/// No es transitorio: ResilientPaymentGateway no lo reintenta ni lo cuenta para el circuit breaker y el pedido queda
/// Rechazado (PagoNoDisponible) con el stock liberado. Indica un error de configuracion o de programacion: se loguea como error.
public sealed class PaymentGatewayException(StatusCode code, string detail)
    : Exception($"El servicio de pagos respondio {code}: {detail}")
{
    public StatusCode Code { get; } = code;
}

/// Cliente gRPC real de Payments.ChargeOrder (contracts/payments.proto), con el token de servicio de `orders-worker`
/// (Keycloak, client credentials). Misma interfaz que FakePaymentGateway. Traduce la respuesta a lo que entiende la SAGA:
/// - APROBADO / RECHAZADO                         -> Approved / Rejected (resultados de negocio, no se reintentan).
/// - UNAVAILABLE, DEADLINE_EXCEEDED, RESOURCE_EXHAUSTED, ABORTED, INTERNAL, UNKNOWN
///                                                -> Unavailable (transitorio: Polly reintenta y cuenta para el breaker).
/// - INVALID_ARGUMENT, FAILED_PRECONDITION, UNAUTHENTICATED, PERMISSION_DENIED, UNIMPLEMENTED, NOT_FOUND...
///                                                -> PaymentGatewayException (no transitorio: no se reintenta).
/// El timeout por intento lo pone Polly (5 s); por eso el canal lanza OperationCanceledException al cancelarse (ver Add...).
public sealed class GrpcPaymentGateway(PaymentsGrpc.PaymentsClient client, ServiceTokenProvider tokens, ILogger<GrpcPaymentGateway> logger)
    : IPaymentGateway
{
    public async Task<PaymentResult> ChargeAsync(Guid orderId, long amountCents, CancellationToken ct)
    {
        var headers = new Metadata { { "Authorization", $"Bearer {await tokens.GetTokenAsync(ct)}" } };
        ChargeOrderReply reply;
        try
        {
            reply = await client.ChargeOrderAsync(new ChargeOrderRequest { OrderId = orderId.ToString(), AmountCents = amountCents },
                headers, cancellationToken: ct);
        }
        catch (RpcException ex) when (IsTransient(ex.StatusCode))
        {
            logger.LogWarning("Pagos no disponible para el pedido {OrderId}: {Code} {Detail}", orderId, ex.StatusCode, ex.Status.Detail);
            return new PaymentResult(PaymentOutcome.Unavailable, "Servicio de pagos no disponible");
        }
        catch (RpcException ex)
        {
            logger.LogError("Pagos rechazo la llamada del pedido {OrderId}: {Code} {Detail}", orderId, ex.StatusCode, ex.Status.Detail);
            throw new PaymentGatewayException(ex.StatusCode, ex.Status.Detail);
        }

        return reply.Status switch
        {
            PaymentStatus.Aprobado => new PaymentResult(PaymentOutcome.Approved),
            PaymentStatus.Rechazado => new PaymentResult(PaymentOutcome.Rejected, string.IsNullOrEmpty(reply.Reason) ? "Pago rechazado" : reply.Reason),
            _ => throw new PaymentGatewayException(StatusCode.Internal, $"Estado de pago desconocido: {reply.Status}"),
        };
    }

    /// Codigos que pueden andar en el proximo intento (servicio caido, sobrecargado o lento).
    public static bool IsTransient(StatusCode code) => code is StatusCode.Unavailable or StatusCode.DeadlineExceeded
        or StatusCode.ResourceExhausted or StatusCode.Aborted or StatusCode.Internal or StatusCode.Unknown;
}

public static class GrpcPaymentGatewayExtensions
{
    /// Registra el cliente gRPC real de pagos protegido con Polly (reemplaza al FakePaymentGateway). Se llama DESPUES de
    /// AddPedidosModule. Config: Payments:GrpcUrl (local http://localhost:5003; en Docker http://payments:8081) y Keycloak:*
    /// para el token de servicio (ver ServiceTokenProvider).
    public static IServiceCollection AddGrpcPaymentGateway(this IServiceCollection services, IConfiguration config)
    {
        var url = config["Payments:GrpcUrl"] ?? throw new InvalidOperationException("Falta Payments:GrpcUrl.");
        services.AddServiceTokenProvider(config);
        services.AddGrpcClient<PaymentsGrpc.PaymentsClient>(o => o.Address = new Uri(url))
            // Al cancelarse el token (timeout de Polly o apagado) el cliente lanza OperationCanceledException y no
            // RpcException(Cancelled): asi Polly lo reconoce como SU timeout, lo reintenta y lo cuenta para el breaker.
            .ConfigureChannel(c => c.ThrowOperationCanceledOnCancellation = true);
        return services.AddResilientPaymentGateway<GrpcPaymentGateway>();
    }
}
