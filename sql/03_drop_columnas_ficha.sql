/* ============================================================================
   MARKET Catálogo — depuración de dbo.Catalogo.
   Elimina las columnas que quedaban 100% NULL: los datos de FICHA (stock,
   ventas, características, ubicaciones) se consultan EN VIVO a Dragon/MARKET al
   abrir el artículo, no se materializan.

   Script ADITIVO e IDEMPOTENTE (se puede correr varias veces).

   ORDEN DE DEPLOY: primero desplegar el código nuevo (que ya NO lee estas
   columnas en LeerBaseAsync) y DESPUÉS correr este script. Si se corre antes,
   la versión vieja del sitio tira "invalid column name" al leer la base.
   ============================================================================ */

USE MARKET;
GO

-- El default con nombre de TopVentas hay que soltarlo antes de dropear la columna.
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_Catalogo_TopVentas')
    ALTER TABLE dbo.Catalogo DROP CONSTRAINT DF_Catalogo_TopVentas;
GO

-- NOTA: Anio NO se dropea — se re-incorporó (poblada con ART.ANO) para el filtro por año de la grilla.
DECLARE @cols TABLE (nombre sysname);
INSERT INTO @cols (nombre) VALUES
    -- Grupo 2: se leían en LeerBaseAsync (ya removidas del SELECT) pero nunca se usaban.
    ('FechaAlta'), ('StockTotal'), ('TopVentas'),
    -- Grupo 1: FICHA reservadas, nunca las tocó el código.
    ('Facturado'), ('CostoPeriodo'), ('StockLuro'), ('StockPeralta'), ('StockDeposito'),
    ('EnTransito'), ('StockDetalleJson'), ('VentaPromSem'), ('VentasSemCsv'), ('Vendido'),
    ('Comprado'), ('PrecioInicial'), ('Forzada'), ('UltimaVenta'), ('PrimeraVenta'),
    ('UbicacionesJson'), ('FichaActualizada');

DECLARE @sql nvarchar(max) = N'';
SELECT @sql = @sql + N'ALTER TABLE dbo.Catalogo DROP COLUMN ' + QUOTENAME(c.nombre) + N';' + CHAR(10)
FROM @cols c
WHERE COL_LENGTH('dbo.Catalogo', c.nombre) IS NOT NULL;   -- sólo las que todavía existen

IF @sql <> N''
BEGIN
    EXEC sys.sp_executesql @sql;
    PRINT 'Columnas de ficha eliminadas de dbo.Catalogo.';
END
ELSE
    PRINT 'Nada para eliminar: dbo.Catalogo ya está depurada.';
GO
