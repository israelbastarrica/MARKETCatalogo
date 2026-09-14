namespace MarketCatalogo.Catalogo.Aplicacion;

/// <summary>
/// Qué cuenta como "novedad de temporada": la temporada que entra (primavera-verano, <c>Prim-Ver</c>) de
/// los años más nuevos. Es UN solo lugar para no desincronizar dos usos que tienen que coincidir:
/// <list type="bullet">
///   <item>El criterio de PUBLICACIÓN (<c>CatalogoStore</c>): un artículo que está sólo en depósito se
///   publica igual si es novedad — así entra al catálogo, a la ficha y a la sección Novedades.</item>
///   <item>La LECTURA de Novedades (<c>LectorCatalogo</c>): la sección del home y el toggle
///   "Novedades" de la grilla pública.</item>
/// </list>
/// Correr el año o la temporada cuando entre una colección nueva se hace acá y listo.
/// </summary>
public static class NovedadesPolitica
{
    /// <summary>Años que cuentan como novedad (los más nuevos), más nuevo primero.</summary>
    public static readonly int[] Anios = [2027, 2026];

    /// <summary>Temporada(s) que cuentan como el ingreso actual: primavera-verano.</summary>
    public static readonly string[] Temporadas = ["Prim-Ver"];

    /// <summary>true si (temporada, año) caen dentro de la novedad de temporada. Insensible a
    /// mayúsculas/espacios en la temporada.</summary>
    public static bool Es(string? temporada, int? anio) =>
        anio is int a && Array.IndexOf(Anios, a) >= 0
        && temporada is not null
        && Array.Exists(Temporadas, t => t.Equals(temporada.Trim(), StringComparison.OrdinalIgnoreCase));
}
