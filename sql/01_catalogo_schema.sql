/* ============================================================================
   MARKET Catálogo — esquema mínimo.
   Base: MARKET. Script ADITIVO e IDEMPOTENTE.

   PRINCIPIO: acá vive SOLO lo que no existe en ninguna otra parte.
   Ni una columna de estas tablas duplica algo de Dragon o de los mapeos. Todo lo
   demás (descripción, rubro, género, familia, precio, combo, locales, talles y
   colores) se lee de Dragon/MARKET y se materializa en dbo.Catalogo (ver
   sql/02_catalogo_tabla.sql y Catalogo.Aplicacion/Servicios/CatalogoStore).

   NOTA: acá vivía además una tabla CatalogoTalles (orden/etiqueta de 53 talles).
   Quedó OBSOLETA: el orden y las etiquetas de talles ahora viven en código
   (Catalogo.Aplicacion/Dominio/Talles.cs) y ningún query la lee. Se retiró del
   esquema; sql/03_migracion_descripcion.sql la dropea en las bases donde exista.
   ============================================================================ */

USE MARKET;
GO

/* ----------------------------------------------------------------------------
   CatalogoArticulo — overrides EDITORIALES, tabla RALA.

   Arranca VACÍA y solo tiene fila para los artículos que alguien editó a mano.
   No es un espejo del catálogo: es el lugar donde se guardan las decisiones
   humanas que Dragon no puede contener. El sitio hace LEFT JOIN y trata la
   ausencia de fila como "sin overrides".

   Es la ÚNICA tabla donde la app ESCRIBE (OcultarManual: bajar/subir un artículo
   del catálogo público desde la ficha interna). Nunca se toca Dragon ni logística.

   No lleva Slug: se deriva del ARTCOD + el nombre, y la ruta resuelve extrayendo
   el ARTCOD del final del slug. Determinístico, sin almacenar nada y sin lookup.
   ---------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.CatalogoArticulo', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CatalogoArticulo
    (
        ARTCOD               varchar(20)   NOT NULL,

        -- Nombre de vidriera. ART.ARTDES no sirve para el público: dice cosas como
        -- "PALAZ DARLON MICRORIB DO VIVO" o "MEDIA TERM C/PIEL 1/3 CAÑA EST ART 9400".
        NombreComercial      varchar(200)      NULL,
        DescripcionMarketing varchar(1000)     NULL,

        -- Orden en el home / secciones destacadas. 0 = no destacado.
        Destacado            int           NOT NULL CONSTRAINT DF_CatArt_Destacado DEFAULT (0),

        -- Bajar un artículo del sitio a mano (foto mala, prenda que no se quiere mostrar)
        -- sin tocar nada en Dragon ni en los mapeos.
        OcultarManual        bit           NOT NULL CONSTRAINT DF_CatArt_Ocultar   DEFAULT (0),

        Eliminado            bit           NOT NULL CONSTRAINT DF_CatArt_Elim      DEFAULT (0),
        Auditoria            varchar(200)      NULL,  -- 'Acción | origen | fecha' (convención MARKET)

        CONSTRAINT PK_CatalogoArticulo PRIMARY KEY CLUSTERED (ARTCOD)
    );
END
GO

-- Los destacados del home se piden por separado y son pocos.
IF INDEXPROPERTY(OBJECT_ID('dbo.CatalogoArticulo'), 'IX_CatArt_Destacado', 'IndexID') IS NULL
    CREATE INDEX IX_CatArt_Destacado ON dbo.CatalogoArticulo (Destacado)
        WHERE Destacado > 0;
GO

PRINT 'Esquema mínimo del catálogo listo (1 tabla: CatalogoArticulo).';
GO
