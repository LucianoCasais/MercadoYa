using MercadoYa.Modules.Catalogo;
using MercadoYa.Modules.Pedidos;
using Microsoft.EntityFrameworkCore;

namespace MercadoYa.Api;

/// Adaptador entre modulos: Pedidos define el puerto IProductLookup y la API lo implementa leyendo Catalogo,
/// asi Pedidos no depende del modulo Catalogo.
public sealed class CatalogProductLookup(CatalogoDbContext db) : IProductLookup
{
    public async Task<IReadOnlyDictionary<int, ProductInfo>> GetAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        var idArray = ids.ToArray();
        return await db.Products.AsNoTracking()
            .Where(p => idArray.Contains(p.Id))
            .Select(p => new ProductInfo(p.Id, p.Name, p.PriceCents, p.Active))
            .ToDictionaryAsync(p => p.Id, ct);
    }
}
