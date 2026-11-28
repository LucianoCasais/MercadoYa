using MercadoYa.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace MercadoYa.Modules.Catalogo;

/// ABM de productos: /api/v1/admin/products/** (solo rol admin; sin token 401, sin rol 403).
/// El ajuste de stock (PATCH /admin/products/{id}/stock) vive en el modulo Inventario porque registra un StockMovement.
public static class CatalogoAdminEndpoints
{
    public static IEndpointRouteBuilder MapCatalogoAdminEndpoints(this IEndpointRouteBuilder v1)
    {
        var g = v1.MapGroup("/admin/products").RequireAuthorization(Policies.Admin).WithTags("Admin - Productos");

        // Listado del panel: incluye inactivos (para poder reactivarlos) y busca por nombre.
        g.MapGet("", async (CatalogoDbContext db, string? search, CancellationToken ct, int page = 1, int pageSize = 20) =>
        {
            (page, pageSize) = Paging.Normalize(page, pageSize, defaultSize: 20, maxSize: 100);
            var q = db.Products.AsNoTracking().NameContains(search ?? "");
            var total = await q.CountAsync(ct);
            var items = await q.OrderBy(p => p.Name).ThenBy(p => p.Id).Skip((page - 1) * pageSize).Take(pageSize)
                .Select(p => new AdminProductDto(p.Id, p.Name, p.Description, p.PriceCents, p.Stock, p.CategoryId, p.ImageUrl, p.Active))
                .ToListAsync(ct);
            return Results.Ok(new PagedResult<AdminProductDto>(items, page, pageSize, total));
        }).WithSummary("Listar productos (admin)")
          .WithDescription("Incluye los productos inactivos. `search` busca por nombre sin distinguir mayúsculas ni tildes; `pageSize` admite hasta 100.")
          .Produces<PagedResult<AdminProductDto>>(StatusCodes.Status200OK);

        g.MapGet("/{id:int}", async (int id, CatalogoDbContext db, CancellationToken ct) =>
            await db.Products.AsNoTracking().Where(p => p.Id == id)
                .Select(p => new AdminProductDto(p.Id, p.Name, p.Description, p.PriceCents, p.Stock, p.CategoryId, p.ImageUrl, p.Active))
                .FirstOrDefaultAsync(ct) is { } dto ? Results.Ok(dto) : NotFound(id))
            .WithSummary("Ver un producto (admin)")
            .Produces<AdminProductDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Alta: producto + movimiento de stock 'Inicial' en una sola transaccion.
        g.MapPost("", async (CreateProductRequest r, CatalogoDbContext db, ICatalogChangeNotifier catalog, CancellationToken ct) =>
        {
            var errors = ProductRules.ValidateCreate(r);
            if (errors.Count == 0 && !await db.Categories.AnyAsync(c => c.Id == r.CategoryId, ct))
                errors["categoryId"] = ["La categoria no existe."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var p = new Product { Stock = r.InitialStock, Active = true };
            ProductRules.Apply(p, r.Name, r.Description, r.PriceCents, r.CategoryId, r.ImageUrl);

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Products.Add(p);
            await db.SaveChangesAsync(ct);
            if (p.Stock > 0)
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""INSERT INTO catalogo."StockMovement" ("ProductId","Delta","Reason") VALUES ({p.Id}, {p.Stock}, 'Inicial')""", ct);
            await tx.CommitAsync(ct);
            catalog.CatalogChanged();
            return Results.Created($"/api/v1/admin/products/{p.Id}", ToDto(p));
        }).WithSummary("Crear un producto")
          .WithDescription("Crea el producto activo y registra su stock inicial como movimiento 'Inicial', en una sola transacción. " +
                           "Responde 201 con el header `Location`.")
          .Produces<AdminProductDto>(StatusCodes.Status201Created)
          .ProducesValidationProblem();

        // Edicion de datos (no de stock). Concurrencia optimista con xmin: si otro cambio pisa la fila en el medio, 409.
        g.MapPut("/{id:int}", async (int id, UpdateProductRequest r, CatalogoDbContext db, ICatalogChangeNotifier catalog, CancellationToken ct) =>
        {
            var errors = ProductRules.Validate(r.Name, r.Description, r.PriceCents, r.CategoryId, r.ImageUrl);
            if (errors.Count == 0 && !await db.Categories.AnyAsync(c => c.Id == r.CategoryId, ct))
                errors["categoryId"] = ["La categoria no existe."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var p = await db.Products.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null) return NotFound(id);
            ProductRules.Apply(p, r.Name, r.Description, r.PriceCents, r.CategoryId, r.ImageUrl);
            p.Active = r.Active;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "El producto cambio mientras se editaba",
                    detail: "Volve a cargar el producto y reintenta.");
            }
            catalog.CatalogChanged();
            return Results.Ok(ToDto(p));
        }).WithSummary("Editar un producto")
          .WithDescription("Cambia los datos del producto (no su stock; para eso está el ajuste de stock). Usa concurrencia optimista: si otro cambio pisó el producto en el medio, responde 409.")
          .Produces<AdminProductDto>(StatusCodes.Status200OK)
          .ProducesValidationProblem()
          .ProducesProblem(StatusCodes.Status404NotFound)
          .ProducesProblem(StatusCodes.Status409Conflict);

        // Baja logica (Active = false). Idempotente: dar de baja un producto ya inactivo tambien responde 204.
        // Los pedidos guardan nombre y precio congelados, asi que el historial no se rompe.
        g.MapDelete("/{id:int}", async (int id, CatalogoDbContext db, ICatalogChangeNotifier catalog, CancellationToken ct) =>
        {
            if (await db.Products.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Active, false), ct) == 0)
                return NotFound(id);
            catalog.CatalogChanged();
            return Results.NoContent();
        }).WithSummary("Dar de baja un producto")
          .WithDescription("Baja lógica (deja el producto inactivo). Es idempotente: repetirla también responde 204. Los pedidos existentes no se modifican.")
          .Produces(StatusCodes.Status204NoContent)
          .ProducesProblem(StatusCodes.Status404NotFound);

        return v1;
    }

    private static AdminProductDto ToDto(Product p) => new(p.Id, p.Name, p.Description, p.PriceCents, p.Stock, p.CategoryId, p.ImageUrl, p.Active);

    private static IResult NotFound(int id) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Producto no encontrado", detail: $"No existe el producto {id}.");
}
