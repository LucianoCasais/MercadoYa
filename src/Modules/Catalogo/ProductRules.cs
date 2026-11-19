namespace MercadoYa.Modules.Catalogo;

// ---- DTOs del ABM de productos (rol admin) ----
/// Alta: el stock inicial se registra tambien como StockMovement 'Inicial'.
public sealed record CreateProductRequest(string? Name, string? Description, long PriceCents, int CategoryId, string? ImageUrl, int InitialStock);
/// Edicion: el stock NO se edita aca (solo por PATCH .../stock, que deja registro). Active permite reactivar un producto dado de baja.
public sealed record UpdateProductRequest(string? Name, string? Description, long PriceCents, int CategoryId, string? ImageUrl, bool Active = true);
public sealed record AdminProductDto(int Id, string Name, string Description, long PriceCents, int Stock, int CategoryId, string? ImageUrl, bool Active);

/// Reglas de validacion del producto (las mismas que los CHECK de 02-mercadoya-schema.sql). Funcion pura: se prueba con xUnit.
public static class ProductRules
{
    public const int MaxNameLength = 150;
    public const int MaxDescriptionLength = 2000;
    public const int MaxImageUrlLength = 500;
    public const long MaxPriceCents = 100_000_000_00;   // $ 100.000.000
    public const int MaxStock = 100_000;

    /// Devuelve los errores por campo (vacio si es valido), en el formato de ValidationProblem.
    public static Dictionary<string, string[]> Validate(string? name, string? description, long priceCents, int categoryId, string? imageUrl)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name)) errors["name"] = ["El nombre es obligatorio."];
        else if (name.Trim().Length > MaxNameLength) errors["name"] = [$"El nombre admite hasta {MaxNameLength} caracteres."];

        if (description is { Length: > MaxDescriptionLength }) errors["description"] = [$"La descripcion admite hasta {MaxDescriptionLength} caracteres."];
        if (priceCents <= 0 || priceCents > MaxPriceCents) errors["priceCents"] = ["El precio debe ser mayor a 0."];
        if (categoryId <= 0) errors["categoryId"] = ["La categoria es obligatoria."];

        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            var url = imageUrl.Trim();
            if (url.Length > MaxImageUrlLength) errors["imageUrl"] = [$"La URL admite hasta {MaxImageUrlLength} caracteres."];
            else if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
                errors["imageUrl"] = ["La imagen debe ser una URL http o https."];
        }
        return errors;
    }

    public static Dictionary<string, string[]> ValidateCreate(CreateProductRequest r)
    {
        var errors = Validate(r.Name, r.Description, r.PriceCents, r.CategoryId, r.ImageUrl);
        if (r.InitialStock < 0 || r.InitialStock > MaxStock) errors["initialStock"] = [$"El stock inicial debe estar entre 0 y {MaxStock}."];
        return errors;
    }

    /// Copia los campos editables ya normalizados (trim, URL vacia = null).
    public static void Apply(Product p, string? name, string? description, long priceCents, int categoryId, string? imageUrl)
    {
        p.Name = name!.Trim();
        p.Description = description?.Trim() ?? "";
        p.PriceCents = priceCents;
        p.CategoryId = categoryId;
        p.ImageUrl = string.IsNullOrWhiteSpace(imageUrl) ? null : imageUrl.Trim();
    }
}
