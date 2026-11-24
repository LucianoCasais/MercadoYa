namespace MercadoYa.Shared;

/// Aviso de que cambio algo que muestra el catalogo publico (un producto, su precio o su stock).
/// Lo implementa la cache del modulo Catalogo y lo llaman el ABM de productos e Inventario (reservas, liberaciones y ajustes).
/// Vive en Shared para que Inventario avise sin depender del modulo Catalogo (los modulos se conocen solo por contratos).
public interface ICatalogChangeNotifier
{
    void CatalogChanged();
}
