using System.Security.Claims;
using MarketCatalogo.Auth.Contratos;
using MarketCatalogo.Catalogo.Contratos.Interno;

namespace MarketCatalogo.Web.Endpoints;

/// <summary>
/// Endpoints de acciones del catálogo INTERNO. Hoy sólo una: ocultar/mostrar un artículo del catálogo
/// público (la ÚNICA escritura de la app). Es un POST de formulario de página completa (SSR, sin JS),
/// gateado por la política "Interno". La auditoría registra quién lo hizo (mail o usuario del claim).
/// </summary>
public static class InternoEndpoint
{
    public static void MapInterno(this WebApplication app)
    {
        app.MapPost("/interno/visibilidad", async (HttpContext ctx, ICatalogoInternoConsulta interno) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var codigo = (form["codigo"].ToString() ?? "").Trim();
            var ocultar = form["accion"].ToString().Equals("ocultar", StringComparison.OrdinalIgnoreCase);
            var volver = form["volver"].ToString();
            if (string.IsNullOrWhiteSpace(volver) || !volver.StartsWith('/') || volver.StartsWith("//"))
                volver = "/interno";

            if (codigo.Length > 0)
            {
                var origen = ctx.User.FindFirst(ClaimTypes.Email)?.Value
                             ?? ctx.User.Identity?.Name ?? "interno";
                await interno.CambiarVisibilidadAsync(codigo, ocultar, origen);
            }
            return Results.Redirect(volver);
        })
        .RequireAuthorization(PoliticasAuth.Interno)
        .DisableAntiforgery();

        // Bloquear/desbloquear un artículo para reposición (RepoArticulosBloqueados en MARKET). POST de
        // formulario, gateado por "Interno". Audita quién lo hizo (mail o usuario del claim).
        app.MapPost("/interno/bloqueo", async (HttpContext ctx, ICatalogoInternoConsulta interno) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var codigo = (form["codigo"].ToString() ?? "").Trim();
            var bloquear = form["accion"].ToString().Equals("bloquear", StringComparison.OrdinalIgnoreCase);
            var volver = form["volver"].ToString();
            if (string.IsNullOrWhiteSpace(volver) || !volver.StartsWith('/') || volver.StartsWith("//"))
                volver = "/interno";

            if (codigo.Length > 0)
            {
                var origen = ctx.User.FindFirst(ClaimTypes.Email)?.Value
                             ?? ctx.User.Identity?.Name ?? "interno";
                await interno.CambiarBloqueoAsync(codigo, bloquear, origen);
            }
            return Results.Redirect(volver);
        })
        .RequireAuthorization(PoliticasAuth.Interno)
        .DisableAntiforgery();

        // Botón "Actualizar": fuerza el rebuild de la base ahora y vuelve a la grilla.
        app.MapPost("/interno/actualizar", async (HttpContext ctx, ICatalogoInternoConsulta interno) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var volver = form["volver"].ToString();
            if (string.IsNullOrWhiteSpace(volver) || !volver.StartsWith('/') || volver.StartsWith("//"))
                volver = "/interno";
            await interno.RefrescarAsync();
            return Results.Redirect(volver);
        })
        .RequireAuthorization(PoliticasAuth.Interno)
        .DisableAntiforgery();
    }
}
