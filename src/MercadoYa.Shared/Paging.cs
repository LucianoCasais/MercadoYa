namespace MercadoYa.Shared;

/// Normaliza page/pageSize de los listados paginados.
/// El tope de pagina evita que (page - 1) * pageSize desborde el int y llegue un OFFSET negativo a PostgreSQL (error 500).
public static class Paging
{
    public const int MaxPage = 10_000;

    /// page fuera de rango se lleva a [1, MaxPage]; pageSize fuera de [1, maxSize] vuelve al valor por defecto.
    public static (int Page, int PageSize) Normalize(int page, int pageSize, int defaultSize, int maxSize) =>
        (Math.Clamp(page, 1, MaxPage), pageSize < 1 || pageSize > maxSize ? defaultSize : pageSize);
}
