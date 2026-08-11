namespace MarketCatalogo.Catalogo.Aplicacion;

/// <summary>
/// Lo que la capa de aplicación necesita del almacenamiento. La interfaz vive ACÁ y la implementación
/// en <c>Catalogo.Datos</c>: inversión de dependencias, así Aplicación no sabe que hay SQL detrás
/// (Datos → Aplicacion, nunca al revés).
///
/// Un método por FUENTE, no una consulta grande: nunca se joinea MARKET con DRAGONFISH. Si algún día
/// las bases se separan en la nube, el join cruzado deja de existir (Azure SQL Database no lo soporta)
/// y esto sigue funcionando cambiando config. Ver docs/CONSULTAS.md §2.bis.
/// </summary>
public interface ICatalogoRepositorio
{
    /// <summary>MARKET: qué artículo está armado en qué local (excluye depósito). Define el universo.</summary>
    Task<IReadOnlyList<ArmadoRow>> TraerArmadosAsync(CancellationToken ct = default);

    /// <summary>DRAGON: cabecera, taxonomía, combo y precio vigente de los códigos pedidos.</summary>
    Task<IReadOnlyList<ArticuloRow>> TraerArticulosAsync(IReadOnlyCollection<string> codigos, CancellationToken ct = default);

    /// <summary>DRAGON: color × talle de cada artículo, de las órdenes de compra (PRECOMPRA), no
    /// armado (COMB): el color viene como texto directo del remito (FCOTXT), sin el problema de
    /// matcheo por código contra DPCOLOR que en COMB dejaba variantes sin nombre — y sin sus datos
    /// sucios en general. Es la PRIMERA fuente que se intenta — ver TraerVariantesRemcompraAsync
    /// para el resto de la cascada, en CatalogoCache.ConstruirAsync. A propósito NO hay una tercera
    /// fuente que caiga a COMB: un artículo sin nada acá queda sin colores/talles antes que mostrar
    /// datos sucios.</summary>
    Task<IReadOnlyList<VarianteRow>> TraerVariantesPrecompraAsync(IReadOnlyCollection<string> codigos, CancellationToken ct = default);

    /// <summary>DRAGON: color × talle de cada artículo, de los remitos de compra (REMCOMPRA) — igual
    /// de limpio que PRECOMPRA (color como texto directo), se usa como SEGUNDA y ÚLTIMA fuente para
    /// los artículos que no tuvieron ninguna orden de compra cargada.</summary>
    Task<IReadOnlyList<VarianteRow>> TraerVariantesRemcompraAsync(IReadOnlyCollection<string> codigos, CancellationToken ct = default);

    /// <summary>MARKET: ruta en disco de la foto de cada artículo.</summary>
    Task<IReadOnlyList<FotoRow>> TraerRutasFotoAsync(CancellationToken ct = default);

    /// <summary>MARKET: overrides editoriales. Devuelve vacío si la tabla todavía no existe.</summary>
    Task<IReadOnlyList<OverrideRow>> TraerOverridesAsync(CancellationToken ct = default);

    /// <summary>MARKET: los tramos oficiales de combo (cuántas unidades y a qué precio total), de la
    /// grilla de márgenes (PruebaCombos). Es la fuente de qué cantidades y qué precios ofrece el filtro
    /// de combo — no se derivan agrupando lo que hay armado en este momento, sino de la tabla que define
    /// los combos válidos; el conteo por artículo de cada tramo sigue viniendo del snapshot, como el
    /// resto de las facetas.</summary>
    Task<IReadOnlyList<ComboTierRow>> TraerComboTiersAsync(CancellationToken ct = default);
}

// Filas crudas de cada fuente. Son del módulo, no del contrato público: nadie afuera las ve.
public sealed record ArmadoRow(string ArtCod, string Local);
public sealed record ArticuloRow(string ArtCod, string ArtDes, string Rubro, string Genero,
                                 string Familia, string Combo, decimal? PrecioSuelta);
public sealed record VarianteRow(string ArtCod, string ColorCod, string Color, string Talle);
public sealed record FotoRow(string ArtCod, string Ruta);
public sealed record OverrideRow(string ArtCod, string? NombreComercial, string? Marketing,
                                 int Destacado, bool OcultarManual);
// Total es int porque así está tipada la columna en PruebaCombos: Dapper materializa records por
// constructor y necesita el tipo exacto de la columna, si no tira InvalidOperationException al mapear.
public sealed record ComboTierRow(int Cantidad, int Total);
