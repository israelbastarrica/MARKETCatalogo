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

    /// <summary>MARKET: qué artículo está mapeado en qué ubicación, INCLUYENDO depósito (marcado con
    /// <see cref="UbicacionRow.EsDeposito"/>). Es el universo del catálogo INTERNO — más amplio que
    /// <see cref="TraerArmadosAsync"/>, que corta el depósito para el público. Lo consume el rebuild de
    /// la tabla <c>dbo.Catalogo</c>.</summary>
    Task<IReadOnlyList<UbicacionRow>> TraerUbicacionesAsync(CancellationToken ct = default);

    /// <summary>DRAGON: cabecera enriquecida para la tabla materializada — suma al header público el
    /// costo (LISTA0), proveedor, temporada y marca. Una fila por código.</summary>
    Task<IReadOnlyList<ArticuloBaseRow>> TraerArticulosBaseAsync(IReadOnlyCollection<string> codigos, CancellationToken ct = default);

    /// <summary>MARKET: persiste (MERGE) las filas BASE calculadas en <c>dbo.Catalogo</c>. Sólo toca las
    /// columnas base; las de ficha (stock/ventas/costo) quedan intactas. Los códigos que ya no están en
    /// el universo se marcan <c>Eliminado = 1</c> (nunca DELETE físico, convención MARKET).</summary>
    Task GuardarBaseAsync(IReadOnlyList<CatalogoFilaBase> filas, CancellationToken ct = default);

    /// <summary>MARKET: lee las filas base de <c>dbo.Catalogo</c> para armar el catálogo. Con
    /// <paramref name="soloPublicados"/> devuelve el subset seguro del público (Publicado = 1); sin él,
    /// todo el universo (vista interna). Siempre excluye <c>Eliminado = 1</c>.</summary>
    Task<IReadOnlyList<CatalogoFilaLeida>> LeerBaseAsync(bool soloPublicados, CancellationToken ct = default);

    /// <summary>MARKET: la ruta en disco de la foto principal de un artículo, leída de <c>dbo.Catalogo</c>.
    /// Con <paramref name="soloPublicado"/> sólo la devuelve si el artículo está publicado — así el endpoint
    /// público no puede servir la foto de un artículo que el catálogo no muestra. null si no hay foto o no
    /// corresponde servirla.</summary>
    Task<string?> LeerRutaFotoAsync(string codigo, bool soloPublicado, CancellationToken ct = default);

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

    /// <summary>DRAGON: la CURVA DE TALLES definida de cada artículo (ART.CURTALL → CTALLE/DCTALLE).
    /// Es la definición teórica de qué talles puede tener el artículo, NO lo que se compró. Se usa sólo
    /// como fallback: cuando las compras (PRECOMPRA/REMCOMPRA) trajeron el artículo sin talle real (todo
    /// ST/U/X/vacío), se muestra esta curva en vez de "Talle único". Ver CatalogoCache.ConstruirAsync.</summary>
    Task<IReadOnlyList<CurvaTalleRow>> TraerCurvasTalleAsync(IReadOnlyCollection<string> codigos, CancellationToken ct = default);

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
// Una ubicación mapeada del artículo. Local = nombre de la ubicación (LURO/PERALTA/…); EsDeposito
// distingue las de tipo DEPOSITO. Un artículo puede tener varias filas (varios locales + depósito).
public sealed record UbicacionRow(string ArtCod, string Local, bool EsDeposito);
public sealed record ArticuloRow(string ArtCod, string ArtDes, string Rubro, string Genero,
                                 string Familia, string Combo, decimal? PrecioSuelta);
// Cabecera enriquecida para la tabla materializada: además del header público, costo (LISTA0),
// proveedor/temporada/marca. PrecioSuelta = LISTA1; PrecioCompra = LISTA0.
public sealed record ArticuloBaseRow(string ArtCod, string ArtDes, string Rubro, string Genero,
                                     string Familia, string Combo, decimal? PrecioSuelta,
                                     decimal? PrecioCompra, string Proveedor, string Temporada, string Marca);
public sealed record VarianteRow(string ArtCod, string ColorCod, string Color, string Talle);
// Un talle de la curva definida del artículo (DCTALLE). Orden es el ORDEN de DCTALLE, que ya viene
// bien de fábrica (2XL antes que 3XL); no se re-ordena con Talles.cs.
public sealed record CurvaTalleRow(string ArtCod, string Talle, int Orden);
public sealed record FotoRow(string ArtCod, string Ruta);
public sealed record OverrideRow(string ArtCod, string? NombreComercial, string? Marketing,
                                 int Destacado, bool OcultarManual);
// Total es int porque así está tipada la columna en PruebaCombos: Dapper materializa records por
// constructor y necesita el tipo exacto de la columna, si no tira InvalidOperationException al mapear.
public sealed record ComboTierRow(int Cantidad, int Total);

/// <summary>Fila BASE calculada, lista para persistir en <c>dbo.Catalogo</c> (columnas base, sin ficha).
/// La arma <c>CatalogoStore</c> cruzando las fuentes; la escribe <c>CatalogoRepositorio.GuardarBaseAsync</c>.
/// Nombres y tipos calzan 1:1 con las columnas base de la tabla (el MERGE mapea por nombre).</summary>
public sealed record CatalogoFilaBase(
    string Codigo, bool Publicado, string Slug, string Titulo, string Descripcion,
    string Rubro, string Genero, string Prenda,
    decimal? PrecioVenta, decimal? PrecioCompra, string? Combo,
    bool EnLuro, bool EnPeralta, bool EnDeposito,
    string TallesCsv, string ColoresCsv,
    bool TieneFoto, string? FotoPrincipalVersion, string? FotosJson,
    string? Proveedor, string? Temporada, string? Marca,
    string TextoBusqueda);

/// <summary>Fila leída de <c>dbo.Catalogo</c> (columnas base). La consume <c>LectorCatalogo</c> para
/// mapearla a <c>ArticuloDto</c> y armar el snapshot. Los derivados (slugs, combo parseado, locales desde
/// los bits, talles/colores desde el CSV) se calculan en C# al leer, no se guardan.</summary>
public sealed record CatalogoFilaLeida(
    string Codigo, bool Publicado, string? Slug, string? Titulo, string? Descripcion,
    string? Rubro, string? Genero, string? Prenda,
    decimal? PrecioVenta, decimal? PrecioCompra, string? Combo,
    bool EnLuro, bool EnPeralta, bool EnDeposito,
    string? TallesCsv, string? ColoresCsv,
    bool TieneFoto, string? FotoPrincipalVersion, string? FotosJson,
    string? Proveedor, string? Temporada, string? Marca, int? Anio, DateTime? FechaAlta,
    decimal? StockTotal, bool TopVentas, string? TextoBusqueda);
