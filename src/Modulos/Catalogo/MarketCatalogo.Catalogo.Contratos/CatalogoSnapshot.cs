using MarketCatalogo.Catalogo.Contratos;

namespace MarketCatalogo.Catalogo.Contratos;

/// <summary>Foto completa del catálogo en un instante. Inmutable: el refresh construye una nueva y la
/// intercambia de una sola vez, así ningún request ve un estado a medio armar.</summary>
public sealed class CatalogoSnapshot
{
    public required IReadOnlyList<ArticuloDto> Articulos { get; init; }
    public required IReadOnlyDictionary<string, ArticuloDto> PorSlug { get; init; }
    public required IReadOnlyDictionary<string, ArticuloDto> PorCodigo { get; init; }
    public required IReadOnlyList<RubroMenu> Menu { get; init; }

    /// <summary>Los tramos oficiales de combo (grilla de márgenes), no derivados del catálogo armado.
    /// Fuente de qué cantidades y qué precios ofrece el filtro de combo.</summary>
    public required IReadOnlyList<ComboTier> ComboTiers { get; init; }

    /// <summary>ARTCOD → ruta de la foto original en disco (GoogleDriveFotosArticulos.LinkDriveDisco).
    /// No va en el DTO porque es una ruta del servidor y no tiene por qué salir al HTML; el endpoint de
    /// fotos la consulta acá. Efecto secundario buscado: <b>sólo se pueden servir fotos de artículos que
    /// están en el catálogo</b>, así que el filtro de visibilidad también aplica a las imágenes.</summary>
    public required IReadOnlyDictionary<string, string> RutaFotoPorCodigo { get; init; }

    public required DateTimeOffset Generado { get; init; }

    // --- Métricas, para la pantalla de estado y para saber si algo se está degradando ---
    /// <summary>Armados en locales antes de filtrar (hoy ~985).</summary>
    public required int TotalArmados { get; init; }
    /// <summary>Descartados por rubro o género inválido: la basura tipo el pseudo-artículo "2X15000".</summary>
    public required int DescartadosPorTaxonomia { get; init; }
    /// <summary>Publicados sin foto, que salen con placeholder (hoy ~303). Métrica a seguir.</summary>
    public required int SinFoto { get; init; }
    /// <summary>Descartados por no tener ninguna fila en PRECOMPRA ni REMCOMPRA (y no ser Lencería,
    /// que no usa esa cascada): mejor no publicarlos que mostrarlos sin colores ni talles.</summary>
    public required int SinVariantes { get; init; }
    /// <summary>Talles que aparecieron en COMB y no están en <see cref="Talles"/>. Si esto crece,
    /// hay que agregarlos al diccionario en código.</summary>
    public required IReadOnlyList<string> TallesDesconocidos { get; init; }

    public int Total => Articulos.Count;
    public TimeSpan Antiguedad => DateTimeOffset.UtcNow - Generado;

    public static CatalogoSnapshot Vacio() => new()
    {
        Articulos = Array.Empty<ArticuloDto>(),
        PorSlug = new Dictionary<string, ArticuloDto>(),
        PorCodigo = new Dictionary<string, ArticuloDto>(),
        Menu = Array.Empty<RubroMenu>(),
        ComboTiers = Array.Empty<ComboTier>(),
        RutaFotoPorCodigo = new Dictionary<string, string>(),
        Generado = DateTimeOffset.MinValue,
        TotalArmados = 0,
        DescartadosPorTaxonomia = 0,
        SinFoto = 0,
        SinVariantes = 0,
        TallesDesconocidos = Array.Empty<string>(),
    };
}
