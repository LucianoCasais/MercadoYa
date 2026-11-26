namespace MercadoYa.Modules.Catalogo;

/// Busqueda por nombre que ignora mayusculas y tildes: "cafe" encuentra "Café" y "CAFÉ" encuentra "cafe".
/// Del lado de la base se usa translate() de PostgreSQL (funcion incluida, no requiere extensiones ni cambiar el esquema);
/// del lado de la consulta, Fold aplica la misma tabla de reemplazos al texto buscado.
public static class SearchText
{
    /// Vocales acentuadas y sus equivalentes sin tilde (misma posicion en las dos cadenas). La ñ se mantiene: es otra letra.
    public const string Accented = "áàäâãéèëêíìïîóòöôõúùüû";
    public const string Plain = "aaaaaeeeeiiiiooooouuuu";

    /// translate(text, from, to) de PostgreSQL. Se mapea en CatalogoDbContext y solo se usa dentro de consultas.
    public static string Translate(string text, string from, string to) =>
        throw new NotSupportedException("SearchText.Translate solo se puede usar dentro de una consulta a la base.");

    /// Texto buscado en minusculas y sin tildes, igual que queda el nombre en la base con lower() + translate().
    public static string Fold(string text) =>
        string.Create(text.Length, text.ToLowerInvariant(), (span, lower) =>
        {
            for (var i = 0; i < lower.Length; i++)
            {
                var k = Accented.IndexOf(lower[i]);
                span[i] = k >= 0 ? Plain[k] : lower[i];
            }
        });

    /// Filtra los productos cuyo nombre contiene el texto, sin distinguir mayusculas ni tildes.
    public static IQueryable<Product> NameContains(this IQueryable<Product> q, string text)
    {
        var folded = Fold(text.Trim());
        return folded.Length == 0 ? q : q.Where(p => Translate(p.Name.ToLower(), Accented, Plain).Contains(folded));
    }
}
