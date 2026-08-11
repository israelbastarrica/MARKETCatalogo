namespace MarketCatalogo.Catalogo.Aplicacion;

/// <summary>
/// Orden y agrupación de los talles. Es una CONSTANTE, no datos: no lo edita nadie desde el sitio y
/// cambia sólo cuando aparece un talle nuevo en COMB, lo que además exige decidir a qué grupo pertenece
/// (decisión de programador). Por eso vive en código y no en una tabla: queda versionado, se revisa en
/// el diff y se despliega junto con el código que lo usa.
///
/// Ordenar alfabéticamente NO sirve: 'L' iría antes que 'M' y '10' antes que '2'. Y los grupos son
/// incompatibles entre sí — un artículo no mezcla S/M/L con 36/38/40.
///
/// Los 53 valores salen de medir el catálogo real (docs/MEDICION.md §4). Entre paréntesis, la cantidad
/// de artículos que usaba cada talle al momento de medir.
/// </summary>
public static class Talles
{
    public enum Grupo
    {
        /// <summary>ST, U, X y vacío. Son la MAYORÍA: no se muestra ningún chip de talle.</summary>
        SinTalle,
        Letra,
        Nino,
        Adulto,
        Lenceria,
        /// <summary>No se pudo determinar el grupo. Hay que mirar qué artículos lo usan.</summary>
        Revisar,
    }

    /// <param name="Etiqueta">Cómo se muestra al público. null = igual que el código del talle.</param>
    public sealed record Info(Grupo Grupo, int Orden, string? Etiqueta = null);

    private static readonly Info Desconocido = new(Grupo.Revisar, 9999);

    private static readonly Dictionary<string, Info> Mapa = new(StringComparer.OrdinalIgnoreCase)
    {
        // --- Sin talle -------------------------------------------------------
        [""]    = new(Grupo.SinTalle, 0),           // (228)
        ["ST"]  = new(Grupo.SinTalle, 0),           // (835) el más común de todos
        ["U"]   = new(Grupo.SinTalle, 0, "Único"),  // (180)
        ["X"]   = new(Grupo.SinTalle, 0),           // (1)

        // --- Letra -----------------------------------------------------------
        ["XS"]  = new(Grupo.Letra, 100),            // (1)
        ["S"]   = new(Grupo.Letra, 101),            // (287)
        ["SM"]  = new(Grupo.Letra, 102, "S/M"),     // (17)
        ["M"]   = new(Grupo.Letra, 103),            // (315)
        ["L"]   = new(Grupo.Letra, 104),            // (323)
        ["LXL"] = new(Grupo.Letra, 105, "L/XL"),    // (18)
        ["XL"]  = new(Grupo.Letra, 106),            // (309)
        ["2XL"]   = new(Grupo.Letra, 107),          // (195)
        ["2/3XL"] = new(Grupo.Letra, 108),          // combinado, como SM/LXL
        ["3XL"]   = new(Grupo.Letra, 109),          // (76)
        ["4XL"]   = new(Grupo.Letra, 110),          // (24)
        ["5XL"]   = new(Grupo.Letra, 111),          // (17)
        ["6XL"]   = new(Grupo.Letra, 112),          // (4)
        ["7XL"]   = new(Grupo.Letra, 113),          // (2)

        // --- Niño (numéricos chicos, con y sin cero adelante) ----------------
        ["01"]  = new(Grupo.Nino, 200, "1"),        // (1)
        ["02"]  = new(Grupo.Nino, 201, "2"),        // (2)
        ["03"]  = new(Grupo.Nino, 202, "3"),        // (1)
        ["04"]  = new(Grupo.Nino, 203, "4"),        // (27)
        ["5"]   = new(Grupo.Nino, 204),             // (1)
        ["06"]  = new(Grupo.Nino, 205, "6"),        // (148)
        ["07"]  = new(Grupo.Nino, 206, "7"),        // (1)
        ["08"]  = new(Grupo.Nino, 207, "8"),        // (147)
        ["10"]  = new(Grupo.Nino, 208),             // (153)
        ["11"]  = new(Grupo.Nino, 209),             // (1)
        ["12"]  = new(Grupo.Nino, 210),             // (153)
        ["14"]  = new(Grupo.Nino, 211),             // (151)
        ["16"]  = new(Grupo.Nino, 212),             // (98)
        ["20"]  = new(Grupo.Nino, 213),             // (1)
        ["24"]  = new(Grupo.Nino, 214),             // (5)
        ["25"]  = new(Grupo.Nino, 215),             // (1)

        // --- Adulto (numéricos de indumentaria / calzado) --------------------
        ["36"]  = new(Grupo.Adulto, 300),           // (2)
        ["38"]  = new(Grupo.Adulto, 301),           // (13)
        ["40"]  = new(Grupo.Adulto, 302),           // (24)
        ["42"]  = new(Grupo.Adulto, 303),           // (25)
        ["44"]  = new(Grupo.Adulto, 304),           // (24)
        ["46"]  = new(Grupo.Adulto, 305),           // (21)
        ["48"]  = new(Grupo.Adulto, 306),           // (20)
        ["50"]  = new(Grupo.Adulto, 307),           // (16)
        ["52"]  = new(Grupo.Adulto, 308),           // (7)
        ["54"]  = new(Grupo.Adulto, 309),           // (5)
        ["56"]  = new(Grupo.Adulto, 310),           // (3)

        // --- Lencería (talles de corpiño) -----------------------------------
        ["80"]  = new(Grupo.Lenceria, 400),         // (1)
        ["85"]  = new(Grupo.Lenceria, 401),         // (4)
        ["90"]  = new(Grupo.Lenceria, 402),         // (7)
        ["95"]  = new(Grupo.Lenceria, 403),         // (6)
        ["100"] = new(Grupo.Lenceria, 404),         // (4)
        ["105"] = new(Grupo.Lenceria, 405),         // (4)
        ["110"] = new(Grupo.Lenceria, 406),         // (2)
        ["114"] = new(Grupo.Lenceria, 407),         // (1)
        ["115"] = new(Grupo.Lenceria, 408),         // (1)
        ["120"] = new(Grupo.Lenceria, 409),         // (1)

        // --- A revisar -------------------------------------------------------
        // No quedó claro a qué familia pertenecen (1 a 5 artículos cada uno). NO se adivinaron:
        // hay que mirar qué artículos los usan y reclasificarlos.
        ["20"]  = new(Grupo.Revisar, 900),          // (1)
        ["24"]  = new(Grupo.Revisar, 901),          // (5)
        ["25"]  = new(Grupo.Revisar, 902),          // (1)
    };

    /// <summary>Grupo, orden y etiqueta de un talle. Los desconocidos caen en Revisar al final y nunca
    /// tiran excepción: un talle nuevo en Dragon no puede romper el catálogo público.</summary>
    public static Info Resolver(string? talle)
        => Mapa.TryGetValue((talle ?? "").Trim(), out var info) ? info : Desconocido;

    /// <summary>true si el talle no significa nada para el público (ST, U, X, vacío) y por lo tanto no
    /// va ningún chip en la ficha. Es el caso de la mayoría de los artículos.</summary>
    public static bool EsSinTalle(string? talle) => Resolver(talle).Grupo == Grupo.SinTalle;

    /// <summary>true si el talle no está en la tabla. Se loguea una vez por refresh del caché para
    /// enterarnos de que apareció uno nuevo, en vez de que quede silenciosamente al final.</summary>
    public static bool EsDesconocido(string? talle) => !Mapa.ContainsKey((talle ?? "").Trim());

    /// <summary>Cómo se muestra el talle al público.</summary>
    public static string Mostrar(string? talle)
    {
        var t = (talle ?? "").Trim();
        return Resolver(t).Etiqueta ?? t;
    }
}
