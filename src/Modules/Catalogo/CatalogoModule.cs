using MercadoYa.Shared;
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
        // translate() de PostgreSQL para la busqueda sin tildes (ver SearchText).
        m.HasDbFunction(typeof(SearchText).GetMethod(nameof(SearchText.Translate))!).HasName("translate").IsBuiltIn();
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
    public const int MaxSearchLength = 100;

    /// DbContext del catalogo y su cache. La cache tambien se registra como ICatalogChangeNotifier: el ABM y el modulo
    /// Inventario la invalidan sin conocerla (ver CatalogCache).
    public static IServiceCollection AddCatalogoModule(this IServiceCollection s, string connectionString) =>
        s.AddDbContext<CatalogoDbContext>(o => o.UseNpgsql(connectionString))
         .AddSingleton<CatalogCache>()
         .AddSingleton<ICatalogChangeNotifier>(sp => sp.GetRequiredService<CatalogCache>());

    /// Rutas publicas del catalogo (GET /products acepta search, categoryId, page y pageSize). El ABM esta en CatalogoAdminEndpoints.
    /// Las tres lecturas pasan por CatalogCache y responden el header X-Cache: HIT (desde memoria) o MISS (desde la base).
    public static IEndpointRouteBuilder MapCatalogoEndpoints(this IEndpointRouteBuilder v1)
    {
        v1.MapGet("/products", async (CatalogoDbContext db, CatalogCache cache, HttpContext http, string? search, int? categoryId,
            CancellationToken ct, int page = 1, int pageSize = 12) =>
        {
            (page, pageSize) = Paging.Normalize(page, pageSize, defaultSize: 12, maxSize: 50);
            // Minusculas y sin tildes: "Café" y "cafe" son la misma busqueda (y comparten la entrada de la cache).
            var term = SearchText.Fold(search?.Trim() ?? "");
            if (term.Length > MaxSearchLength)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["search"] = [$"La busqueda admite hasta {MaxSearchLength} caracteres."] });

            var (result, hit) = await cache.GetOrCreateAsync($"products|{term}|{categoryId}|{page}|{pageSize}", async token =>
            {
                var q = db.Products.AsNoTracking().Where(p => p.Active).NameContains(term);
                if (categoryId is int cid)   // incluye todas las subcategorias y hojas descendientes
                {
                    var all = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentId }).ToListAsync(token);
                    var ids = CategoryTree.DescendantIds(all.Select(c => (c.Id, c.ParentId)), cid);
                    q = q.Where(p => ids.Contains(p.CategoryId));
                }
                var total = await q.CountAsync(token);
                var items = await q.OrderBy(p => p.Name).ThenBy(p => p.Id).Skip((page - 1) * pageSize).Take(pageSize)
                    .Select(p => new ProductDto(p.Id, p.Name, p.Description, p.PriceCents, p.Stock, p.CategoryId, p.ImageUrl)).ToListAsync(token);
                return new PagedResult<ProductDto>(items, page, pageSize, total);
            }, ct);
            MarkCache(http, hit);
            return Results.Ok(result);
        }).AllowAnonymous();

        v1.MapGet("/products/{id:int}", async (int id, CatalogoDbContext db, CatalogCache cache, HttpContext http, CancellationToken ct) =>
        {
            // Tambien se cachea el "no existe": un alta o una reactivacion invalidan la cache igual que cualquier otro cambio.
            var (dto, hit) = await cache.GetOrCreateAsync($"product|{id}", token => db.Products.AsNoTracking()
                .Where(p => p.Id == id && p.Active)
                .Select(p => new ProductDto(p.Id, p.Name, p.Description, p.PriceCents, p.Stock, p.CategoryId, p.ImageUrl))
                .FirstOrDefaultAsync(token), ct);
            MarkCache(http, hit);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        }).AllowAnonymous();

        v1.MapGet("/categories", async (CatalogoDbContext db, CatalogCache cache, HttpContext http, CancellationToken ct) =>
        {
            // Lista plana con ParentId (la web arma el arbol si lo necesita).
            var (list, hit) = await cache.GetOrCreateAsync("categories", token => db.Categories.AsNoTracking().OrderBy(c => c.Name)
                .Select(c => new CategoryDto(c.Id, c.Name, c.ParentId)).ToListAsync(token), ct);
            MarkCache(http, hit);
            return Results.Ok(list);
        }).AllowAnonymous();
        return v1;
    }

    private static void MarkCache(HttpContext http, bool hit) => http.Response.Headers["X-Cache"] = hit ? "HIT" : "MISS";
}
