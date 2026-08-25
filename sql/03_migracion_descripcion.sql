/* ============================================================================
   MARKET Catálogo — migración: unificar Titulo + Descripcion en un solo campo,
   y retirar la tabla obsoleta CatalogoTalles.
   Base: MARKET. IDEMPOTENTE (se puede correr más de una vez sin romper).

   CONTEXTO:
   - dbo.Catalogo tenía dos textos de nombre casi iguales: Titulo (nombre de
     vidriera, derivado/override) y Descripcion (ART.ARTDES crudo). Se unifican en
     UNO solo, llamado Descripcion, con el contenido del viejo Titulo (el lindo).
     El ARTDES crudo NO se pierde para buscar: sigue yendo a TextoBusqueda en cada
     rebuild. Esta migración deja la tabla consistente sin esperar al próximo rebuild.
   - CatalogoTalles quedó obsoleta (el orden/etiqueta de talles vive en Talles.cs).
   ============================================================================ */

USE MARKET;
GO

/* 1) Descripcion pasa a tener el contenido del viejo Titulo, y se dropea Titulo.
      Sólo corre si Titulo todavía existe (idempotente). */
IF COL_LENGTH('dbo.Catalogo', 'Titulo') IS NOT NULL
BEGIN
    UPDATE dbo.Catalogo
        SET Descripcion = Titulo
        WHERE Titulo IS NOT NULL AND (Descripcion IS NULL OR Descripcion <> Titulo);

    ALTER TABLE dbo.Catalogo DROP COLUMN Titulo;

    PRINT 'dbo.Catalogo: columna Titulo unificada en Descripcion y eliminada.';
END
ELSE
    PRINT 'dbo.Catalogo: Titulo ya no existe, nada que migrar.';
GO

/* 2) Retirar la tabla obsoleta de talles (ningún query la lee). */
IF OBJECT_ID('dbo.CatalogoTalles', 'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.CatalogoTalles;
    PRINT 'dbo.CatalogoTalles eliminada (obsoleta).';
END
ELSE
    PRINT 'dbo.CatalogoTalles ya no existe.';
GO
