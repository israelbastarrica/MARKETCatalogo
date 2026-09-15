namespace MarketCatalogo.Auth.Contratos;

/// <summary>Resultado de resolver el acceso de una persona contra <c>UsuariosPC</c>.
/// <para><b>Estado</b>: <c>"ok"</c> (aprobado, es staff, entra al interno), <c>"pendiente"</c> (existe pero
/// sin aprobar) u <c>"onboarding"</c> (no hay fila para ese mail/usuario).</para>
/// Perfil/Pc/Area sólo son confiables cuando Estado = "ok".</summary>
public sealed record AccesoResultado(string Estado, string? Perfil, string? Pc, int? Area = null);

/// <summary>La superficie pública del módulo Auth (lo único que el host referencia además de
/// <see cref="PoliticasAuth"/>). Valida credenciales y resuelve el acceso contra <c>UsuariosPC</c>.</summary>
public interface IAutenticacion
{
    /// <summary>Login LOCAL (usuario + contraseña, para quien no tiene cuenta @marketarg.com). Devuelve el
    /// acceso "ok" sólo si el usuario existe, está aprobado y la contraseña verifica; si no, null.</summary>
    Task<AccesoResultado?> ValidarLoginLocalAsync(string usuario, string password, CancellationToken ct = default);

    /// <summary>Resuelve el acceso por mail (login Google).</summary>
    Task<AccesoResultado> ResolverAccesoAsync(string mail, CancellationToken ct = default);

    /// <summary>Resuelve el acceso por nombre de usuario (login local, para la ClaimsTransformation).</summary>
    Task<AccesoResultado> ResolverAccesoPorUsuarioAsync(string usuario, CancellationToken ct = default);
}

/// <summary>Autorización del catálogo. Dos niveles:
/// <list type="bullet">
/// <item><b>Interno</b>: cualquier staff logueado y aprobado (estado = ok). Ve el universo interno completo
/// (depósito, no publicados) y sus filtros. Pero la ficha del detalle que le toca es REDUCIDA (= la pública)
/// salvo que sea de gestión.</item>
/// <item><b>Gestión</b>: perfil ADMIN. Ve la ficha interna COMPLETA (costo, márgenes, ventas, stock por
/// local, órdenes, ubicaciones) y es el único que puede escribir (mostrar/ocultar del público, bloqueo).</item>
/// </list>
/// Cuando haga falta diferenciar por otra área se agregan más políticas / perfiles acá.</summary>
public static class PoliticasAuth
{
    /// <summary>Ver el catálogo interno = estar logueado y aprobado (estado = ok).</summary>
    public const string Interno = "Interno";
    /// <summary>Acceso de GESTIÓN (ficha completa + escrituras) = perfil ADMIN. Carola y Paula ya son ADMIN.</summary>
    public const string Gestion = "Gestion";
    public const string ClaimEstado = "estado";
    public const string ClaimPerfil = "perfil";
    public const string ClaimPc = "pc";
    public const string ClaimArea = "area";
    public const string EstadoOk = "ok";
    /// <summary>Perfil (columna PERFIL de UsuariosPC) que habilita la vista de gestión.</summary>
    public const string PerfilAdmin = "ADMIN";
}

/// <summary>
/// Qué formas de entrar están realmente disponibles. El login con Google solo existe si el server tiene
/// cargadas las credenciales; sin ellas, el esquema ni se registra y desafiarlo tira 500. La pantalla de
/// login usa esto para no ofrecer un botón que no puede funcionar.
/// </summary>
public sealed class OpcionesDeIngreso
{
    public bool GoogleHabilitado { get; init; }
}
