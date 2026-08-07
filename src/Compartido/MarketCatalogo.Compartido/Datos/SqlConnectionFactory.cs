using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace MarketCatalogo.Compartido.Datos;

/// <summary>
/// Ver <see cref="ISqlConnectionFactory"/>. El sitio SOLO lee: la cadena debería apuntar a un login
/// read-only con permiso mínimo.
///
/// Si no hay cadena "DragonDb" explícita, se DERIVA de "MarketDb" cambiándole la base
/// (<c>Initial Catalog</c>) por la de Dragon. Así hoy alcanza con configurar una sola cadena, pero las
/// queries de Dragon se escriben SIN prefijo de base (<c>ZooLogic.ART</c> en vez de
/// <c>DRAGONFISH_CENTRAL.ZooLogic.ART</c>) — que es la forma portable: el día que Dragon se mude a otro
/// servidor o a la nube, se define "DragonDb" y no hay que tocar una sola query.
/// </summary>
public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private const string BaseDragonPorDefecto = "DRAGONFISH_CENTRAL";

    private readonly string _market;
    private readonly string _dragon;

    public SqlConnectionFactory(IConfiguration cfg)
    {
        _market = cfg.GetConnectionString("MarketDb")
            ?? throw new InvalidOperationException(
                "Falta la cadena de conexión 'MarketDb'. Definirla con dotnet user-secrets.");

        var dragonExplicita = cfg.GetConnectionString("DragonDb");
        if (!string.IsNullOrWhiteSpace(dragonExplicita))
        {
            _dragon = dragonExplicita;
        }
        else
        {
            var baseDragon = cfg["Catalogo:BaseDragon"];
            if (string.IsNullOrWhiteSpace(baseDragon)) baseDragon = BaseDragonPorDefecto;
            _dragon = new SqlConnectionStringBuilder(_market) { InitialCatalog = baseDragon.Trim() }
                .ConnectionString;
        }
    }

    public SqlConnection CrearMarket() => new(_market);
    public SqlConnection CrearDragon() => new(_dragon);
}
