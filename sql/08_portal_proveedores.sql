/* =============================================================================================
   PORTAL DE PROVEEDORES — tablas propias del catálogo.

   Por qué existen, si MarketWeb ya registra en EtiquetasImpresas:
   esa tabla guarda UNA fila por ARTCOD, sin color ni talle (decisión de Israel: no se toca su
   modelo). El tope que pidió es por COMBINACIÓN ART/COLOR/TALLE, así que el detalle fino tiene
   que vivir de este lado. MarketWeb valida que un pedido no supere lo pedido de cada combinación;
   lo que NO puede saber es cuánto imprimió ese proveedor en pedidos ANTERIORES. Eso es esto.

   Aditivo e idempotente. Nunca se borra físicamente (convención MARKET: Eliminado = 1).
   Ruta: C:\Documentos\Programación\VB.Net\MARKETCatalogo\sql\08_portal_proveedores.sql
   ============================================================================================= */

IF OBJECT_ID('dbo.CatalogoProveedorImpresion', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CatalogoProveedorImpresion
    (
        Id            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CatalogoProveedorImpresion PRIMARY KEY,
        CodProveedor  NVARCHAR(20)   NOT NULL,   -- Dragon PROV.CLCOD (UsuariosPC.CodProveedor)
        NroOrden      INT            NOT NULL,   -- ProdOrdenes.NroOrden (el N° visible, no el Id)
        ArtCod        VARCHAR(40)    NOT NULL,
        CodColor      VARCHAR(10)    NOT NULL,   -- como viaja a la etiqueta (2 dígitos)
        Talle         VARCHAR(20)    NOT NULL,   -- TAL CUAL la fuente: sin padding (si no, no coincide)
        Cantidad      INT            NOT NULL,
        Usuario       NVARCHAR(100)  NULL,       -- el login del proveedor que lo pidió
        Fecha         DATETIME       NOT NULL CONSTRAINT DF_CatProvImpr_Fecha DEFAULT (GETDATE()),
        Eliminado     BIT            NOT NULL CONSTRAINT DF_CatProvImpr_Elim  DEFAULT (0)
    );

    -- El tope se calcula leyendo por proveedor+orden en cada pantalla: ese es el índice que importa.
    CREATE INDEX IX_CatProvImpr_Prov_Orden
        ON dbo.CatalogoProveedorImpresion (CodProveedor, NroOrden, Eliminado)
        INCLUDE (ArtCod, CodColor, Talle, Cantidad);
END;
GO

IF OBJECT_ID('dbo.CatalogoProveedorConfig', 'U') IS NULL
BEGIN
    /* La impresora que eligió el proveedor. Define el LENGUAJE y el DPI con el que se genera la
       etiqueta: el ZPL se arma en dots, así que 203 y 300 dan coordenadas distintas y una elegida
       mal sale corrida o cortada. Se guarda una vez y queda. */
    CREATE TABLE dbo.CatalogoProveedorConfig
    (
        CodProveedor  NVARCHAR(20)  NOT NULL CONSTRAINT PK_CatalogoProveedorConfig PRIMARY KEY,
        Impresora     NVARCHAR(60)  NOT NULL,   -- clave del modelo elegido en el combo
        Lenguaje      VARCHAR(10)   NOT NULL CONSTRAINT DF_CatProvCfg_Leng DEFAULT ('ZPL'),
        Dpi           INT           NOT NULL CONSTRAINT DF_CatProvCfg_Dpi  DEFAULT (203),
        Usuario       NVARCHAR(100) NULL,
        Actualizado   DATETIME      NOT NULL CONSTRAINT DF_CatProvCfg_Act  DEFAULT (GETDATE())
    );
END;
GO
