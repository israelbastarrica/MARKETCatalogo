namespace MarketCatalogo.Catalogo.Contratos;

/// <summary>Un artículo del catálogo público, ya resuelto y listo para mostrar. Vive en el caché en
/// memoria (~981 instancias, ~2 MB); no se serializa a JSON en el flujo normal porque el render es SSR.</summary>
public sealed class ArticuloDto
{
    public required string ArtCod { get; init; }

    /// <summary>Lo que ve el público: el override manual si existe, si no el título derivado de ARTDES.</summary>
    public required string Titulo { get; init; }

    /// <summary>ART.ARTDES crudo. Se conserva para búsqueda y para la ficha ("código de fábrica").</summary>
    public required string Descripcion { get; init; }

    public string? Marketing { get; init; }

    /// <summary>URL canónica del producto, ej. "campera-magic-con-piel-im071-062".</summary>
    public required string Slug { get; init; }

    public required string Rubro { get; init; }        // "Indumentaria"
    public required string RubroSlug { get; init; }     // "indumentaria"
    public required string Genero { get; init; }        // "Mujer"
    public required string GeneroSlug { get; init; }    // "mujer"
    public string? Familia { get; init; }               // "Campera"
    public string? FamiliaSlug { get; init; }

    // --- Precio (ver docs/MEDICION.md §6) ---------------------------------------
    /// <summary>ART.CLASIFART tal cual, ej. "2X15000". Es la oferta que se muestra como titular.</summary>
    public string? ComboTexto { get; init; }
    public int? ComboCantidad { get; init; }
    public decimal? ComboTotal { get; init; }
    /// <summary>Precio por unidad DENTRO del combo. Es el número por el que se ordena y se filtra.</summary>
    public decimal? PrecioUnidadCombo { get; init; }
    /// <summary>Precio de UNA unidad suelta (PRECIOAR LISTA1). Es PrecioUnidadCombo + $5.000.</summary>
    public decimal? PrecioUnidadSuelta { get; init; }

    /// <summary>Ya formateado ("$12.500"). Va acá para que la capa de UI no tenga que conocer el dominio
    /// del módulo sólo para formatear moneda.</summary>
    public string? PrecioSueltaTexto { get; init; }

    public bool TieneFoto { get; init; }
    public int Destacado { get; init; }

    /// <summary>Locales donde está armado, ej. ["LURO", "PERALTA"].</summary>
    public required IReadOnlyList<string> Locales { get; init; }

    public required IReadOnlyList<VarianteDto> Variantes { get; init; }

    /// <summary>Talles distintos, ya ordenados y sin los que significan "sin talle".</summary>
    public required IReadOnlyList<string> Talles { get; init; }

    /// <summary>Colores distintos, ordenados.</summary>
    public required IReadOnlyList<string> Colores { get; init; }

    /// <summary>Título + descripción + código, en minúsculas y sin acentos. Para el filtro ?q=.</summary>
    public required string TextoBusqueda { get; init; }
}

public sealed record VarianteDto(string ColorCod, string Color, string Talle, string TalleMostrar, int TalleOrden);

/// <summary>Filtros de la grilla. Todo sale de la ruta y del query string: la URL es el único estado
/// (docs/CONSULTAS.md §1).</summary>
public sealed record FiltrosCatalogo
{
    public string? RubroSlug { get; init; }
    public string? GeneroSlug { get; init; }

    // Todos los filtros de faceta son ACUMULABLES (multi-selección): se puede tildar más de una opción
    // por tipo. Vacío = sin filtrar ese tipo; con varias = unión (artículos que cumplen CUALQUIERA).
    // En el query string viajan como CSV: ?familia=campera,pantalon&talle=s,m&local=luro,peralta.
    // "Rubros" es el TIPO (Indumentaria, Accesorios, Lencería…). Viaja como ?tipo=… para no chocar con
    // el {rubro} de la RUTA, que es la landing indexable de una sección; acá es refinamiento (noindex).
    public IReadOnlyList<string> Rubros { get; init; } = [];
    // Géneros como filtro multi-valor (?gen=nene,nena,bebe). Lo usa el mega-menú para agrupar géneros
    // bajo un rótulo (ej. "Niños" = nene+nena+bebe). El {genero} de la RUTA sigue siendo el género único
    // e indexable de una sección; esto es refinamiento (noindex), como el resto.
    public IReadOnlyList<string> Generos { get; init; } = [];
    public IReadOnlyList<string> Familias { get; init; } = [];
    public IReadOnlyList<string> Talles { get; init; } = [];
    public IReadOnlyList<string> Colores { get; init; } = [];
    public IReadOnlyList<string> Locales { get; init; } = [];
    public IReadOnlyList<int> Combos { get; init; } = [];

    public decimal? PrecioMin { get; init; }
    public decimal? PrecioMax { get; init; }
    public string? Texto { get; init; }
    public string Orden { get; init; } = "destacados";
    public int Pagina { get; init; } = 1;

    public const int PorPagina = 48;
}

public sealed record OpcionFaceta(string Valor, string Etiqueta, int Cantidad, bool Activa);

public sealed class PaginaCatalogoDto
{
    public required IReadOnlyList<ArticuloDto> Items { get; init; }
    public required int Total { get; init; }
    public required int Pagina { get; init; }
    public int Paginas => Total == 0 ? 1 : (int)Math.Ceiling(Total / (double)FiltrosCatalogo.PorPagina);

    public required IReadOnlyList<OpcionFaceta> Rubros { get; init; }
    public required IReadOnlyList<OpcionFaceta> Familias { get; init; }
    public required IReadOnlyList<OpcionFaceta> Talles { get; init; }
    public required IReadOnlyList<OpcionFaceta> Colores { get; init; }
    public required IReadOnlyList<OpcionFaceta> Locales { get; init; }
    public required IReadOnlyList<OpcionFaceta> Combos { get; init; }
}

/// <summary>Una entrada del menú principal: rubro con sus géneros y cuántos artículos tiene cada uno.</summary>
public sealed record RubroMenu(string Slug, string Nombre, int Cantidad, IReadOnlyList<GeneroMenu> Generos);
public sealed record GeneroMenu(string Slug, string Nombre, int Cantidad);
