namespace MarketCatalogo.Catalogo.Contratos.Interno;

/// <summary>Una card del catálogo INTERNO: lo del público + los datos de gestión (costo, margen teórico,
/// proveedor, ubicaciones incl. depósito, si está publicado). Sólo la ve el staff logueado.</summary>
public sealed class ArticuloInternoDto
{
    public required string Codigo { get; init; }
    public required string Titulo { get; init; }
    public required string Descripcion { get; init; }
    public required string Slug { get; init; }

    public required string Rubro { get; init; }
    public required string Genero { get; init; }
    public string? Prenda { get; init; }

    // Precio: venta suelta (LISTA1), combo y costo (LISTA0). El margen TEÓRICO se calcula como en
    // "Cambiar Precios": sobre el precio unitario del combo, NO el suelto (que trae el recargo de $5.000).
    public decimal? PrecioVenta { get; init; }
    public decimal? PrecioCompra { get; init; }
    public string? ComboTexto { get; init; }
    public int? ComboCantidad { get; init; }
    public decimal? ComboTotal { get; init; }
    public decimal? PrecioUnidadCombo { get; init; }
    public decimal? MargenTeorico { get; init; }   // %

    public bool EnLuro { get; init; }
    public bool EnPeralta { get; init; }
    public bool EnDeposito { get; init; }
    public bool EnAlgunLocal => EnLuro || EnPeralta;

    /// <summary>Se está mostrando en el catálogo PÚBLICO. El staff lo ve para saber qué ve el cliente.</summary>
    public bool Publicado { get; init; }

    public required IReadOnlyList<string> Talles { get; init; }
    public required IReadOnlyList<string> Colores { get; init; }

    public string? Proveedor { get; init; }
    public string? Temporada { get; init; }
    public string? Marca { get; init; }

    public bool TieneFoto { get; init; }
    public string? FotoVersion { get; init; }

    // Stock, sólo en la ficha (a demanda; null = no se consultó, como en la grilla). Desglosado por origen
    // (Luro / Peralta / central-depósito), leído de cada réplica por separado. StockTotal/EnTransito son la
    // suma de los tres.
    public decimal? StockTotal { get; init; }
    public decimal? EnTransito { get; init; }
    public decimal? StockLuro { get; init; }
    public decimal? TransitoLuro { get; init; }
    public decimal? StockPeralta { get; init; }
    public decimal? TransitoPeralta { get; init; }
    public decimal? StockCentral { get; init; }
    public decimal? TransitoCentral { get; init; }
}

/// <summary>Filtros de la grilla interna. Todo multi-selección es unión (OR dentro de la faceta). Estado
/// en la URL, igual que el público.</summary>
public sealed record FiltrosInterno
{
    // Ubicación: dónde está mapeado. "luro"/"peralta"/"deposito" (multi). El cruce depo/local afina:
    // null = sin filtro; "solo-deposito" = en depo y en NINGÚN local; "en-local" = en algún local.
    public IReadOnlyList<string> Ubicaciones { get; init; } = [];
    public string? CruceDepoLocal { get; init; }

    // Rubros por VALOR (ej. "Indumentaria"); Géneros por SLUG (ej. "mujer") — así el header (que trabaja
    // con slugs) puede linkear a la grilla interna.
    public IReadOnlyList<string> Rubros { get; init; } = [];
    public IReadOnlyList<string> Generos { get; init; } = [];
    public IReadOnlyList<string> Prendas { get; init; } = [];
    public IReadOnlyList<string> Talles { get; init; } = [];
    public IReadOnlyList<string> Colores { get; init; } = [];
    public IReadOnlyList<string> Proveedores { get; init; } = [];
    public IReadOnlyList<string> Marcas { get; init; } = [];
    public IReadOnlyList<string> Temporadas { get; init; } = [];

    /// <summary>true = sólo los que se ven en el público; false = sólo los que NO; null = todos.</summary>
    public bool? Publicado { get; init; }
    /// <summary>Margen teórico máximo (ej. 30 = "margen &lt; 30%"): para cazar los de margen flaco.</summary>
    public decimal? MargenMax { get; init; }

    public string? Texto { get; init; }
    public string Orden { get; init; } = "codigo";
    public int Pagina { get; init; } = 1;

    public const int PorPagina = 60;
}

public sealed record OpcionFacetaInterna(string Valor, string Etiqueta, int Cantidad, bool Activa);

public sealed class PaginaInternaDto
{
    public required IReadOnlyList<ArticuloInternoDto> Items { get; init; }
    public required int Total { get; init; }
    public required int Pagina { get; init; }
    public int Paginas => Total == 0 ? 1 : (int)Math.Ceiling(Total / (double)FiltrosInterno.PorPagina);

    // Totales del universo interno (para el encabezado): cuántos hay en total, en depósito, sólo-depósito.
    public required int TotalUniverso { get; init; }
    public required int EnDeposito { get; init; }
    public required int SoloDeposito { get; init; }
    public required int Publicados { get; init; }

    /// <summary>Cuándo se reconstruyó la base por última vez (reloj en memoria del store). null = todavía
    /// no se armó en esta instancia. Para el "datos de hace X min".</summary>
    public DateTime? BaseActualizada { get; init; }

    public required IReadOnlyList<OpcionFacetaInterna> Rubros { get; init; }
    public required IReadOnlyList<OpcionFacetaInterna> Prendas { get; init; }
    public required IReadOnlyList<OpcionFacetaInterna> Proveedores { get; init; }
    public required IReadOnlyList<OpcionFacetaInterna> Marcas { get; init; }
    public required IReadOnlyList<OpcionFacetaInterna> Temporadas { get; init; }
}
