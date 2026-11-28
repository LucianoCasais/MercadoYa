namespace MercadoYa.Shared;

/// Marca de endpoint: exige el header Idempotency-Key. No cambia el comportamiento (el endpoint lo valida por su cuenta);
/// la lee el filtro de Swagger de la api (IdempotencyKeyOperationFilter) para documentar el header como obligatorio.
/// Se agrega con .WithMetadata(new RequiresIdempotencyKey()).
public sealed record RequiresIdempotencyKey;
