/* ============================================================================
   MARKET Catálogo — migración a UNA SOLA TABLA (dbo.Catalogo).
   Base: MARKET. IDEMPOTENTE (se puede correr más de una vez sin romper).

   Lleva una base con el esquema VIEJO al nuevo. Cuatro pasos:
     1) Unificar Titulo + Descripcion en un solo campo Descripcion (contenido del
        viejo Titulo, el lindo). El ARTDES crudo NO se pierde: sigue alimentando
        TextoBusqueda en cada rebuild.
     2) Sumar a dbo.Catalogo las columnas de la decisión humana (OcultarManual,
        Auditoria), que antes vivían en la tabla aparte CatalogoArticulo.
     3) Traer el OcultarManual/Auditoria que ya había en CatalogoArticulo y
        recomputar Publicado; después dropear CatalogoArticulo.
     4) Retirar la tabla obsoleta CatalogoTalles (el orden/etiqueta de talles vive
        en código, Catalogo.Aplicacion/Dominio/Talles.cs).

   IMPORTANTE: correr DESPUÉS de deployar el código nuevo (el viejo referencia la
   columna Titulo y la tabla CatalogoArticulo).
   ============================================================================ */

USE MARKET;
GO

/* 1) Descripcion toma el contenido del viejo Titulo, y se dropea Titulo (si existe). */
IF COL_LENGTH('dbo.Catalogo', 'Titulo') IS NOT NULL
BEGIN
    UPDATE dbo.Catalogo
        SET Descripcion = Titulo
        WHERE Titulo IS NOT NULL AND (Descripcion IS NULL OR Descripcion <> Titulo);

    ALTER TABLE dbo.Catalogo DROP COLUMN Titulo;
    PRINT '1) dbo.Catalogo: Titulo unificado en Descripcion y eliminado.';
END
ELSE
    PRINT '1) dbo.Catalogo: Titulo ya no existe, nada que migrar.';
GO

/* 2) Columnas de la decisión humana en la tabla única (si no existen todavía). */
IF COL_LENGTH('dbo.Catalogo', 'OcultarManual') IS NULL
    ALTER TABLE dbo.Catalogo ADD OcultarManual bit NOT NULL CONSTRAINT DF_Catalogo_Ocultar DEFAULT (0);
GO
IF COL_LENGTH('dbo.Catalogo', 'Auditoria') IS NULL
    ALTER TABLE dbo.Catalogo ADD Auditoria nvarchar(200) NULL;
GO

/* 3) Traer el OcultarManual/Auditoria que ya había en CatalogoArticulo a la tabla única,
      recomputar Publicado con eso, y dropear CatalogoArticulo. */
IF OBJECT_ID('dbo.CatalogoArticulo', 'U') IS NOT NULL
BEGIN
    UPDATE C
        SET C.OcultarManual = CA.OcultarManual,
            C.Auditoria     = CA.Auditoria,
            C.Publicado     = CASE WHEN CA.OcultarManual = 1 THEN 0 ELSE C.Publicado END
        FROM dbo.Catalogo C
        JOIN dbo.CatalogoArticulo CA ON CA.ARTCOD = C.Codigo
        WHERE ISNULL(CA.Eliminado, 0) = 0;

    DROP TABLE dbo.CatalogoArticulo;
    PRINT '3) OcultarManual migrado a dbo.Catalogo y CatalogoArticulo eliminada (una sola tabla).';
END
ELSE
    PRINT '3) CatalogoArticulo ya no existe; nada que migrar de overrides.';
GO

/* 4) Retirar la tabla obsoleta de talles (ningún query la lee). */
IF OBJECT_ID('dbo.CatalogoTalles', 'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.CatalogoTalles;
    PRINT '4) dbo.CatalogoTalles eliminada (obsoleta).';
END
ELSE
    PRINT '4) dbo.CatalogoTalles ya no existe.';
GO

PRINT 'Migración a una sola tabla completa.';
GO
