/* ============================================================================
   MARKET Catálogo — tabla materializada dbo.Catalogo.
   Base: MARKET. Script ADITIVO e IDEMPOTENTE.

   Una fila por artículo. Universo = TODO lo mapeado en cualquier ubicación
   (local o depósito). Sirve a los DOS catálogos:
     * Público  -> lee WHERE Publicado = 1 AND Eliminado = 0 (subset seguro).
     * Interno  -> lee todo (gateado por rol en la app).

   Esta tabla ES EL CACHÉ (modelo "tabla como caché" de Israel), NO la fuente de
   verdad: se regenera desde Dragon (ART/taxonomía/precio/combo/curva) + MARKET
   (mapeo/fotos/overrides). Si se borra, se rearma sola. Sin SP: el llenado y el
   refresco los hace C#/Dapper (read-through perezoso + TTL + single-flight),
   ver Catalogo.Aplicacion/Servicios/CatalogoStore.

   Dos grupos de columnas con refresco distinto:
     * BASE (grilla/público) -> se recalcula para TODO el universo en el rebuild
       global (stale-while-revalidate, TTL en config). Son las que se
       filtran/ordenan/muestran en la grilla.
     * FICHA (detalle)       -> se llena A DEMANDA al abrir un artículo, TTL por
       fila (columna FichaActualizada). NULL = nunca calculada; si nadie abre un
       artículo, no corre nada por él.

   El reloj de la BASE NO vive acá (es un DateTime en memoria del servicio, un
   solo timestamp global): materializarlo por fila sería una columna repetida
   1.554 veces sin sentido. El TTL (minutos) va en appsettings, no en la tabla.
   ============================================================================ */

USE MARKET;
GO

IF OBJECT_ID('dbo.Catalogo', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Catalogo
    (
        -- ===== Identidad =====
        Codigo               varchar(20)   NOT NULL,   -- = ART.ARTCOD (ej 'IH093.140')

        -- ===== BASE — se recalcula para TODOS en el rebuild global =====
        -- Publicación / estado
        Publicado            bit           NOT NULL CONSTRAINT DF_Catalogo_Publicado DEFAULT (0),
                                                       -- filtro público: Indumentaria + en algún local +
                                                       -- tiene foto + NO oculto manual. Materializado e indexado.
        Eliminado            bit           NOT NULL CONSTRAINT DF_Catalogo_Eliminado DEFAULT (0),
                                                       -- soft-delete del MERGE (NOT MATCHED BY SOURCE).

        -- Presentación
        Slug                 varchar(200)      NULL,   -- URL canónica: /producto/{slug}
        Descripcion          nvarchar(400)     NULL,   -- nombre de vidriera: override manual o derivado de
                                                       -- ARTDES. Único campo de nombre (el ARTDES crudo ya
                                                       -- no se guarda; sólo alimenta TextoBusqueda).

        -- Taxonomía (descripción directa, un valor — para filtros)
        Rubro                nvarchar(60)      NULL,   -- TIPOART.DESCRIP  (ej 'Indumentaria')
        Genero               nvarchar(60)      NULL,   -- CATEGART.DESCRIP (ej 'Mujer')
        Prenda               nvarchar(60)      NULL,   -- FAMILIA.DESCRIP  (ej 'Chomba')

        -- Precio / combo
        PrecioVenta          decimal(18,2)     NULL,   -- PRECIOAR LISTA1 (1 unidad suelta, trae recargo)
        PrecioCompra         decimal(18,2)     NULL,   -- PRECIOAR LISTA0 (costo unitario — campo interno)
        Combo                nvarchar(50)      NULL,   -- CLASIFART crudo (ej '2X15000'); cant/$u se derivan en C#

        -- Presencia por ubicación (mapeo) — filtros directos
        EnLuro               bit           NOT NULL CONSTRAINT DF_Catalogo_EnLuro     DEFAULT (0),
        EnPeralta            bit           NOT NULL CONSTRAINT DF_Catalogo_EnPeralta  DEFAULT (0),
        EnDeposito           bit           NOT NULL CONSTRAINT DF_Catalogo_EnDeposito DEFAULT (0),

        -- Variantes (de las COMPRAS: PRECOMPRA->REMCOMPRA + fallback CURTALL)
        TallesCsv            nvarchar(400)     NULL,   -- ej 'S,M,L,XL'
        ColoresCsv           nvarchar(800)     NULL,   -- ej 'Negro,Blanco,Único'

        -- Fotos (N por artículo)
        TieneFoto            bit           NOT NULL CONSTRAINT DF_Catalogo_TieneFoto DEFAULT (0),
        FotoPrincipalVersion varchar(40)       NULL,   -- token ?v= de la principal (card, sin parsear JSON)
        FotosJson            nvarchar(max)     NULL,   -- [{orden,tipo,link,version,esPrincipal}] — 0..N fotos

        -- Ficha comercial (de ART)
        Proveedor            nvarchar(80)      NULL,
        Temporada            nvarchar(80)      NULL,
        Marca                nvarchar(80)      NULL,
        Anio                 int               NULL,
        FechaAlta            datetime2         NULL,   -- para el filtro "novedades"

        -- Búsqueda / métricas base
        TextoBusqueda        nvarchar(600)     NULL,   -- normalizado (sin acentos) para ?q=
        StockTotal           decimal(18,2)     NULL,   -- para el filtro "con stock" (materializado en base)
        TopVentas            bit           NOT NULL CONSTRAINT DF_Catalogo_TopVentas DEFAULT (0),
                                                       -- Top N por unidades 30 días (config); filtro + badge

        -- ===== FICHA — a demanda por artículo, TTL por fila. NULL = nunca cargada. =====
        Facturado            decimal(18,2)     NULL,   -- $ vendido en la ventana (precios reales c/desc)
        CostoPeriodo         decimal(18,2)     NULL,   -- COGS de lo vendido (costo histórico)
        StockLuro            decimal(18,2)     NULL,
        StockPeralta         decimal(18,2)     NULL,
        StockDeposito        decimal(18,2)     NULL,
        EnTransito           decimal(18,2)     NULL,
        StockDetalleJson     nvarchar(max)     NULL,   -- stock por talle/color
        VentaPromSem         decimal(18,2)     NULL,   -- promedio semanal (ventana)
        VentasSemCsv         nvarchar(200)     NULL,   -- baldes p/ sparkline, ej '3,5,2,0,1,4,6,2'
        Vendido              decimal(18,2)     NULL,   -- unidades vendidas en la ventana
        Comprado             decimal(18,2)     NULL,   -- unidades ingresadas (REMCOMPRA / enviado a locales)
        PrecioInicial        decimal(18,2)     NULL,
        Forzada              bit               NULL,
        UltimaVenta          datetime2         NULL,
        PrimeraVenta         datetime2         NULL,
        UbicacionesJson      nvarchar(max)     NULL,   -- ubicaciones ACTUALES del mapeo (mueble/sector/fila/pos)
        FichaActualizada     datetime2         NULL,   -- reloj de la ficha (por fila); NULL = nunca calculada

        CONSTRAINT PK_Catalogo PRIMARY KEY CLUSTERED (Codigo)
    );

    -- Índices para los filtros más comunes (tabla chica ~1.554 filas, pero prolijo).
    CREATE INDEX IX_Catalogo_Publicado ON dbo.Catalogo (Publicado, Eliminado);
    CREATE INDEX IX_Catalogo_Taxonomia ON dbo.Catalogo (Rubro, Genero, Prenda);
    CREATE INDEX IX_Catalogo_Ubicacion ON dbo.Catalogo (EnDeposito, EnLuro, EnPeralta);
    CREATE INDEX IX_Catalogo_Slug      ON dbo.Catalogo (Slug);
END
GO

PRINT 'Tabla materializada del catálogo lista: dbo.Catalogo.';
GO
