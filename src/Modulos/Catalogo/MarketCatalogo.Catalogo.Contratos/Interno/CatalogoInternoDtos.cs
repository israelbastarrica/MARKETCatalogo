namespace MarketCatalogo.Catalogo.Contratos.Interno;

/// <summary>Una card del catálogo INTERNO: lo del público + los datos de gestión (costo, margen teórico,
/// proveedor, ubicaciones incl. depósito, si está publicado). Sólo la ve el staff logueado.</summary>
public sealed class ArticuloInternoDto
{
    public required string Codigo { get; init; }
    /// <summary>Nombre de vidriera (override o derivado de ARTDES). Campo único de nombre (antes Titulo +
    /// Descripcion). El ARTDES crudo ya no se expone; sólo alimenta la búsqueda.</summary>
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

    /// <summary>Está bloqueado para reposición (existe una fila activa en RepoArticulosBloqueados).</summary>
    public bool Bloqueado { get; init; }

    public required IReadOnlyList<string> Talles { get; init; }
    public required IReadOnlyList<string> Colores { get; init; }

    public string? Proveedor { get; init; }
    public string? Temporada { get; init; }
    public string? Marca { get; init; }
    /// <summary>Año del artículo (ART.ANO). Materializado en dbo.Catalogo; se usa para filtrar la grilla.</summary>
    public int? Anio { get; init; }

    // Características extendidas — sólo en la ficha (a demanda desde Dragon; null = no se consultó, como
    // el stock/ventas). No se materializan ni se usan para filtrar la grilla. Tratamiento = ARTDESADIC;
    // Caracteristica = UNIMED (unidad de medida).
    public string? Tratamiento { get; init; }
    public string? Linea { get; init; }
    public string? Subfamilia { get; init; }
    public string? Material { get; init; }
    public string? Paleta { get; init; }
    public string? CurvaTalles { get; init; }
    public string? Caracteristica { get; init; }
    public string? DescEcommerce { get; init; }
    public bool? PubEcommerce { get; init; }

    /// <summary>Ubicaciones ACTUALES del artículo (mueble/módulo/pasillo/fila/posición), por local y
    /// depósito. Sólo en la ficha (a demanda desde MARKET). Vacío = no se consultó o no está mapeado.</summary>
    public IReadOnlyList<UbicacionInternaDto> Ubicaciones { get; init; } = [];

    /// <summary>Órdenes de pedido (PedidosOrdenes) asociadas al artículo. Sólo en la ficha. Vacío = no
    /// se consultó o no tiene órdenes.</summary>
    public IReadOnlyList<OrdenPedidoDto> Ordenes { get; init; } = [];

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

    // Ventas realizadas de la ventana (últimas N semanas), sólo en la ficha. null = no se consultó.
    // Facturado y costo firmados (las devoluciones restan); el margen realizado se deriva de ambos.
    public int? VentasDias { get; init; }
    /// <summary>Unidades vendidas por semana en la ventana (bucket 0 = la más vieja, último = la más
    /// reciente), Luro + Peralta. Para el gráfico de barras de la ficha. Vacío = no se consultó.</summary>
    public IReadOnlyList<decimal> VentasSemanales { get; init; } = [];
    public decimal? Vendido { get; init; }
    public decimal? VendidoLuro { get; init; }
    public decimal? VendidoPeralta { get; init; }
    public decimal? Facturado { get; init; }
    public decimal? CostoPeriodo { get; init; }
    public decimal? MargenRealPesos { get; init; }
    public decimal? MargenRealPct { get; init; }
    public DateTime? UltimaVenta { get; init; }
    public bool HuboVentas => Vendido is decimal v && v != 0;

    // Benchmark vs la FAMILIA (Prenda): promedio de facturado por artículo de la familia en la misma
    // ventana, y cuántos artículos la componen. Sólo en la ficha. null = no se consultó / sin familia.
    public decimal? FamiliaFacturadoProm { get; init; }
    public int? FamiliaArticulos { get; init; }
    /// <summary>true si el facturado de este artículo supera el promedio de su familia.</summary>
    public bool? SuperaPromedioFamilia =>
        Facturado is decimal f && FamiliaFacturadoProm is decimal p ? f > p : null;
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
    /// <summary>Años seleccionados (ART.ANO como texto, ej. "2025"). Multi-selección (unión).</summary>
    public IReadOnlyList<string> Anios { get; init; } = [];

    /// <summary>Filtro de combo, dos niveles (cantidad + precio del tramo): cada valor es "{cantidad}-{total}"
    /// (ej. "2-15000"). Mismo formato que el público. Vacío = sin filtrar por combo.</summary>
    public IReadOnlyList<string> ComboDetalles { get; init; } = [];

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

/// <summary>Una posición mapeada del artículo (una fila de Mapeo). <c>Tipo</c> = LOCAL/DEPOSITO;
/// <c>Modulo</c> es el código de posición legible (ej. "G1-1", "J03-5"); <c>Mobiliario</c> el mueble
/// (ej. "Perchero"), presente sobre todo en locales.</summary>
public sealed record UbicacionInternaDto(
    string Local, string Tipo, string? Mobiliario, string? Modulo, string? Pasillo, int? Fila, int? Posicion);

/// <summary>Una orden de pedido asociada al artículo (de PedidosOrdenes).</summary>
public sealed record OrdenPedidoDto(int NroOrden, string? Tipo, string? Estado, bool Finalizada, DateTime? FechaMod);

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

    /// <summary>Géneros como faceta: el <c>Valor</c> es el SLUG (ej. "mujer"), la <c>Etiqueta</c> el nombre
    /// mostrable. Va arriba de todo en el drawer, igual que las secciones del filtro mobile público.</summary>
    public required IReadOnlyList<OpcionFacetaInterna> Generos { get; init; }
    public required IReadOnlyList<OpcionFacetaInterna> Rubros { get; init; }
    public required IReadOnlyList<OpcionFacetaInterna> Prendas { get; init; }
    public required IReadOnlyList<OpcionFacetaInterna> Proveedores { get; init; }
    public required IReadOnlyList<OpcionFacetaInterna> Marcas { get; init; }
    public required IReadOnlyList<OpcionFacetaInterna> Temporadas { get; init; }
    public required IReadOnlyList<OpcionFacetaInterna> Anios { get; init; }

    /// <summary>Faceta de combo de dos niveles (cantidad → tramos de precio), igual que el público
    /// (reusa <see cref="OpcionFacetaCombo"/> del contrato). Los tramos salen de la grilla de márgenes.</summary>
    public required IReadOnlyList<OpcionFacetaCombo> Combos { get; init; }
}
