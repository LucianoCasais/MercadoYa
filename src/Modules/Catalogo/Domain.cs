namespace MercadoYa.Modules.Catalogo;

public sealed class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }   // null = categoria raiz (arbol Categoria > Subcategoria > Hoja)
}

public static class CategoryTree
{
    /// Id de la categoria y de todos sus descendientes (para filtrar por una categoria raiz o subcategoria).
    public static HashSet<int> DescendantIds(IEnumerable<(int Id, int? ParentId)> all, int rootId)
    {
        var children = all.Where(c => c.ParentId.HasValue).ToLookup(c => c.ParentId!.Value, c => c.Id);
        var result = new HashSet<int> { rootId };
        var queue = new Queue<int>([rootId]);
        while (queue.Count > 0)
            foreach (var child in children[queue.Dequeue()])
                if (result.Add(child)) queue.Enqueue(child);
        return result;
    }
}

public sealed class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public long PriceCents { get; set; }
    public int Stock { get; set; }
    public int CategoryId { get; set; }
    public string? ImageUrl { get; set; }
    public bool Active { get; set; } = true;   // baja logica
}
