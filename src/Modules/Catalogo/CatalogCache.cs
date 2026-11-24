using MercadoYa.Shared;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace MercadoYa.Modules.Catalogo;

/// Cache en memoria de las lecturas PUBLICAS del catalogo (listado, detalle y categorias).
/// - Invalidacion: cualquier cambio de producto o de stock llama a CatalogChanged() y TODAS las entradas vencen a la vez
///   (una "generacion": un CancellationTokenSource por generacion, atado a cada entrada).
/// - Carrera lectura/escritura: la generacion se toma ANTES de leer la base. Si mientras se leia hubo un cambio, la entrada
///   nace ya vencida y no se guarda: nunca queda cacheado un dato anterior al ultimo cambio.
/// - TTL de 60 s como red de seguridad para cambios hechos por fuera de la API (por ejemplo un UPDATE a mano en la base).
/// - Limite de 1000 entradas (cada busqueda distinta es una entrada): la memoria no crece sin control.
/// Vale para UNA instancia de la API; con varias habria que pasar a una cache distribuida (Redis) o invalidar por mensaje.
public sealed class CatalogCache : ICatalogChangeNotifier, IDisposable
{
    public static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(60);
    public const int MaxEntries = 1000;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = MaxEntries });
    private CancellationTokenSource _generation = new();

    /// Devuelve el valor cacheado (Hit = true) o lo carga con `load` y lo guarda (Hit = false).
    public async Task<(T Value, bool Hit)> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> load, CancellationToken ct)
    {
        if (_cache.TryGetValue(key, out T? cached)) return (cached!, true);

        var generation = Volatile.Read(ref _generation).Token;   // antes de leer la base (ver "Carrera" arriba)
        var value = await load(ct);
        if (!generation.IsCancellationRequested)
        {
            var options = new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeToLive };
            options.AddExpirationToken(new CancellationChangeToken(generation));
            _cache.Set(key, value, options);
        }
        return (value, false);
    }

    /// Vence todas las entradas. Se llama DESPUES de confirmar el cambio en la base.
    public void CatalogChanged() => Interlocked.Exchange(ref _generation, new CancellationTokenSource()).Cancel();

    public void Dispose() => _cache.Dispose();
}
