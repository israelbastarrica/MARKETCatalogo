/* ============================================================================
   MARKET Catálogo — filtrado/faceteo/paginado en SQL.
   Prepara dbo.Catalogo para que la grilla (pública e interna) resuelva WHERE +
   OFFSET/FETCH + GROUP BY en la base, en vez de traer todo a memoria:

     · Columnas slug (RubroSlug/GeneroSlug/PrendaSlug): los filtros viajan por
       slug; con la columna materializada se filtra/agrupa sin traducir en C#.
     · Tablas hijas CatalogoTalle / CatalogoColor: talle y color son
       multi-valor. Normalizados se filtran con EXISTS y se facetean con
       GROUP BY (la faceta de talle además respeta el orden de curva por Orden).
       La CSV de la fila se conserva para MOSTRAR (card/ficha); las hijas son
       sólo para consultar.
     · Índices para los WHERE/GROUP BY frecuentes.

   Las pobla el rebuild (CatalogoStore + CatalogoRepositorio.GuardarBaseAsync)
   dentro de la misma transacción que el MERGE de la base.

   Script ADITIVO e IDEMPOTENTE.
   ============================================================================ */

USE MARKET;
GO

-- ===== Columnas slug en dbo.Catalogo =====
IF COL_LENGTH('dbo.Catalogo', 'RubroSlug') IS NULL
    ALTER TABLE dbo.Catalogo ADD RubroSlug varchar(80) NULL;
GO
IF COL_LENGTH('dbo.Catalogo', 'GeneroSlug') IS NULL
    ALTER TABLE dbo.Catalogo ADD GeneroSlug varchar(80) NULL;
GO
IF COL_LENGTH('dbo.Catalogo', 'PrendaSlug') IS NULL
    ALTER TABLE dbo.Catalogo ADD PrendaSlug varchar(80) NULL;
GO

-- ===== Tablas hijas (multi-valor) =====
IF OBJECT_ID('dbo.CatalogoTalle', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CatalogoTalle
    (
        Codigo varchar(20)  NOT NULL,   -- = dbo.Catalogo.Codigo
        Talle  nvarchar(40) NOT NULL,   -- etiqueta mostrable (la de la CSV)
        Orden  int          NOT NULL,   -- orden de curva (Talles.cs / DCTALLE) para ordenar la faceta
        CONSTRAINT PK_CatalogoTalle PRIMARY KEY CLUSTERED (Codigo, Talle)
    );
    CREATE INDEX IX_CatalogoTalle_Talle ON dbo.CatalogoTalle (Talle) INCLUDE (Codigo, Orden);
END
GO

IF OBJECT_ID('dbo.CatalogoColor', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CatalogoColor
    (
        Codigo varchar(20)  NOT NULL,
        Color  nvarchar(80) NOT NULL,
        CONSTRAINT PK_CatalogoColor PRIMARY KEY CLUSTERED (Codigo, Color)
    );
    CREATE INDEX IX_CatalogoColor_Color ON dbo.CatalogoColor (Color) INCLUDE (Codigo);
END
GO

-- ===== Índices para los filtros/orden de la grilla =====
IF INDEXPROPERTY(OBJECT_ID('dbo.Catalogo'), 'IX_Catalogo_TaxonomiaSlug', 'IndexID') IS NULL
    CREATE INDEX IX_Catalogo_TaxonomiaSlug ON dbo.Catalogo (RubroSlug, GeneroSlug, PrendaSlug);
GO
IF INDEXPROPERTY(OBJECT_ID('dbo.Catalogo'), 'IX_Catalogo_Combo', 'IndexID') IS NULL
    CREATE INDEX IX_Catalogo_Combo ON dbo.Catalogo (ComboCantidad, ComboTotal);
GO
IF INDEXPROPERTY(OBJECT_ID('dbo.Catalogo'), 'IX_Catalogo_Anio', 'IndexID') IS NULL
    CREATE INDEX IX_Catalogo_Anio ON dbo.Catalogo (Anio);
GO

PRINT 'dbo.Catalogo listo para faceteo SQL: slugs + CatalogoTalle/CatalogoColor + indices.';
GO
