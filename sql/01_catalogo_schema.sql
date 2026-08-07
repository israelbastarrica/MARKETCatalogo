/* ============================================================================
   MARKET Catálogo — esquema mínimo.
   Base: MARKET. Script ADITIVO e IDEMPOTENTE.

   PRINCIPIO: acá vive SOLO lo que no existe en ninguna otra parte.
   Ni una columna de estas tablas duplica algo de Dragon o de los mapeos. Todo lo
   demás (descripción, rubro, género, familia, precio, combo, locales, talles y
   colores) se lee EN VIVO y se cachea con OutputCache.

   El por qué de esta decisión, con los tiempos medidos, en docs/DECISION-TABLAS.md.
   Si algún día el catálogo crece ~5x, el diseño materializado (5 tablas + job de
   refresh) está documentado en docs/CATALOGO-SYNC.md como camino de escalamiento.
   ============================================================================ */

USE MARKET;
GO

/* ----------------------------------------------------------------------------
   1) CatalogoArticulo — overrides EDITORIALES, tabla RALA.

   Arranca VACÍA y solo tiene fila para los artículos que alguien editó a mano.
   No es un espejo del catálogo: es el lugar donde se guardan las decisiones
   humanas que Dragon no puede contener. El sitio hace LEFT JOIN y trata la
   ausencia de fila como "sin overrides".

   No lleva Slug: se deriva del ARTCOD + el título, y la ruta resuelve extrayendo
   el ARTCOD del final del slug. Determinístico, sin almacenar nada y sin lookup.
   ---------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.CatalogoArticulo', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CatalogoArticulo
    (
        ARTCOD               varchar(20)   NOT NULL,

        -- Título de vidriera. ART.ARTDES no sirve para el público: dice cosas como
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


/* ----------------------------------------------------------------------------
   2) CatalogoTalles — orden y agrupación de talles.

   Esto NO se puede derivar de nada: alfabéticamente 'L' va antes que 'M' y '10'
   antes que '2'. Son 53 valores de familias incompatibles entre sí; se cargan a
   mano una vez (seed abajo) y queda resuelto.
   Un artículo no mezcla grupos: o es S/M/L, o es 36/38/40.
   ---------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.CatalogoTalles', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CatalogoTalles
    (
        Talle    varchar(20) NOT NULL,  -- tal cual viene en COMB.TALLE
        Grupo    varchar(20) NOT NULL,  -- SIN_TALLE | LETRA | NINO | ADULTO | LENCERIA | REVISAR
        Orden    int         NOT NULL,
        Etiqueta varchar(20)     NULL,  -- cómo se muestra al público (NULL = igual que Talle)
        CONSTRAINT PK_CatalogoTalles PRIMARY KEY CLUSTERED (Talle)
    );
END
GO


/* ============================================================================
   SEED de CatalogoTalles — los 53 valores medidos en el catálogo real.
   Entre paréntesis, artículos que usan cada talle (docs/MEDICION.md §4).
   ============================================================================ */
MERGE dbo.CatalogoTalles AS d
USING (VALUES
    -- SIN_TALLE: son la MAYORÍA. En la ficha no se muestra ningún chip de talle.
    (''    , 'SIN_TALLE', 0, NULL),          -- (228)
    ('ST'  , 'SIN_TALLE', 0, NULL),          -- (835) el más común de todos
    ('U'   , 'SIN_TALLE', 0, 'Único'),       -- (180)
    ('X'   , 'SIN_TALLE', 0, NULL),          -- (1)

    -- LETRA
    ('XS'  , 'LETRA', 100, NULL),            -- (1)
    ('S'   , 'LETRA', 101, NULL),            -- (287)
    ('SM'  , 'LETRA', 102, 'S/M'),           -- (17)
    ('M'   , 'LETRA', 103, NULL),            -- (315)
    ('L'   , 'LETRA', 104, NULL),            -- (323)
    ('LXL' , 'LETRA', 105, 'L/XL'),          -- (18)
    ('XL'  , 'LETRA', 106, NULL),            -- (309)
    ('2XL' , 'LETRA', 107, NULL),            -- (195)
    ('3XL' , 'LETRA', 108, NULL),            -- (76)
    ('4XL' , 'LETRA', 109, NULL),            -- (24)
    ('5XL' , 'LETRA', 110, NULL),            -- (17)
    ('6XL' , 'LETRA', 111, NULL),            -- (4)
    ('7XL' , 'LETRA', 112, NULL),            -- (2)

    -- NINO (numéricos chicos, con y sin cero adelante)
    ('01'  , 'NINO', 200, '1'),              -- (1)
    ('02'  , 'NINO', 201, '2'),              -- (2)
    ('03'  , 'NINO', 202, '3'),              -- (1)
    ('04'  , 'NINO', 203, '4'),              -- (27)
    ('5'   , 'NINO', 204, NULL),             -- (1)
    ('06'  , 'NINO', 205, '6'),              -- (148)
    ('07'  , 'NINO', 206, '7'),              -- (1)
    ('08'  , 'NINO', 207, '8'),              -- (147)
    ('10'  , 'NINO', 208, NULL),             -- (153)
    ('11'  , 'NINO', 209, NULL),             -- (1)
    ('12'  , 'NINO', 210, NULL),             -- (153)
    ('14'  , 'NINO', 211, NULL),             -- (151)
    ('16'  , 'NINO', 212, NULL),             -- (98)

    -- ADULTO (numéricos de indumentaria / calzado)
    ('36'  , 'ADULTO', 300, NULL),           -- (2)
    ('38'  , 'ADULTO', 301, NULL),           -- (13)
    ('40'  , 'ADULTO', 302, NULL),           -- (24)
    ('42'  , 'ADULTO', 303, NULL),           -- (25)
    ('44'  , 'ADULTO', 304, NULL),           -- (24)
    ('46'  , 'ADULTO', 305, NULL),           -- (21)
    ('48'  , 'ADULTO', 306, NULL),           -- (20)
    ('50'  , 'ADULTO', 307, NULL),           -- (16)
    ('52'  , 'ADULTO', 308, NULL),           -- (7)
    ('54'  , 'ADULTO', 309, NULL),           -- (5)
    ('56'  , 'ADULTO', 310, NULL),           -- (3)

    -- LENCERIA (talles de corpiño)
    ('80'  , 'LENCERIA', 400, NULL),         -- (1)
    ('85'  , 'LENCERIA', 401, NULL),         -- (4)
    ('90'  , 'LENCERIA', 402, NULL),         -- (7)
    ('95'  , 'LENCERIA', 403, NULL),         -- (6)
    ('100' , 'LENCERIA', 404, NULL),         -- (4)
    ('105' , 'LENCERIA', 405, NULL),         -- (4)
    ('110' , 'LENCERIA', 406, NULL),         -- (2)
    ('114' , 'LENCERIA', 407, NULL),         -- (1)
    ('115' , 'LENCERIA', 408, NULL),         -- (1)
    ('120' , 'LENCERIA', 409, NULL),         -- (1)

    -- REVISAR: no quedó claro a qué familia pertenecen (1 a 5 artículos cada uno).
    -- NO se adivinaron: hay que mirar qué artículos los usan y reclasificarlos.
    ('20'  , 'REVISAR', 900, NULL),          -- (1)
    ('24'  , 'REVISAR', 901, NULL),          -- (5)
    ('25'  , 'REVISAR', 902, NULL)           -- (1)
) AS s (Talle, Grupo, Orden, Etiqueta)
    ON d.Talle = s.Talle
WHEN MATCHED THEN
    UPDATE SET Grupo = s.Grupo, Orden = s.Orden, Etiqueta = s.Etiqueta
WHEN NOT MATCHED BY TARGET THEN
    INSERT (Talle, Grupo, Orden, Etiqueta) VALUES (s.Talle, s.Grupo, s.Orden, s.Etiqueta);
GO

PRINT 'Esquema mínimo del catálogo listo (2 tablas).';
GO
