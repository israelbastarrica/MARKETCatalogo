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
    private const string ClaimPerfil = "perfil";
    private const string PerfilAdmin = "ADMIN";

    public static async Task<bool> EsInternoAsync(Task<AuthenticationState>? estado)
    {
        if (estado is null) return false;
        return (await estado).User.HasClaim(ClaimEstado, EstadoOk);
    }

    /// <summary>
    /// ¿Es GESTIÓN (perfil ADMIN)? Sólo la gestión ve la ficha interna COMPLETA (costo, márgenes, ventas,
    /// stock por local, órdenes, ubicaciones y las acciones de mostrar/ocultar y bloqueo). El resto del staff
    /// logueado ve la ficha REDUCIDA (= la pública), pero con acceso al universo interno completo (depósito,
    /// no publicados) y sus filtros. Espeja la política <c>PoliticasAuth.Gestion</c> del servidor (que es la
    /// barrera real); acá se usa para decidir qué renderizar. Los literales van igual que en EsInternoAsync,
    /// porque Catalogo.Ui referencia sólo Catalogo.Contratos y no conoce el módulo Auth.
    /// </summary>
    public static async Task<bool> EsGestionAsync(Task<AuthenticationState>? estado)
    {
        if (estado is null) return false;
        var user = (await estado).User;
        return user.HasClaim(ClaimEstado, EstadoOk)
            && user.Claims.Any(c => c.Type == ClaimPerfil
                                    && string.Equals(c.Value, PerfilAdmin, StringComparison.OrdinalIgnoreCase));
    }
}
