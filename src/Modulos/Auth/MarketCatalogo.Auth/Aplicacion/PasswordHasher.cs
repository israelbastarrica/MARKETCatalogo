using System.Security.Cryptography;

namespace MarketCatalogo.Auth.Aplicacion;

/// <summary>
/// Hash de contraseñas para el login LOCAL (usuario+clave), aparte del SSO Google. PBKDF2-SHA256 con sal
/// aleatoria por contraseña e iteraciones altas. Formato guardado: "pbkdf2.sha256.{iter}.{saltB64}.{hashB64}".
/// Comparación de tiempo fijo. <b>Portado tal cual de MARKETweb</b> para validar los MISMOS hashes ya
/// cargados en UsuariosPC (no se re-hashea nada; el catálogo sólo verifica).
/// </summary>
public static class PasswordHasher
{
    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(stored)) return false;
        try
        {
            var p = stored.Split('.');
            if (p.Length != 5 || p[0] != "pbkdf2" || p[1] != "sha256") return false;
            var iter = int.Parse(p[2]);
            var salt = Convert.FromBase64String(p[3]);
            var key = Convert.FromBase64String(p[4]);
            var test = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, key.Length);
            return CryptographicOperations.FixedTimeEquals(test, key);
        }
        catch { return false; }
    }
}
