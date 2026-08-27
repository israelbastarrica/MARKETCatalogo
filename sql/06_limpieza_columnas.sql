/* ============================================================================
   MARKET Catálogo — limpieza de columnas muertas de dbo.Catalogo.
   Dropea:
     · 20 columnas LEGACY de un diseño viejo (stock/ventas/ficha materializada)
       que el código ya no escribe ni lee — están 100% NULL.
     · TallesCsv / ColoresCsv: reemplazadas por las tablas hijas CatalogoTalle /
       CatalogoColor (ver 05). La grilla reconstruye la lista con STRING_AGG.

   DESTRUCTIVO. Correr AL FINAL, cuando ya esté:
     1) corrida la migración 05 (tablas hijas),
     2) desplegado el código nuevo (no lee esas columnas),
     3) hecho al menos un rebuild (las hijas ya pobladas) y verificado.

   Supersede a 03_drop_columnas_ficha.sql (cubre el mismo set y más).
   IDEMPOTENTE: sólo dropea lo que exista; dropea antes el default constraint.
   ============================================================================ */

USE MARKET;
GO

DECLARE @cols TABLE (n sysname);
INSERT INTO @cols (n) VALUES
    ('FechaAlta'), ('StockTotal'), ('TopVentas'), ('Facturado'), ('CostoPeriodo'),
    ('StockLuro'), ('StockPeralta'), ('StockDeposito'), ('EnTransito'), ('StockDetalleJson'),
    ('VentaPromSem'), ('VentasSemCsv'), ('Vendido'), ('Comprado'), ('PrecioInicial'),
    ('Forzada'), ('UltimaVenta'), ('PrimeraVenta'), ('UbicacionesJson'), ('FichaActualizada'),
    ('TallesCsv'), ('ColoresCsv');

DECLARE @c sysname, @sql nvarchar(max);
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT n FROM @cols;
OPEN cur;
FETCH NEXT FROM cur INTO @c;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF COL_LENGTH('dbo.Catalogo', @c) IS NOT NULL
    BEGIN
        -- 1) Si la columna tiene un default constraint, dropearlo primero.
        SET @sql = NULL;
        SELECT @sql = 'ALTER TABLE dbo.Catalogo DROP CONSTRAINT ' + QUOTENAME(dc.name)
        FROM sys.default_constraints dc
        JOIN sys.columns col ON col.object_id = dc.parent_object_id AND col.column_id = dc.parent_column_id
        WHERE dc.parent_object_id = OBJECT_ID('dbo.Catalogo') AND col.name = @c;
        IF @sql IS NOT NULL EXEC sys.sp_executesql @sql;

        -- 2) Dropear la columna.
        SET @sql = 'ALTER TABLE dbo.Catalogo DROP COLUMN ' + QUOTENAME(@c);
        EXEC sys.sp_executesql @sql;
        PRINT 'Dropeada dbo.Catalogo.' + @c;
    END
    FETCH NEXT FROM cur INTO @c;
END
CLOSE cur;
DEALLOCATE cur;
GO

PRINT 'Limpieza de columnas de dbo.Catalogo completa.';
GO
