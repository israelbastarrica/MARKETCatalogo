/* ============================================================================
   MARKET Catálogo — combo en columnas.
   Reemplaza la columna Combo (string CLASIFART, ej '2X20000') por dos columnas
   numéricas ComboCantidad / ComboTotal, para poder filtrar/facetear el combo en
   SQL. El rebuild las puebla parseando CLASIFART.

   Script ADITIVO e IDEMPOTENTE. Correr ANTES de desplegar el código nuevo (el
   MERGE del rebuild ya escribe estas columnas).
   ============================================================================ */

USE MARKET;
GO

IF COL_LENGTH('dbo.Catalogo', 'ComboCantidad') IS NULL
    ALTER TABLE dbo.Catalogo ADD ComboCantidad int NULL;
GO

IF COL_LENGTH('dbo.Catalogo', 'ComboTotal') IS NULL
    ALTER TABLE dbo.Catalogo ADD ComboTotal int NULL;
GO

-- Ya no se usa: el combo vive en ComboCantidad/ComboTotal.
IF COL_LENGTH('dbo.Catalogo', 'Combo') IS NOT NULL
    ALTER TABLE dbo.Catalogo DROP COLUMN Combo;
GO

PRINT 'dbo.Catalogo: combo en ComboCantidad/ComboTotal.';
GO
