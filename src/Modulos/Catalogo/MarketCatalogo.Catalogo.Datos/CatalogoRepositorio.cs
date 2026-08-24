using System.Data;
using Dapper;
using MarketCatalogo.Catalogo.Aplicacion;
using MarketCatalogo.Compartido.Datos;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace MarketCatalogo.Catalogo.Datos;

/// <summary>
/// CAPA DE DATOS del módulo. Implementa <see cref="ICatalogoRepositorio"/>: es lo único que sabe SQL y
/// lo único que conoce el esquema de Dragon y de los mapeos de MARKET.
///
/// <b>Una consulta por fuente y por conexión</b>: nunca un JOIN entre MARKET y DRAGONFISH. El cruce se
/// hace en C#, en la capa de aplicación — ver docs/CONSULTAS.md §2.bis (decisión D8c).
///
/// Corre una vez cada 5 minutos, no por request.
/// </summary>
public sealed class CatalogoRepositorio : ICatalogoRepositorio
{
    // Los códigos viajan como parámetros, en lotes: así la consulta no depende de que las dos bases
    // estén en la misma instancia. 500 por lote deja margen sobre el límite de 2100 parámetros de SQL
    // Server y evita inflar el plan cache con una firma distinta por cada corrida.
    private const int TamanioLote = 500;

    private readonly ISqlConnectionFactory _db;
    private readonly ILogger<CatalogoRepositorio> _log;

    public CatalogoRepositorio(ISqlConnectionFactory db, ILogger<CatalogoRepositorio> log)
    {
        _db = db;
        _log = log;
    }

    // Las filas crudas (ArmadoRow, ArticuloRow, …) las declara ICatalogoRepositorio en la capa de
    // aplicación: son parte de lo que ésta le pide al almacenamiento, no de lo que Datos decide.

    /// <summary>MARKET: qué artículo está armado en qué local. Excluye depósito — un artículo que sólo
    /// está en el depósito no se vende en ningún local, así que no va al catálogo.
    /// El predicado es el mismo que usa MARKETweb para "armado en un local".</summary>
    public async Task<IReadOnlyList<ArmadoRow>> TraerArmadosAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT ArtCod = RTRIM(REG.ARTCOD), Local = RTRIM(UB.Descripcion)
            FROM MARKET.dbo.MapeoRegistro      REG WITH (NOLOCK)
            JOIN MARKET.dbo.Mapeo              MAP WITH (NOLOCK) ON MAP.ID = REG.IDMapeo
            JOIN MARKET.dbo.Ubicaciones        UB  WITH (NOLOCK) ON UB.ID  = MAP.IDUbicacion
            JOIN MARKET.dbo.UbicacionesTipo    UT  WITH (NOLOCK) ON UT.ID  = UB.IDTipo
            WHERE REG.Eliminado = 0 AND MAP.Eliminado = 0 AND UT.Descripcion <> 'DEPOSITO'
            GROUP BY RTRIM(REG.ARTCOD), RTRIM(UB.Descripcion);
            """;
        using var cn = _db.CrearMarket();
        return (await cn.QueryAsync<ArmadoRow>(new CommandDefinition(sql, commandTimeout: 120, cancellationToken: ct))).ToList();
    }

    /// <summary>MARKET: universo INTERNO — todo lo mapeado, incluido depósito (marcado con EsDeposito).
    /// Mismo origen que TraerArmadosAsync pero SIN el corte de depósito: la vista interna lo necesita.</summary>
    public async Task<IReadOnlyList<UbicacionRow>> TraerUbicacionesAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT ArtCod = RTRIM(REG.ARTCOD),
                   Local  = RTRIM(UB.Descripcion),
                   EsDeposito = CASE WHEN UT.Descripcion = 'DEPOSITO' THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END
            FROM MARKET.dbo.MapeoRegistro      REG WITH (NOLOCK)
            JOIN MARKET.dbo.Mapeo              MAP WITH (NOLOCK) ON MAP.ID = REG.IDMapeo
            JOIN MARKET.dbo.Ubicaciones        UB  WITH (NOLOCK) ON UB.ID  = MAP.IDUbicacion
            JOIN MARKET.dbo.UbicacionesTipo    UT  WITH (NOLOCK) ON UT.ID  = UB.IDTipo
            WHERE REG.Eliminado = 0 AND MAP.Eliminado = 0
              AND RTRIM(ISNULL(REG.ARTCOD, '')) <> ''   -- posiciones de depósito sin artículo
            GROUP BY RTRIM(REG.ARTCOD), RTRIM(UB.Descripcion),
                     CASE WHEN UT.Descripcion = 'DEPOSITO' THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END;
            """;
        using var cn = _db.CrearMarket();
        return (await cn.QueryAsync<UbicacionRow>(new CommandDefinition(sql, commandTimeout: 120, cancellationToken: ct))).ToList();
    }

    /// <summary>DRAGON: cabecera enriquecida para la tabla materializada. Suma al header público el costo
    /// (LISTA0) y proveedor/temporada/marca. Los JOIN de maestros calcan a MARKETweb (ArticulosService)
    /// para no divergir. Sin prefijo de base a propósito (ver SqlConnectionFactory).</summary>
    public Task<IReadOnlyList<ArticuloBaseRow>> TraerArticulosBaseAsync(
        IReadOnlyCollection<string> codigos, CancellationToken ct = default)
    {
        // Proveedor = 3 chars antes del '.' del ARTCOD (mismo criterio que MARKETweb), resuelto a nombre
        // por PROV.CLNOM. LISTA0 = costo, LISTA1 = venta suelta; ambas con FECHAVIG<=GETDATE() para no
        // adelantar precios futuros del proceso de "Cambiar Precios".
        const string sql = """
            SELECT ArtCod  = RTRIM(A.ARTCOD),
                   ArtDes  = RTRIM(ISNULL(A.ARTDES, '')),
                   Rubro   = RTRIM(ISNULL(TIPO.DESCRIP, '')),
                   Genero  = RTRIM(ISNULL(CATE.DESCRIP, '')),
                   Familia = RTRIM(ISNULL(FAM.DESCRIP, '')),
                   Combo   = UPPER(RTRIM(ISNULL(A.CLASIFART, ''))),
                   PrecioSuelta = PV.PDIRECTO,
                   PrecioCompra = PC.PDIRECTO,
                   Proveedor = RTRIM(ISNULL(PROV.CLNOM, '')),
                   Temporada = RTRIM(ISNULL(TE.TDES, '')),
                   Marca     = RTRIM(ISNULL(MK.DESCRIP, ''))
            FROM ZooLogic.ART A WITH (NOLOCK)
            LEFT JOIN ZooLogic.TIPOART  TIPO WITH (NOLOCK) ON TIPO.COD = A.TIPOARTI
            LEFT JOIN ZooLogic.CATEGART CATE WITH (NOLOCK) ON CATE.COD = A.CATEARTI
            LEFT JOIN ZooLogic.FAMILIA  FAM  WITH (NOLOCK) ON FAM.COD  = A.FAMILIA
            LEFT JOIN ZooLogic.MARCAS   MK   WITH (NOLOCK) ON MK.CODIGO = A.MARCA
            LEFT JOIN ZooLogic.TEMPORADA TE  WITH (NOLOCK) ON TE.TCOD   = A.ATEMPORADA
            LEFT JOIN ZooLogic.PROV     PROV WITH (NOLOCK)
                   ON PROV.CLCOD = CASE WHEN CHARINDEX('.', A.ARTCOD) >= 4
                                        THEN SUBSTRING(A.ARTCOD, CHARINDEX('.', A.ARTCOD) - 3, 3) ELSE '' END
            OUTER APPLY (SELECT TOP 1 P.PDIRECTO
                         FROM ZooLogic.PRECIOAR P WITH (NOLOCK)
                         WHERE P.ARTICULO = A.ARTCOD AND P.LISTAPRE = 'LISTA1'
                           AND P.FECHAVIG <= GETDATE()
                         ORDER BY P.FECHAVIG DESC, P.HMODIFW DESC) PV
            OUTER APPLY (SELECT TOP 1 P.PDIRECTO
                         FROM ZooLogic.PRECIOAR P WITH (NOLOCK)
                         WHERE P.ARTICULO = A.ARTCOD AND P.LISTAPRE = 'LISTA0'
                           AND P.FECHAVIG <= GETDATE()
                         ORDER BY P.FECHAVIG DESC, P.HMODIFW DESC) PC
            WHERE RTRIM(A.ARTCOD) IN @codigos;
            """;
        return PorLotesAsync<ArticuloBaseRow>(sql, codigos, dragon: true, ct);
    }

    /// <summary>DRAGON: cabecera, taxonomía, combo y precio vigente de los códigos pedidos.
    /// Sin prefijo de base a propósito (ver SqlConnectionFactory).</summary>
    public Task<IReadOnlyList<ArticuloRow>> TraerArticulosAsync(
        IReadOnlyCollection<string> codigos, CancellationToken ct = default)
    {
        // FECHAVIG <= GETDATE() es OBLIGATORIO: el proceso de "Cambiar Precios" de MARKETweb inserta
        // filas con vigencia futura, y sin este filtro el sitio publicaría un precio que todavía no
        // entró en vigencia. MARKETweb no filtra por esto (a una pantalla interna le interesa ver el
        // pendiente), pero para el público es otra cosa. Ver docs/MEDICION.md §6.
        const string sql = """
            SELECT ArtCod  = RTRIM(A.ARTCOD),
                   ArtDes  = RTRIM(ISNULL(A.ARTDES, '')),
                   Rubro   = RTRIM(ISNULL(TIPO.DESCRIP, '')),
                   Genero  = RTRIM(ISNULL(CATE.DESCRIP, '')),
                   Familia = RTRIM(ISNULL(FAM.DESCRIP, '')),
                   Combo   = UPPER(RTRIM(ISNULL(A.CLASIFART, ''))),
                   PrecioSuelta = PV.PDIRECTO
            FROM ZooLogic.ART A WITH (NOLOCK)
            LEFT JOIN ZooLogic.TIPOART  TIPO WITH (NOLOCK) ON TIPO.COD = A.TIPOARTI
            LEFT JOIN ZooLogic.CATEGART CATE WITH (NOLOCK) ON CATE.COD = A.CATEARTI
            LEFT JOIN ZooLogic.FAMILIA  FAM  WITH (NOLOCK) ON FAM.COD  = A.FAMILIA
            OUTER APPLY (SELECT TOP 1 P.PDIRECTO
                         FROM ZooLogic.PRECIOAR P WITH (NOLOCK)
                         WHERE P.ARTICULO = A.ARTCOD AND P.LISTAPRE = 'LISTA1'
                           AND P.FECHAVIG <= GETDATE()
                         ORDER BY P.FECHAVIG DESC, P.HMODIFW DESC) PV
            WHERE RTRIM(A.ARTCOD) IN @codigos;
            """;
        return PorLotesAsync<ArticuloRow>(sql, codigos, dragon: true, ct);
    }

    /// <summary>DRAGON: color × talle de cada artículo, de las órdenes de compra (PRECOMPRA). El color
    /// sale como texto directo del remito (FCOTXT) — sin el problema de matcheo de COMB. Se excluyen
    /// las órdenes anuladas (PRECOMPRA.ANULADO). Primera fuente de la cascada.</summary>
    public Task<IReadOnlyList<VarianteRow>> TraerVariantesPrecompraAsync(
        IReadOnlyCollection<string> codigos, CancellationToken ct = default)
    {
        const string sql = """
            SELECT ArtCod   = RTRIM(PC.FART),
                   ColorCod = RTRIM(PC.FCOLO),
                   Color    = RTRIM(ISNULL(PC.FCOTXT, '')),
                   Talle    = RTRIM(PC.FTALL)
            FROM ZooLogic.PRECOMPRADET PC WITH (NOLOCK)
            JOIN ZooLogic.PRECOMPRA PH WITH (NOLOCK) ON PH.CODIGO = PC.CODIGO
            WHERE RTRIM(PC.FART) IN @codigos AND ISNULL(PH.ANULADO, 0) = 0
            GROUP BY RTRIM(PC.FART), RTRIM(PC.FCOLO), RTRIM(ISNULL(PC.FCOTXT, '')), RTRIM(PC.FTALL);
            """;
        return PorLotesAsync<VarianteRow>(sql, codigos, dragon: true, ct);
    }

    /// <summary>DRAGON: color × talle de cada artículo, de los remitos de compra (REMCOMPRA). Mismo
    /// criterio que PRECOMPRA (color como texto directo, se excluyen los anulados). Segunda fuente
    /// de la cascada: cubre artículos que no tuvieron ninguna orden de compra cargada.</summary>
    public Task<IReadOnlyList<VarianteRow>> TraerVariantesRemcompraAsync(
        IReadOnlyCollection<string> codigos, CancellationToken ct = default)
    {
        const string sql = """
            SELECT ArtCod   = RTRIM(RC.FART),
                   ColorCod = RTRIM(RC.FCOLO),
                   Color    = RTRIM(ISNULL(RC.FCOTXT, '')),
                   Talle    = RTRIM(RC.FTALL)
            FROM ZooLogic.REMCOMPRADET RC WITH (NOLOCK)
            JOIN ZooLogic.REMCOMPRA RH WITH (NOLOCK) ON RH.CODIGO = RC.CODIGO
            WHERE RTRIM(RC.FART) IN @codigos AND ISNULL(RH.ANULADO, 0) = 0
            GROUP BY RTRIM(RC.FART), RTRIM(RC.FCOLO), RTRIM(ISNULL(RC.FCOTXT, '')), RTRIM(RC.FTALL);
            """;
        return PorLotesAsync<VarianteRow>(sql, codigos, dragon: true, ct);
    }

    /// <summary>DRAGON: la curva de talles definida de cada artículo. ART.CURTALL es el código de la
    /// curva; CTALLE es la cabecera y DCTALLE el detalle (un talle por fila, con su ORDEN de fábrica).
    /// Se excluyen los CURTALL vacíos. NO es lo que se compró — es sólo el fallback para los artículos
    /// que las compras dejaron sin talle (ver CatalogoCache.ConstruirAsync).</summary>
    public Task<IReadOnlyList<CurvaTalleRow>> TraerCurvasTalleAsync(
        IReadOnlyCollection<string> codigos, CancellationToken ct = default)
    {
        const string sql = """
            SELECT ArtCod = RTRIM(A.ARTCOD),
                   Talle  = RTRIM(D.CODTALL),
                   Orden  = CAST(D.ORDEN AS int)
            FROM ZooLogic.ART A WITH (NOLOCK)
            JOIN ZooLogic.DCTALLE D WITH (NOLOCK) ON RTRIM(D.CODIGO) = RTRIM(A.CURTALL)
            WHERE RTRIM(A.ARTCOD) IN @codigos AND RTRIM(ISNULL(A.CURTALL, '')) <> ''
            ORDER BY RTRIM(A.ARTCOD), D.ORDEN;
            """;
        return PorLotesAsync<CurvaTalleRow>(sql, codigos, dragon: true, ct);
    }

    /// <summary>MARKET: la ruta en disco de la foto de cada artículo.
    /// La tabla tiene VARIAS filas por código (hasta 70 medidas), así que hay que quedarse con la
    /// última por ID. Se prefiere la foto IA (<c>LinkIADisco</c>) sobre la normal (<c>LinkDriveDisco</c>):
    /// mismo criterio que MARKETweb (CatalogosService, "LinkIADisco preferido, luego LinkDriveDisco").
    /// Si la fila no tiene ninguna de las dos, el artículo queda sin foto (placeholder).
    /// El blob de fallback no se mira: la medición encontró 0 artículos con blob.</summary>
    public async Task<IReadOnlyList<FotoRow>> TraerRutasFotoAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT ArtCod, Ruta FROM (
                SELECT ArtCod = RTRIM(F.Codigo),
                       -- IA primero; si esa fila no tiene IA, la foto normal. Vacío = sin foto.
                       Ruta   = COALESCE(
                                    NULLIF(RTRIM(ISNULL(F.LinkIADisco,   '')), ''),
                                    NULLIF(RTRIM(ISNULL(F.LinkDriveDisco, '')), ''),
                                    ''),
                       Fila   = ROW_NUMBER() OVER (PARTITION BY F.Codigo ORDER BY F.ID DESC)
                FROM MARKET.dbo.GoogleDriveFotosArticulos F WITH (NOLOCK)
                WHERE ISNULL(F.Eliminado, 0) = 0
            ) T
            WHERE T.Fila = 1 AND LEN(T.Ruta) > 0;
            """;
        using var cn = _db.CrearMarket();
        return (await cn.QueryAsync<FotoRow>(new CommandDefinition(sql, commandTimeout: 120, cancellationToken: ct))).ToList();
    }

    /// <summary>MARKET: overrides editoriales. La tabla puede no existir todavía (el catálogo funciona
    /// igual sin ella, sólo con títulos derivados), así que un error acá no rompe el sitio.</summary>
    public async Task<IReadOnlyList<OverrideRow>> TraerOverridesAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT ArtCod = RTRIM(ARTCOD), NombreComercial, Marketing = DescripcionMarketing,
                   Destacado, OcultarManual
            FROM MARKET.dbo.CatalogoArticulo WITH (NOLOCK)
            WHERE Eliminado = 0;
            """;
        try
        {
            using var cn = _db.CrearMarket();
            return (await cn.QueryAsync<OverrideRow>(new CommandDefinition(sql, commandTimeout: 30, cancellationToken: ct))).ToList();
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "CatalogoArticulo no disponible; se sigue sin overrides editoriales.");
            return Array.Empty<OverrideRow>();
        }
    }

    /// <summary>MARKET: los tramos oficiales de combo (unidades y precio total), de la grilla de
    /// márgenes. Tabla chica (unas pocas decenas de filas): se trae entera, sin lotes.</summary>
    public async Task<IReadOnlyList<ComboTierRow>> TraerComboTiersAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT Cantidad = N, Total
            FROM MARKET.dbo.PruebaCombos WITH (NOLOCK)
            WHERE Activo = 1 AND Eliminado = 0
            GROUP BY N, Total;
            """;
        using var cn = _db.CrearMarket();
        return (await cn.QueryAsync<ComboTierRow>(new CommandDefinition(sql, commandTimeout: 30, cancellationToken: ct))).ToList();
    }

    /// <summary>MARKET: persiste las filas BASE en dbo.Catalogo. Estrategia: bulk-copy a una tabla
    /// temporal + un solo MERGE (atómico) — nada de un INSERT por fila. El MERGE toca SÓLO las columnas
    /// base; deja intactas las de ficha (stock/ventas/costo, que se llenan a demanda). Lo que ya no está
    /// en el universo se marca Eliminado = 1 (nunca DELETE físico, convención MARKET).</summary>
    public async Task GuardarBaseAsync(IReadOnlyList<CatalogoFilaBase> filas, CancellationToken ct = default)
    {
        // Sin filas no se hace nada: NO se vacía la tabla. Un universo vacío es casi siempre un fallo de
        // la consulta de mapeo, no que se cerraron todos los locales; mejor conservar lo último bueno.
        if (filas.Count == 0) return;

        using var cn = _db.CrearMarket();
        await cn.OpenAsync(ct);

        // 1) Tabla temporal con las columnas base (viven mientras dure la conexión).
        const string crearStage = """
            CREATE TABLE #stage (
                Codigo               varchar(20)   NOT NULL PRIMARY KEY,
                Publicado            bit           NOT NULL,
                Slug                 varchar(200)      NULL,
                Titulo               nvarchar(200)     NULL,
                Descripcion          nvarchar(400)     NULL,
                Rubro                nvarchar(60)      NULL,
                Genero               nvarchar(60)      NULL,
                Prenda               nvarchar(60)      NULL,
                PrecioVenta          decimal(18,2)     NULL,
                PrecioCompra         decimal(18,2)     NULL,
                Combo                nvarchar(50)      NULL,
                EnLuro               bit           NOT NULL,
                EnPeralta            bit           NOT NULL,
                EnDeposito           bit           NOT NULL,
                TallesCsv            nvarchar(400)     NULL,
                ColoresCsv           nvarchar(800)     NULL,
                TieneFoto            bit           NOT NULL,
                FotoPrincipalVersion varchar(40)       NULL,
                FotosJson            nvarchar(max)     NULL,
                Proveedor            nvarchar(80)      NULL,
                Temporada            nvarchar(80)      NULL,
                Marca                nvarchar(80)      NULL,
                TextoBusqueda        nvarchar(600)     NULL
            );
            """;
        await cn.ExecuteAsync(new CommandDefinition(crearStage, cancellationToken: ct));

        // 2) Bulk-copy de las filas a #stage.
        using var tabla = ArmarDataTable(filas);
        using (var bulk = new SqlBulkCopy(cn) { DestinationTableName = "#stage", BulkCopyTimeout = 120 })
        {
            foreach (DataColumn c in tabla.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
            await bulk.WriteToServerAsync(tabla, ct);
        }

        // 3) MERGE: update base (revive Eliminado=0), insert nuevos, marca Eliminado=1 los que ya no están.
        const string merge = """
            MERGE dbo.Catalogo AS T
            USING #stage AS S ON T.Codigo = S.Codigo
            WHEN MATCHED THEN UPDATE SET
                Eliminado = 0, Publicado = S.Publicado, Slug = S.Slug, Titulo = S.Titulo,
                Descripcion = S.Descripcion, Rubro = S.Rubro, Genero = S.Genero, Prenda = S.Prenda,
                PrecioVenta = S.PrecioVenta, PrecioCompra = S.PrecioCompra, Combo = S.Combo,
                EnLuro = S.EnLuro, EnPeralta = S.EnPeralta, EnDeposito = S.EnDeposito,
                TallesCsv = S.TallesCsv, ColoresCsv = S.ColoresCsv,
                TieneFoto = S.TieneFoto, FotoPrincipalVersion = S.FotoPrincipalVersion, FotosJson = S.FotosJson,
                Proveedor = S.Proveedor, Temporada = S.Temporada, Marca = S.Marca,
                TextoBusqueda = S.TextoBusqueda
            WHEN NOT MATCHED BY TARGET THEN INSERT
                (Codigo, Publicado, Eliminado, Slug, Titulo, Descripcion, Rubro, Genero, Prenda,
                 PrecioVenta, PrecioCompra, Combo, EnLuro, EnPeralta, EnDeposito, TallesCsv, ColoresCsv,
                 TieneFoto, FotoPrincipalVersion, FotosJson, Proveedor, Temporada, Marca, TextoBusqueda)
                VALUES
                (S.Codigo, S.Publicado, 0, S.Slug, S.Titulo, S.Descripcion, S.Rubro, S.Genero, S.Prenda,
                 S.PrecioVenta, S.PrecioCompra, S.Combo, S.EnLuro, S.EnPeralta, S.EnDeposito, S.TallesCsv, S.ColoresCsv,
                 S.TieneFoto, S.FotoPrincipalVersion, S.FotosJson, S.Proveedor, S.Temporada, S.Marca, S.TextoBusqueda)
            WHEN NOT MATCHED BY SOURCE AND T.Eliminado = 0 THEN UPDATE SET Eliminado = 1;
            """;
        await cn.ExecuteAsync(new CommandDefinition(merge, commandTimeout: 120, cancellationToken: ct));
    }

    /// <summary>MARKET: lee las filas base de dbo.Catalogo. El público pide soloPublicados=true (subset
    /// seguro); el interno, false (todo el universo). La tabla es chica e indexada: traer las ~569
    /// publicadas es una lectura local barata (el modelo tabla-como-caché: la tabla ES el caché).</summary>
    public async Task<IReadOnlyList<CatalogoFilaLeida>> LeerBaseAsync(bool soloPublicados, CancellationToken ct = default)
    {
        var filtro = soloPublicados ? "AND Publicado = 1" : "";
        var sql = $"""
            SELECT Codigo, Publicado, Slug, Titulo, Descripcion, Rubro, Genero, Prenda,
                   PrecioVenta, PrecioCompra, Combo, EnLuro, EnPeralta, EnDeposito,
                   TallesCsv, ColoresCsv, TieneFoto, FotoPrincipalVersion, FotosJson,
                   Proveedor, Temporada, Marca, Anio, FechaAlta, StockTotal, TopVentas, TextoBusqueda
            FROM MARKET.dbo.Catalogo WITH (NOLOCK)
            WHERE Eliminado = 0 {filtro};
            """;
        using var cn = _db.CrearMarket();
        return (await cn.QueryAsync<CatalogoFilaLeida>(new CommandDefinition(sql, commandTimeout: 60, cancellationToken: ct))).ToList();
    }

    /// <summary>MARKET: ruta de la foto principal de un artículo, sacada de FotosJson ($[0].link). Con
    /// soloPublicado=true sólo la devuelve si Publicado=1 (el endpoint público no sirve fotos de artículos
    /// que el catálogo no muestra). Es un lookup por PK: barato, sólo se dispara en cache-miss del WebP.</summary>
    public async Task<string?> LeerRutaFotoAsync(string codigo, bool soloPublicado, CancellationToken ct = default)
    {
        var filtro = soloPublicado ? "AND Publicado = 1" : "";
        var sql = $"""
            SELECT JSON_VALUE(FotosJson, '$[0].link')
            FROM MARKET.dbo.Catalogo WITH (NOLOCK)
            WHERE Codigo = @codigo AND Eliminado = 0 {filtro};
            """;
        using var cn = _db.CrearMarket();
        var ruta = await cn.ExecuteScalarAsync<string?>(new CommandDefinition(sql, new { codigo }, commandTimeout: 30, cancellationToken: ct));
        return string.IsNullOrWhiteSpace(ruta) ? null : ruta;
    }

    // DataTable con el mismo orden/nombres que #stage. Los nullables van como DBNull; los bits siempre
    // con valor. SqlBulkCopy mapea por nombre (ColumnMappings), no por posición, pero se respeta igual.
    private static DataTable ArmarDataTable(IReadOnlyList<CatalogoFilaBase> filas)
    {
        var t = new DataTable();
        t.Columns.Add("Codigo", typeof(string));
        t.Columns.Add("Publicado", typeof(bool));
        t.Columns.Add("Slug", typeof(string));
        t.Columns.Add("Titulo", typeof(string));
        t.Columns.Add("Descripcion", typeof(string));
        t.Columns.Add("Rubro", typeof(string));
        t.Columns.Add("Genero", typeof(string));
        t.Columns.Add("Prenda", typeof(string));
        t.Columns.Add("PrecioVenta", typeof(decimal));
        t.Columns.Add("PrecioCompra", typeof(decimal));
        t.Columns.Add("Combo", typeof(string));
        t.Columns.Add("EnLuro", typeof(bool));
        t.Columns.Add("EnPeralta", typeof(bool));
        t.Columns.Add("EnDeposito", typeof(bool));
        t.Columns.Add("TallesCsv", typeof(string));
        t.Columns.Add("ColoresCsv", typeof(string));
        t.Columns.Add("TieneFoto", typeof(bool));
        t.Columns.Add("FotoPrincipalVersion", typeof(string));
        t.Columns.Add("FotosJson", typeof(string));
        t.Columns.Add("Proveedor", typeof(string));
        t.Columns.Add("Temporada", typeof(string));
        t.Columns.Add("Marca", typeof(string));
        t.Columns.Add("TextoBusqueda", typeof(string));

        static object N(object? v) => v ?? DBNull.Value;
        foreach (var f in filas)
            t.Rows.Add(
                f.Codigo, f.Publicado, N(f.Slug), N(f.Titulo), N(f.Descripcion),
                N(f.Rubro), N(f.Genero), N(f.Prenda),
                N(f.PrecioVenta), N(f.PrecioCompra), N(f.Combo),
                f.EnLuro, f.EnPeralta, f.EnDeposito,
                N(f.TallesCsv), N(f.ColoresCsv),
                f.TieneFoto, N(f.FotoPrincipalVersion), N(f.FotosJson),
                N(f.Proveedor), N(f.Temporada), N(f.Marca),
                N(f.TextoBusqueda));
        return t;
    }

    private async Task<IReadOnlyList<T>> PorLotesAsync<T>(
        string sql, IReadOnlyCollection<string> codigos, bool dragon, CancellationToken ct)
    {
        if (codigos.Count == 0) return Array.Empty<T>();

        var resultado = new List<T>(codigos.Count);
        using var cn = dragon ? _db.CrearDragon() : _db.CrearMarket();

        foreach (var lote in codigos.Chunk(TamanioLote))
        {
            var filas = await cn.QueryAsync<T>(new CommandDefinition(
                sql, new { codigos = lote }, commandTimeout: 120, cancellationToken: ct));
            resultado.AddRange(filas);
        }
        return resultado;
    }
}
