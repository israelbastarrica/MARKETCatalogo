using System.Diagnostics;
using System.Text.Json;
using MarketCatalogo.Compartido;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MarketCatalogo.Catalogo.Aplicacion;

/// <summary>
/// Reconstruye la BASE del catálogo (todo el universo mapeado, incluido depósito) y la persiste en la
/// tabla materializada <c>dbo.Catalogo</c>. Es el "job" del modelo tabla-como-caché (read-through): NO
/// hay refresco periódico que corra siempre — se recalcula cuando la base venció (TTL), con
/// <b>single-flight</b> (un candado) para que N requests concurrentes generen UN solo rebuild.
///
/// El reloj de la base vive ACÁ, en memoria (<see cref="_baseActualizada"/>): un único timestamp global,
/// no una columna repetida por fila. Al reiniciar la app queda null y el primer acceso dispara un rebuild.
///
/// Reutiliza el mismo cruce en C# que <c>CatalogoCache.ConstruirAsync</c> (una consulta por fuente, nunca
/// un JOIN cross-DB), pero <b>escribe a la tabla</b> en lugar de armar un snapshot en RAM, y amplía el
/// universo al depósito para la vista interna.
/// </summary>
public sealed class CatalogoStore
{
    private readonly ICatalogoRepositorio _repo;
    private readonly ILogger<CatalogoStore> _log;
    private readonly TimeSpan _ttl;
    private readonly string? _dirOriginales;

    // Un solo permiso: garantiza que sólo un rebuild de base corra a la vez (single-flight).
    private readonly SemaphoreSlim _candadoBase = new(1, 1);
    private volatile bool _rebuildEnCurso;
    // Reloj de la base, en memoria. null = nunca construida en esta instancia del proceso.
    private DateTime? _baseActualizada;

    public CatalogoStore(ICatalogoRepositorio repo, ILogger<CatalogoStore> log, IConfiguration cfg)
    {
        _repo = repo;
        _log = log;
        var minutos = int.TryParse(cfg["Catalogo:MinutosTtl"], out var m) ? m
                    : int.TryParse(cfg["Catalogo:MinutosCache"], out var m2) ? m2 : 20;
        _ttl = TimeSpan.FromMinutes(Math.Clamp(minutos, 1, 240));
        _dirOriginales = cfg["Fotos:DirOriginales"];
    }

    public DateTime? BaseActualizada => _baseActualizada;
    public TimeSpan Ttl => _ttl;

    /// <summary>Reconstruye la base y la persiste, esperando a que termine. La usa el warmup de arranque
    /// (arranque en frío) y el botón "Actualizar". Single-flight: si ya hay un rebuild en curso, espera a
    /// que ese termine (no encola un segundo).</summary>
    public async Task ReconstruirBaseAsync(CancellationToken ct = default)
    {
        await _candadoBase.WaitAsync(ct);
        try
        {
            await ConstruirYGuardarAsync(ct);
            _baseActualizada = DateTime.UtcNow;
        }
        finally { _candadoBase.Release(); }
    }

    /// <summary>Stale-while-revalidate: si la base venció, dispara un rebuild EN BACKGROUND y vuelve al
    /// instante. El request en curso sigue leyendo la tabla (lo anterior); el próximo ya verá lo nuevo.
    /// Nadie espera (salvo el arranque en frío, que cubre el warmup). Idempotente: si ya hay uno en curso
    /// o la base está fresca, no hace nada.</summary>
    public void AsegurarBaseFresca()
    {
        var edad = _baseActualizada is null ? TimeSpan.MaxValue : DateTime.UtcNow - _baseActualizada.Value;
        if (edad < _ttl || _rebuildEnCurso) return;
        _ = RefrescarEnBackgroundAsync();
    }

    private async Task RefrescarEnBackgroundAsync()
    {
        // WaitAsync(0): si otro ya tomó el candado, este disparo se descarta (no se apila).
        if (!await _candadoBase.WaitAsync(0)) return;
        _rebuildEnCurso = true;
        try
        {
            await ConstruirYGuardarAsync(CancellationToken.None);
            _baseActualizada = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            // Guardarraíl: un rebuild fallido NO tira la app ni pisa la tabla (GuardarBaseAsync no borra
            // ante universo vacío, y el MERGE es atómico). Se sigue sirviendo lo último bueno.
            _log.LogError(ex, "Falló el rebuild de la base del catálogo; se conserva lo último persistido.");
        }
        finally { _rebuildEnCurso = false; _candadoBase.Release(); }
    }

    private async Task ConstruirYGuardarAsync(CancellationToken ct)
    {
        var reloj = Stopwatch.StartNew();
        var filas = await ConstruirFilasAsync(ct);
        await _repo.GuardarBaseAsync(filas, ct);
        reloj.Stop();

        var publicados = filas.Count(f => f.Publicado);
        var soloDepo = filas.Count(f => f.EnDeposito && !f.EnLuro && !f.EnPeralta);
        _log.LogInformation(
            "Base del catálogo reconstruida en {Ms} ms: {Total} artículos ({Publicados} publicados, " +
            "{SoloDepo} sólo-depósito).", reloj.ElapsedMilliseconds, filas.Count, publicados, soloDepo);
    }

    /// <summary>Arma las filas BASE cruzando las fuentes en C#. Espeja el cruce de
    /// <c>CatalogoCache.ConstruirAsync</c> con dos diferencias: el universo incluye depósito (bits
    /// EnLuro/EnPeralta/EnDeposito) y se calcula <c>Publicado</c> en vez de descartar lo no-público.</summary>
    private async Task<IReadOnlyList<CatalogoFilaBase>> ConstruirFilasAsync(CancellationToken ct)
    {
        // 1) Universo interno: todo lo mapeado, incluido depósito.
        var ubicaciones = await _repo.TraerUbicacionesAsync(ct);
        var porCodigo = ubicaciones
            .GroupBy(u => u.ArtCod, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var codigos = porCodigo.Keys.ToArray();
        if (codigos.Length == 0) return Array.Empty<CatalogoFilaBase>();

        // 2) Otras fuentes (cada Dragon por su conexión).
        var articulos = await _repo.TraerArticulosBaseAsync(codigos, ct);
        var fotos = await _repo.TraerRutasFotoAsync(ct);
        var overrides = await _repo.TraerOverridesAsync(ct);

        // Color/talle: cascada PRECOMPRA -> REMCOMPRA (idéntica al público).
        var variantesPrecompra = await _repo.TraerVariantesPrecompraAsync(codigos, ct);
        var codigosSinPrecompra = codigos.Except(
            variantesPrecompra.Select(v => v.ArtCod), StringComparer.OrdinalIgnoreCase).ToArray();
        var variantesRemcompra = codigosSinPrecompra.Length == 0
            ? Array.Empty<VarianteRow>()
            : await _repo.TraerVariantesRemcompraAsync(codigosSinPrecompra, ct);
        var variantes = variantesPrecompra.Concat(variantesRemcompra).ToList();

        // Curva definida (ART.CURTALL -> DCTALLE): fallback de talle cuando las compras no traen.
        var curvasPorCodigo = (await _repo.TraerCurvasTalleAsync(codigos, ct))
            .GroupBy(c => c.ArtCod, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Orden).ToList(), StringComparer.OrdinalIgnoreCase);

        var fotoPorCodigo = fotos
            .GroupBy(f => f.ArtCod, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Ruta, StringComparer.OrdinalIgnoreCase);
        var overridePorCodigo = overrides
            .GroupBy(o => o.ArtCod, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var variantesPorCodigo = variantes
            .GroupBy(v => v.ArtCod, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // 3) Cruce.
        var filas = new List<CatalogoFilaBase>(articulos.Count);
        var tallesDesconocidos = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in articulos)
        {
            // Basura del ERP (pseudo-artículos de promoción, taxonomía "No aplica"): NO entran ni a la
            // tabla. Es lo único que se descarta por completo; todo lo demás se persiste con su bit
            // Publicado, para que la vista interna lo vea.
            if (!TaxonomiaValida(a.Rubro) || !TaxonomiaValida(a.Genero)) continue;

            var ubis = porCodigo.GetValueOrDefault(a.ArtCod) ?? new();
            var enDeposito = ubis.Any(u => u.EsDeposito);
            var enLuro = ubis.Any(u => !u.EsDeposito && u.Local.Equals("LURO", StringComparison.OrdinalIgnoreCase));
            var enPeralta = ubis.Any(u => !u.EsDeposito && u.Local.Equals("PERALTA", StringComparison.OrdinalIgnoreCase));
            var enAlgunLocal = enLuro || enPeralta;

            overridePorCodigo.TryGetValue(a.ArtCod, out var ovr);

            var artDes = Texto.RepararEnie(a.ArtDes);
            var rubro = Texto.RepararEnie(a.Rubro);
            var genero = Texto.RepararEnie(a.Genero);
            var familia = Texto.RepararEnie(a.Familia);

            var titulo = string.IsNullOrWhiteSpace(ovr?.NombreComercial)
                ? TituloArticulo.Derivar(artDes, familia)
                : ovr!.NombreComercial!.Trim();

            var ruta = fotoPorCodigo.GetValueOrDefault(a.ArtCod);
            var tieneFoto = !string.IsNullOrWhiteSpace(ruta);
            var fotoVersion = tieneFoto ? VersionFoto(ruta!) : null;

            // Color/talle: mismo criterio que el público, con la excepción Lencería (datos inconsistentes
            // en las tres fuentes -> talle y color "Único" fijos).
            var esLenceria = Texto.SinAcentos(rubro) == "lenceria";
            List<string> talles;
            List<string> colores;
            var tieneVariantes = variantesPorCodigo.ContainsKey(a.ArtCod);

            if (esLenceria)
            {
                talles = ["Único"];
                colores = ["Único"];
            }
            else
            {
                var vs = variantesPorCodigo.GetValueOrDefault(a.ArtCod) ?? new();
                foreach (var v in vs)
                    if (Talles.EsDesconocido(v.Talle) && v.Talle.Length > 0) tallesDesconocidos.Add(v.Talle);

                var variantesDto = vs
                    .Select(v => (Color: LimpiarColor(Texto.RepararEnie(v.Color), v.ColorCod),
                                  Talle: v.Talle,
                                  TalleMostrar: Talles.Mostrar(v.Talle),
                                  TalleOrden: Talles.Resolver(v.Talle).Orden))
                    .OrderBy(v => v.TalleOrden).ThenBy(v => v.Color, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                talles = variantesDto
                    .Where(v => !Talles.EsSinTalle(v.Talle))
                    .GroupBy(v => v.TalleMostrar)
                    .OrderBy(g => g.Min(v => v.TalleOrden))
                    .Select(g => g.Key).ToList();

                // Fallback a la curva definida cuando las compras dejaron todo sin talle.
                if (talles.Count == 0 && curvasPorCodigo.TryGetValue(a.ArtCod, out var curva))
                {
                    talles = curva
                        .Where(c => !Talles.EsSinTalle(c.Talle))
                        .Select(c => Talles.Mostrar(c.Talle))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                colores = variantesDto
                    .Where(v => v.Color.Length > 0)
                    .Select(v => v.Color).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            }

            var combo = Combo.Parsear(a.Combo);

            // Publicado = criterio del catálogo PÚBLICO, preservando paridad con el sitio actual:
            //   Indumentaria + taxonomía válida + en algún local + (tiene variantes o es Lencería) +
            //   NO oculto manual. (La foto NO es requisito: hoy el sitio publica artículos sin foto.)
            var publicado =
                Texto.SinAcentos(rubro) == "indumentaria"
                && enAlgunLocal
                && (tieneVariantes || esLenceria)
                && !(ovr?.OcultarManual == true);

            filas.Add(new CatalogoFilaBase(
                Codigo: a.ArtCod,
                Publicado: publicado,
                Slug: Texto.SlugProducto(titulo, a.ArtCod),
                Titulo: titulo,
                Descripcion: artDes,
                Rubro: rubro,
                Genero: genero,
                Prenda: familia,
                PrecioVenta: a.PrecioSuelta > 0 ? a.PrecioSuelta : null,
                PrecioCompra: a.PrecioCompra > 0 ? a.PrecioCompra : null,
                Combo: combo is null ? null : combo.Texto,
                EnLuro: enLuro,
                EnPeralta: enPeralta,
                EnDeposito: enDeposito,
                TallesCsv: string.Join(',', talles),
                ColoresCsv: string.Join(',', colores),
                TieneFoto: tieneFoto,
                FotoPrincipalVersion: fotoVersion,
                FotosJson: ArmarFotosJson(ruta, fotoVersion),
                Proveedor: string.IsNullOrWhiteSpace(a.Proveedor) ? null : Texto.RepararEnie(a.Proveedor),
                Temporada: string.IsNullOrWhiteSpace(a.Temporada) ? null : a.Temporada,
                Marca: string.IsNullOrWhiteSpace(a.Marca) ? null : a.Marca,
                TextoBusqueda: Texto.SinAcentos($"{titulo} {artDes} {a.ArtCod} {familia}")));
        }

        if (tallesDesconocidos.Count > 0)
            _log.LogWarning("Talles no registrados en Talles.cs: {Talles}.", string.Join(", ", tallesDesconocidos));

        return filas;
    }

    /// <summary>FotosJson mínimo: la foto principal (0..1). El modelo soporta N fotos por artículo — se
    /// amplía en la fase de fotos sin cambiar el esquema (columna JSON). null si el artículo no tiene foto.</summary>
    private static string? ArmarFotosJson(string? ruta, string? version)
    {
        if (string.IsNullOrWhiteSpace(ruta)) return null;
        var fotos = new[]
        {
            new { orden = 0, tipo = "principal", link = ruta, version, esPrincipal = true },
        };
        return JsonSerializer.Serialize(fotos);
    }

    private static bool TaxonomiaValida(string? valor)
        => !string.IsNullOrWhiteSpace(valor)
           && !valor.Trim().Equals("No aplica", StringComparison.OrdinalIgnoreCase);

    // "NEUTRO" (color "sin color real" de las compras) -> "Único". Igual que CatalogoCache.LimpiarColor.
    private static string LimpiarColor(string? desc, string? cod)
    {
        var d = (desc ?? "").Trim();
        if (d.Equals("NEUTRO", StringComparison.OrdinalIgnoreCase)) return "Único";
        if (d.Length > 0) return d;
        var c = (cod ?? "").Trim();
        if (c.Length == 0 || int.TryParse(c, out _)) return "";
        return c;
    }

    // Token ?v= de la foto: fecha de modificación del original (mismo criterio que CatalogoCache).
    private string VersionFoto(string rutaEnBase)
    {
        try
        {
            var origen = RutasFoto.Resolver(rutaEnBase, _dirOriginales);
            if (origen is not null && File.Exists(origen))
                return File.GetLastWriteTimeUtc(origen).Ticks.ToString("x");
        }
        catch { /* sin acceso al disco: fallback */ }
        return ((uint)StringComparer.Ordinal.GetHashCode(rutaEnBase)).ToString("x");
    }
}
