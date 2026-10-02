using Dapper;
using MarketCatalogo.Auth.Aplicacion;
using MarketCatalogo.Compartido.Datos;

namespace MarketCatalogo.Auth.Datos;

/// <summary>
/// Implementa <see cref="IUsuariosAuthRepositorio"/> leyendo <c>MARKET.dbo.UsuariosPC</c> con Dapper.
/// SÓLO LECTURA: las columnas Usuario/PasswordHash/Area ya existen (las creó MARKETweb); el catálogo no
/// las modifica ni da de alta usuarios. Una persona puede tener varias filas (una por PC): se elige por
/// mismo criterio que MARKETweb (área cargada primero, después ID).
/// </summary>
public sealed class UsuariosAuthRepositorio : IUsuariosAuthRepositorio
{
    private readonly ISqlConnectionFactory _db;
    public UsuariosAuthRepositorio(ISqlConnectionFactory db) => _db = db;

    // ¿Existe ya UsuariosPC.CodProveedor? La columna la agrega MARKETweb junto con el perfil PROVEEDORES y
    // puede no estar todavía: si se nombrara en el SELECT antes de existir, SQL ni compila la consulta y se
    // cae el login de TODOS. Se consulta una vez por proceso y se elige la consulta que corresponde.
    private static bool? _hayCodProveedor;

    private async Task<bool> HayCodProveedorAsync(System.Data.IDbConnection cn, CancellationToken ct)
    {
        if (_hayCodProveedor is bool ya) return ya;
        try
        {
            var existe = await cn.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT COL_LENGTH('MARKET.dbo.UsuariosPC','CodProveedor');", cancellationToken: ct));
            _hayCodProveedor = existe is not null;
        }
        catch { _hayCodProveedor = false; }
        return _hayCodProveedor.Value;
    }

    // El SELECT es el mismo salvo esa columna: con ella si existe, NULL si todavía no.
    private static string Consulta(bool hayCod, string filtro, string orden) => $"""
        SELECT TOP 1 Perfil = PERFIL, Pc = PC, PasswordHash, MailAprobado, Area,
               CodProveedor = {(hayCod ? "CodProveedor" : "CAST(NULL AS NVARCHAR(20))")}
        FROM MARKET.dbo.UsuariosPC WITH (NOLOCK)
        WHERE Eliminado = 0 AND {filtro}
        ORDER BY {orden};
        """;

    public async Task<UsuarioAuthRow?> BuscarPorMailAsync(string mail, CancellationToken ct = default)
    {
        using var cn = _db.CrearMarket();
        var sql = Consulta(await HayCodProveedorAsync(cn, ct), "Mail = @mail",
                           "CASE WHEN Area IS NULL THEN 1 ELSE 0 END, ID");
        return await cn.QuerySingleOrDefaultAsync<UsuarioAuthRow>(
            new CommandDefinition(sql, new { mail = (mail ?? "").Trim().ToLowerInvariant() }, cancellationToken: ct));
    }

    public async Task<UsuarioAuthRow?> BuscarPorUsuarioAsync(string usuario, CancellationToken ct = default)
    {
        using var cn = _db.CrearMarket();
        var sql = Consulta(await HayCodProveedorAsync(cn, ct), "Usuario = @u", "ID");
        return await cn.QuerySingleOrDefaultAsync<UsuarioAuthRow>(
            new CommandDefinition(sql, new { u = (usuario ?? "").Trim() }, cancellationToken: ct));
    }
}
