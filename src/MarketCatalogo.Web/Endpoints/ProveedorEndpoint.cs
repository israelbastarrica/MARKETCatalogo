using System.Text;
using MarketCatalogo.Auth.Contratos;
using MarketCatalogo.Proveedores.Contratos;

namespace MarketCatalogo.Web.Endpoints;

/// <summary>
/// Los POST del portal de proveedores: guardar la impresora y generar las etiquetas. Son formularios de
/// página completa (SSR, sin JS), igual que el login.
///
/// El código de proveedor sale SIEMPRE del claim de la identidad, nunca del formulario ni de la URL: si
/// viniera del cliente, cambiar un número alcanzaría para imprimir etiquetas de otro proveedor.
/// </summary>
public static class ProveedorEndpoint
{
    public static void MapProveedor(this WebApplication app)
    {
        // Qué impresora tiene: define lenguaje y DPI de la etiqueta.
        app.MapPost("/proveedor/impresora", async (HttpContext ctx, IPortalProveedores portal) =>
        {
            var cod = CodProveedor(ctx);
            if (cod is null) return Results.Redirect("/login");

            var form = await ctx.Request.ReadFormAsync();
            var clave = form["impresora"].ToString();
            try
            {
                await portal.GuardarImpresoraAsync(cod, clave, Usuario(ctx));
                return Results.Redirect("/proveedor?msg=" + Uri.EscapeDataString("Listo, guardamos tu impresora."));
            }
            catch (ArgumentException)
            {
                return Results.Redirect("/proveedor?msg=" + Uri.EscapeDataString("Ese modelo de impresora no está en la lista."));
            }
        }).RequireAuthorization(PoliticasAuth.Proveedor);

        // Genera las etiquetas y las devuelve como archivo para mandar a la térmica.
        app.MapPost("/proveedor/orden/{nro:int}/imprimir", async (int nro, HttpContext ctx, IPortalProveedores portal) =>
        {
            var cod = CodProveedor(ctx);
            if (cod is null) return Results.Redirect("/login");

            var form = await ctx.Request.ReadFormAsync();
            var items = new List<PortalItemPedido>();
            // Los campos vienen como "i:{ARTCOD}|{COLOR}|{TALLE}" con la cantidad. Lo que no parsea se
            // ignora en silencio: el servicio revalida todo contra la curva antes de imprimir nada.
            foreach (var campo in form.Keys)
            {
                if (!campo.StartsWith("i:", StringComparison.Ordinal)) continue;
                var partes = campo[2..].Split('|');
                if (partes.Length != 3) continue;
                if (!int.TryParse(form[campo], out var cant) || cant <= 0) continue;
                items.Add(new PortalItemPedido(partes[0], partes[1], partes[2], cant));
            }

            var r = await portal.ImprimirAsync(cod, Usuario(ctx), nro, items);
            if (!r.Ok || string.IsNullOrEmpty(r.Zpl))
                return Results.Redirect($"/proveedor/orden/{nro}?error=" + Uri.EscapeDataString(r.Mensaje ?? "No se pudo generar."));

            // Archivo plano con el ZPL: el proveedor lo manda a su impresora de etiquetas.
            var bytes = Encoding.ASCII.GetBytes(r.Zpl);
            return Results.File(bytes, "application/octet-stream", $"etiquetas-orden-{nro}-{r.Etiquetas}.zpl");
        }).RequireAuthorization(PoliticasAuth.Proveedor);
    }

    private static string? CodProveedor(HttpContext ctx)
    {
        var cod = ctx.User.FindFirst(PoliticasAuth.ClaimCodProveedor)?.Value;
        return string.IsNullOrWhiteSpace(cod) ? null : cod.Trim();
    }

    private static string Usuario(HttpContext ctx)
        => ctx.User.FindFirst("usuario")?.Value ?? ctx.User.Identity?.Name ?? "";
}
