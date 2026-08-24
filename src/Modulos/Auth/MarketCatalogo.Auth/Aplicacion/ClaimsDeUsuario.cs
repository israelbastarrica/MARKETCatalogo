using System.Security.Claims;
using MarketCatalogo.Auth.Contratos;
using Microsoft.AspNetCore.Authentication;

namespace MarketCatalogo.Auth.Aplicacion;

/// <summary>
/// Enriquece la identidad (mail de Google, o el usuario del login local) con los claims de MARKET:
/// estado (ok/pendiente/onboarding), perfil, pc y área — resueltos desde <c>UsuariosPC</c>. Corre en cada
/// autenticación; idempotente (no re-agrega si el claim <c>estado</c> ya está). Portado de MARKETweb.
/// </summary>
public sealed class ClaimsDeUsuario : IClaimsTransformation
{
    private readonly IAutenticacion _auth;
    public ClaimsDeUsuario(IAutenticacion auth) => _auth = auth;

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
            return principal;
        if (identity.HasClaim(c => c.Type == PoliticasAuth.ClaimEstado))
            return principal; // ya transformado en este request

        // Login por Google → resuelve por mail. Login LOCAL → por el claim "usuario".
        var email = principal.FindFirst(ClaimTypes.Email)?.Value;
        AccesoResultado acceso;
        if (!string.IsNullOrEmpty(email))
        {
            acceso = await _auth.ResolverAccesoAsync(email);
        }
        else
        {
            var usuario = principal.FindFirst("usuario")?.Value;
            if (string.IsNullOrEmpty(usuario)) return principal;
            acceso = await _auth.ResolverAccesoPorUsuarioAsync(usuario);
        }

        identity.AddClaim(new Claim(PoliticasAuth.ClaimEstado, acceso.Estado));
        if (!string.IsNullOrEmpty(acceso.Perfil)) identity.AddClaim(new Claim(PoliticasAuth.ClaimPerfil, acceso.Perfil));
        if (!string.IsNullOrEmpty(acceso.Pc)) identity.AddClaim(new Claim(PoliticasAuth.ClaimPc, acceso.Pc));
        if (acceso.Area is int area) identity.AddClaim(new Claim(PoliticasAuth.ClaimArea, area.ToString()));

        return principal;
    }
}
