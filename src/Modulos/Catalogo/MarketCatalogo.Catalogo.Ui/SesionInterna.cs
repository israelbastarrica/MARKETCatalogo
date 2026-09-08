using Microsoft.AspNetCore.Components.Authorization;

namespace MarketCatalogo.Catalogo.Ui;

/// <summary>
/// ¿El que está mirando es staff/proveedor logueado y aprobado? Si lo es, las páginas PÚBLICAS del
/// catálogo lo derivan a su equivalente interna: una vez adentro, nada tiene que devolverlo a la
/// vista del visitante.
/// </summary>
/// <remarks>
/// El claim va literal ("estado" = "ok") porque Catalogo.Ui referencia SÓLO Catalogo.Contratos y no
/// conoce el módulo Auth — el mismo motivo por el que <c>FichaInterna</c> escribe la política como
/// <c>"Interno"</c> en vez de usar <c>PoliticasAuth.Interno</c>. Si esas constantes cambian, cambian acá.
/// </remarks>
public static class SesionInterna
{
    private const string ClaimEstado = "estado";
    private const string EstadoOk = "ok";

    public static async Task<bool> EsInternoAsync(Task<AuthenticationState>? estado)
    {
        if (estado is null) return false;
        return (await estado).User.HasClaim(ClaimEstado, EstadoOk);
    }
}
