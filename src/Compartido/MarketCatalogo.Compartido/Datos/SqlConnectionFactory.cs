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
    private const string BaseLuroPorDefecto = "DRAGONFISH_LURO";
    private const string BasePeraltaPorDefecto = "DRAGONFISH_PERALTA";

    private readonly string _market;
    private readonly string _dragon;
    private readonly string _luro;
    private readonly string _peralta;

    public SqlConnectionFactory(IConfiguration cfg)
    {
        _market = cfg.GetConnectionString("MarketDb")
            ?? throw new InvalidOperationException(
                "Falta la cadena de conexión 'MarketDb'. Definirla con dotnet user-secrets.");

        _dragon = Derivar(cfg, "DragonDb", "Catalogo:BaseDragon", BaseDragonPorDefecto);
        _luro = Derivar(cfg, "DragonLuroDb", "Catalogo:BaseLuro", BaseLuroPorDefecto);
        _peralta = Derivar(cfg, "DragonPeraltaDb", "Catalogo:BasePeralta", BasePeraltaPorDefecto);
    }

    // Una cadena explícita (ConnectionStrings:<claveExplicita>) gana; si no, se DERIVA de MarketDb
    // cambiando la base (Initial Catalog) por la de config (<claveBase>) o el default. Así hoy alcanza una
    // sola cadena para las cuatro bases (mismo servidor), pero cada una puede apuntarse por separado el día
    // que una se mude a otro server o a la nube — sin tocar una sola query.
    private string Derivar(IConfiguration cfg, string claveExplicita, string claveBase, string baseDefecto)
    {
        var explicita = cfg.GetConnectionString(claveExplicita);
        if (!string.IsNullOrWhiteSpace(explicita)) return explicita;

        var baseNombre = cfg[claveBase];
        if (string.IsNullOrWhiteSpace(baseNombre)) baseNombre = baseDefecto;
        return new SqlConnectionStringBuilder(_market) { InitialCatalog = baseNombre.Trim() }.ConnectionString;
    }

    public SqlConnection CrearMarket() => new(_market);
    public SqlConnection CrearDragon() => new(_dragon);
    public SqlConnection CrearLuro() => new(_luro);
    public SqlConnection CrearPeralta() => new(_peralta);
}
