/* ============================================================================
   MARKET Catálogo — filtrado/faceteo/paginado en SQL (tablas hijas).
   Prepara dbo.Catalogo para que la grilla (pública e interna) resuelva WHERE +
   OFFSET/FETCH + GROUP BY en la base, en vez de traer todo a memoria:

     · Tablas hijas CatalogoTalle / CatalogoColor: talle y color son
       multi-valor. Normalizados se filtran con EXISTS y se facetean con
       GROUP BY (la faceta de talle además respeta el orden de curva por Orden).
       Son la ÚNICA fuente de talle/color: la card/ficha reconstruye la lista
       mostrable con STRING_AGG (por eso NO hay TallesCsv/ColoresCsv — ver 06).
     · Índices para los WHERE/GROUP BY de combo y año.

   La taxonomía (rubro/género/prenda) NO lleva columnas slug: el rebuild arma un
   mapa slug→valor en memoria y el servicio traduce el slug de la URL a valor
   antes de consultar (se filtra por Rubro/Genero/Prenda).

   Las tablas hijas las puebla el rebuild (CatalogoStore + GuardarBaseAsync)
   dentro de la misma transacción que el MERGE de la base.

   Script ADITIVO e IDEMPOTENTE. Correr ANTES de desplegar el código nuevo.
   ============================================================================ */

USE MARKET;
GO

-- ===== Tablas hijas (multi-valor) =====
IF OBJECT_ID('dbo.CatalogoTalle', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CatalogoTalle
    (
        Codigo varchar(20)  NOT NULL,   -- = dbo.Catalogo.Codigo
        Talle  nvarchar(40) NOT NULL,   -- etiqueta mostrable
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
IF INDEXPROPERTY(OBJECT_ID('dbo.Catalogo'), 'IX_Catalogo_Combo', 'IndexID') IS NULL
    CREATE INDEX IX_Catalogo_Combo ON dbo.Catalogo (ComboCantidad, ComboTotal);
GO
IF INDEXPROPERTY(OBJECT_ID('dbo.Catalogo'), 'IX_Catalogo_Anio', 'IndexID') IS NULL
    CREATE INDEX IX_Catalogo_Anio ON dbo.Catalogo (Anio);
GO

PRINT 'dbo.Catalogo listo para faceteo SQL: CatalogoTalle/CatalogoColor + indices combo/anio.';
GO
