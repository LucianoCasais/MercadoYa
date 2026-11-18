using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MercadoYa.Modules.Catalogo;

public sealed class CatalogoDbContext(DbContextOptions<CatalogoDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.HasDefaultSchema("catalogo");
        m.Entity<Category>(e => { e.ToTable("Category"); e.Property(x => x.Name).HasMaxLength(80); e.HasOne<Category>().WithMany().HasForeignKey(x => x.ParentId); });
        m.Entity<Product>(e =>
        {
            e.ToTable("Product");
            e.Property(x => x.Name).HasMaxLength(150);
            e.Property<uint>("xmin").IsRowVersion();      // concurrencia optimista
            e.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId);
        });
    }
}

public sealed record ProductDto(int Id, string Name, string Description, long PriceCents, int Stock, int CategoryId, string? ImageUrl);
public sealed record CategoryDto(int Id, string Name, int? ParentId);
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public static class CatalogoModule
{
    public static IServiceCollection AddCatalogoModule(this IServiceCollection s, string connectionString) =>
        s.AddDbContext<CatalogoDbContext>(o => o.UseNpgsql(connectionString));

    /// Rutas publicas del catalogo (GET /products acepta search, categoryId, page y pageSize). TODO (dia 4-5): ABM de productos y PATCH de stock con la politica Admin; cache en memoria.
    public static IEndpointRouteBuilder MapCatalogoEndpoints(this IEndpointRouteBuilder v1)
    {
        v1.MapGet("/products", async (CatalogoDbContext db, string? search, int? categoryId, int page, int pageSize, CancellationToken ct) =>
        {
            page = Math.Max(page, 1); pageSize = pageSize is < 1 or > 50 ? 12 : pageSize;
            var q = db.Products.AsNoTracking().Where(p => p.Active);
            if (!string.IsNullOrWhiteSpace(search)) { var t = search.Trim().ToLower(); q = q.Where(p => p.Name.ToLower().Contains(t)); }
            if (categoryId is int cid)   // incluye todas las subcategorias y hojas descendientes
            {
                var all = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentId }).ToListAsync(ct);
                var ids = CategoryTree.DescendantIds(all.Select(c => (c.Id, c.ParentId)), cid);
                q = q.Where(p => ids.Contains(p.CategoryId));
            }
            var total = await q.CountAsync(ct);
            var items = await q.OrderBy(p => p.Name).Skip((page - 1) * pageSize).Take(pageSize)
                .Select(p => new ProductDto(p.Id, p.Name, p.Description, p.PriceCents, p.Stock, p.CategoryId, p.ImageUrl)).ToListAsync(ct);
            return Results.Ok(new PagedResult<ProductDto>(items, page, pageSize, total));
        }).AllowAnonymous();

        v1.MapGet("/products/{id:int}", async (int id, CatalogoDbContext db, CancellationToken ct) =>
            await db.Products.AsNoTracking().Where(p => p.Id == id && p.Active)
                .Select(p => new ProductDto(p.Id, p.Name, p.Description, p.PriceCents, p.Stock, p.CategoryId, p.ImageUrl))
                .FirstOrDefaultAsync(ct) is { } dto ? Results.Ok(dto) : Results.NotFound()).AllowAnonymous();

        v1.MapGet("/categories", async (CatalogoDbContext db, CancellationToken ct) =>
            // Lista plana con ParentId: la web arma el arbol del mega menu.
            Results.Ok(await db.Categories.AsNoTracking().OrderBy(c => c.Name).Select(c => new CategoryDto(c.Id, c.Name, c.ParentId)).ToListAsync(ct))).AllowAnonymous();
        return v1;
    }
}
