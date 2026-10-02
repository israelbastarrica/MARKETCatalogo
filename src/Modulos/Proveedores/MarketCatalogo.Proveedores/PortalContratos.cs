namespace MarketCatalogo.Proveedores.Contratos;

/// <summary>Un renglón de una OP, con su estado de etiquetas.</summary>
public sealed record PortalRenglon(
    int IdRenglon, string ArtCod, string? Descripcion, decimal? CantidadPedida,
    int EtiquetasImpresas, int EtiquetasImpresasPortal,
    int? EtiquetasEnviadas, DateTime? FechaEnvioEtiquetas);

/// <summary>Una OP vigente del proveedor.</summary>
public sealed record PortalOrden(
    int NroOrden, DateTime? FechaOrden, string? Estado, string? Tipo, IReadOnlyList<PortalRenglon> Renglones);

/// <summary>Las órdenes del proveedor. <paramref name="Disponible"/> separa dos casos que a la vista son
/// iguales y no lo son: NO TENER órdenes, y no haber podido preguntárselo a MarketWeb (servicio caído o
/// clave mal cargada). Decirle "no tenés órdenes" a alguien que sí las tiene lo manda a llamar por teléfono
/// por un problema nuestro.</summary>
public sealed record PortalListaOrdenes(bool Disponible, IReadOnlyList<PortalOrden> Ordenes);

/// <summary>Una combinación color/talle de un artículo, con lo pedido y lo que el proveedor ya imprimió.</summary>
public sealed record PortalCombinacion(string CodColor, string Color, string Talle, int Pedido, int ImpresoPortal)
{
    /// <summary>Lo que todavía puede imprimir. El tope es por COMBINACIÓN (decisión de Israel) y descuenta
    /// SOLO lo impreso por el portal: las etiquetas que se mandaron en papel no restan, porque justamente
    /// el portal existe para cuando ésas no alcanzaron.</summary>
    public int Disponible => Math.Max(0, Pedido - ImpresoPortal);
}

public sealed record PortalArticulo(string ArtCod, string? Descripcion, IReadOnlyList<PortalCombinacion> Combinaciones)
{
    public int Pedido => Combinaciones.Sum(c => c.Pedido);
    public int ImpresoPortal => Combinaciones.Sum(c => c.ImpresoPortal);
    public int Disponible => Combinaciones.Sum(c => c.Disponible);
}

/// <summary>La curva de una OP ya cruzada con lo que el proveedor imprimió.</summary>
public sealed record PortalCurva(int NroOrden, bool PorPack, IReadOnlyList<PortalArticulo> Articulos);

/// <summary>Un pedido de impresión: qué combinación y cuántas.</summary>
public sealed record PortalItemPedido(string ArtCod, string CodColor, string Talle, int Cantidad);

/// <summary>Resultado de imprimir: el ZPL listo para mandar a la térmica, o el motivo del rechazo.</summary>
public sealed record PortalImpresionResultado(bool Ok, string? Mensaje, int Etiquetas, string? Zpl);

/// <summary>La impresora que eligió el proveedor (define lenguaje y DPI).</summary>
public sealed record PortalImpresora(string Clave, string Etiqueta, string Lenguaje, int Dpi);

/// <summary>Lo que el portal expone a las pantallas. El CÓDIGO DE PROVEEDOR nunca es un parámetro del
/// cliente: lo resuelve el servicio desde el claim de la identidad.</summary>
public interface IPortalProveedores
{
    Task<PortalListaOrdenes> OrdenesAsync(string codProveedor, CancellationToken ct = default);
    Task<PortalCurva?> CurvaAsync(string codProveedor, int nroOrden, CancellationToken ct = default);
    Task<PortalImpresionResultado> ImprimirAsync(string codProveedor, string usuario, int nroOrden,
                                                 IReadOnlyList<PortalItemPedido> items, CancellationToken ct = default);
    Task<PortalImpresora?> ImpresoraAsync(string codProveedor, CancellationToken ct = default);
    Task GuardarImpresoraAsync(string codProveedor, string clave, string usuario, CancellationToken ct = default);

    /// <summary>Modelos ofrecidos en el combo. Hoy solo ZPL: es lo único que MarketWeb genera.</summary>
    static IReadOnlyList<PortalImpresora> Impresoras { get; } =
    [
        new("zebra-s4m", "Zebra S4M (203 dpi)", "ZPL", 203),
        new("zebra-203", "Otra Zebra de 203 dpi", "ZPL", 203),
        new("zebra-300", "Zebra de 300 dpi", "ZPL", 300),
        new("otra-zpl-203", "Otra compatible ZPL (203 dpi)", "ZPL", 203),
        new("otra-zpl-300", "Otra compatible ZPL (300 dpi)", "ZPL", 300),
    ];
}
