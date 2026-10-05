/* =============================================================================================
   CONSTITUCIÓN en el catálogo — el bit "está mapeado en ese local", como EnLuro / EnPeralta.

   Constitución (MARKET.dbo.Ubicaciones ID 4, tipo LOCAL) abre el 13/10/2026. Hoy no tiene ningún
   mapeo; cuando empiecen a mapear el salón, el catálogo la tiene que tomar sola: filtro de ubicación
   en el interno, "Locales" en el público y la marca en cards y fichas. Para eso necesita este bit en
   la tabla materializada, igual que los otros dos locales.

   Aditivo e idempotente. Default 0: el código que ya está corriendo no lo conoce y no lo necesita
   (su INSERT nombra las columnas), así que se puede correr ANTES de deployar la versión que lo usa —
   y tiene que ser antes, porque esa versión lo lee.
   Ruta: C:\Documentos\Programación\VB.Net\MARKETCatalogo\sql\09_catalogo_constitucion.sql
   ============================================================================================= */

IF COL_LENGTH('dbo.Catalogo', 'EnConstitucion') IS NULL
BEGIN
    ALTER TABLE dbo.Catalogo
        ADD EnConstitucion bit NOT NULL
            CONSTRAINT DF_Catalogo_EnConstitucion DEFAULT (0);
END;
GO
