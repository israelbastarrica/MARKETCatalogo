/* ============================================================================
   MARKET Catálogo — override de visibilidad de 3 estados.
   Reemplaza la columna OcultarManual (bit: ocultar sí/no) por VisibilidadManual
   (varchar: 'auto' | 'mostrar' | 'ocultar'), para poder también FORZAR mostrar
   un artículo de cualquier rubro (no solo Indumentaria) y que el rebuild lo
   respete. El MERGE recomputa Publicado:
     'ocultar' -> 0 | 'mostrar' -> 1 | 'auto' -> criterio objetivo (PublicadoBase)

   Migra los datos: OcultarManual = 1 -> 'ocultar'; resto -> 'auto'.

   Script ADITIVO/idempotente en lo que agrega; dropea OcultarManual al final.
   Correr ANTES de desplegar el código nuevo (el MERGE ya usa VisibilidadManual).
   ============================================================================ */

USE MARKET;
GO

-- 1) Nueva columna (default 'auto').
IF COL_LENGTH('dbo.Catalogo', 'VisibilidadManual') IS NULL
    ALTER TABLE dbo.Catalogo ADD VisibilidadManual varchar(10) NOT NULL
        CONSTRAINT DF_Catalogo_Visib DEFAULT ('auto');
GO

-- 2) Backfill desde OcultarManual (si todavía existe).
IF COL_LENGTH('dbo.Catalogo', 'OcultarManual') IS NOT NULL
BEGIN
    UPDATE dbo.Catalogo
       SET VisibilidadManual = CASE WHEN OcultarManual = 1 THEN 'ocultar' ELSE 'auto' END;
END
GO

-- 3) Dropear OcultarManual (antes su default constraint, si lo tiene).
IF COL_LENGTH('dbo.Catalogo', 'OcultarManual') IS NOT NULL
BEGIN
    DECLARE @df sysname;
    SELECT @df = dc.name
    FROM sys.default_constraints dc
    JOIN sys.columns col ON col.object_id = dc.parent_object_id AND col.column_id = dc.parent_column_id
    WHERE dc.parent_object_id = OBJECT_ID('dbo.Catalogo') AND col.name = 'OcultarManual';
    IF @df IS NOT NULL EXEC('ALTER TABLE dbo.Catalogo DROP CONSTRAINT ' + @df);
    ALTER TABLE dbo.Catalogo DROP COLUMN OcultarManual;
END
GO

PRINT 'dbo.Catalogo: visibilidad manual en VisibilidadManual (auto/mostrar/ocultar).';
GO
