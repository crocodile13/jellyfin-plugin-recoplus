using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RecoPlus.Engine;

/// <summary>Indice du pays d'un compte : d'où il vient (pour l'expliquer), le pays qu'il désigne, son poids.</summary>
public sealed record RegionClue(string Source, string Country, double Weight);

/// <summary>
/// Pays retenus pour un compte (un ou plusieurs ; aucun en mode international), avec les indices qui l'ont désigné.
/// </summary>
public sealed record RegionGuess(IReadOnlyList<string> Countries, bool International, bool Detected, IReadOnlyList<RegionClue> Clues)
{
    /// <summary>Notation courte : « * » (international), « FR » ou « FR,BE,CH ».</summary>
    public string Spec => International ? Regions.InternationalSpec : string.Join(",", Countries);

    /// <summary>Premier pays (affichage) ; « US » en mode international.</summary>
    public string Country => Countries.Count > 0 ? Countries[0] : "US";
}

/// <summary>
/// Pays d'un compte (« région ») : celui où il regarde, pour les dates de sortie (sorti en salle, en vidéo ou à la
/// télévision dans ce pays). Jamais le pays de production d'un titre. Détection sans rien envoyer au dehors : fuseau
/// horaire et langues du navigateur (signalés par le script de l'accueil), langues audio et sous-titres préférées du compte,
/// réglages du serveur ; chaque indice vote pour un pays, le plus fort l'emporte. Pas d'adresse IP : la situer demande un
/// service extérieur (l'adresse partirait chez lui) ou une base à tenir à jour, se trompe derrière un VPN ou un relais, et
/// ne dit rien sur le réseau local ; le fuseau du navigateur donne la même information sans rien envoyer.
/// </summary>
public static class Regions
{
    /// <summary>Poids des indices : un fuseau horaire désigne un pays, une langue souvent plusieurs.</summary>
    public const double TimeZoneWeight = 3, BrowserRegionWeight = 2, LanguageWeight = 1, ServerWeight = 0.5;

    /// <summary>
    /// Langue audio préférée : moitié du poids d'une langue. Beaucoup regardent en VO (audio japonais pour un fan d'anime,
    /// anglais en VOSTFR) : la langue des sous-titres dit mieux la langue de tous les jours.
    /// </summary>
    public const double AudioWeight = 0.5;

    /// <summary>
    /// Plafond, par pays, de tous les indices tirés d'une langue (navigateur, audio, sous-titres, langue du serveur) : ils
    /// disent souvent la même chose (« français ») et ne doivent pas l'emporter, additionnés, sur le fuseau horaire, qui
    /// désigne le pays (un Belge au navigateur en fr-FR, audio et sous-titres français, reste en Belgique).
    /// </summary>
    public const double LanguageCap = 2.5;

    private static readonly HashSet<string> LanguageSources = new(StringComparer.Ordinal) { "ui", "browser", "audio", "subtitles", "serverlang" };

    private static readonly Lazy<Dictionary<string, string>> Zones = new(LoadZones);

    private static readonly Lazy<string[]> AllCountries = new(() => Zones.Value.Values.Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToArray());

    /// <summary>Pays qu'un compte peut choisir (ISO 3166-1, ceux qui ont un fuseau horaire), dans l'ordre des codes.</summary>
    public static IReadOnlyList<string> Countries => AllCountries.Value;

    /// <summary>
    /// Pays par défaut d'une langue (ISO 639-1) quand elle n'en dit pas plus (« fr » sans « -CA ») : le pays où elle est
    /// le plus parlée comme langue d'usage, à défaut d'autre indice. L'anglais n'en a pas : trop de pays à la fois.
    /// </summary>
    private static readonly Dictionary<string, string> LanguageCountry = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fr"] = "FR", ["de"] = "DE", ["es"] = "ES", ["it"] = "IT", ["pt"] = "PT", ["nl"] = "NL", ["pl"] = "PL", ["sv"] = "SE",
        ["da"] = "DK", ["nb"] = "NO", ["nn"] = "NO", ["no"] = "NO", ["fi"] = "FI", ["cs"] = "CZ", ["sk"] = "SK", ["hu"] = "HU",
        ["ro"] = "RO", ["el"] = "GR", ["tr"] = "TR", ["ru"] = "RU", ["uk"] = "UA", ["bg"] = "BG", ["hr"] = "HR", ["sr"] = "RS",
        ["sl"] = "SI", ["lt"] = "LT", ["lv"] = "LV", ["et"] = "EE", ["is"] = "IS", ["ja"] = "JP", ["ko"] = "KR", ["zh"] = "CN",
        ["he"] = "IL", ["th"] = "TH", ["vi"] = "VN", ["id"] = "ID", ["ms"] = "MY", ["hi"] = "IN", ["fa"] = "IR", ["ca"] = "ES",
        ["eu"] = "ES", ["gl"] = "ES",
    };

    /// <summary>Codes ISO 639-2/B (ceux de Jellyfin, « fre », « ger ») qui diffèrent du code /T (« fra », « deu »).</summary>
    private static readonly Dictionary<string, string> Iso6392B = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fre"] = "fr", ["ger"] = "de", ["dut"] = "nl", ["cze"] = "cs", ["slo"] = "sk", ["gre"] = "el", ["rum"] = "ro",
        ["chi"] = "zh", ["per"] = "fa", ["ice"] = "is", ["baq"] = "eu", ["may"] = "ms", ["alb"] = "sq", ["arm"] = "hy",
        ["geo"] = "ka", ["mac"] = "mk", ["bur"] = "my", ["tib"] = "bo", ["wel"] = "cy",
    };

    /// <summary>Notation du mode international (toutes les sorties, sans pays) dans les réglages.</summary>
    public const string InternationalSpec = "*";

    /// <summary>Pays au plus dans une liste personnalisée.</summary>
    public const int MaxCountries = 12;

    /// <summary>
    /// Lit une notation de pays : vide = automatique (null) ; « * » (ou « international ») = international ; « FR » ou
    /// « FR, BE, CH » = une liste de pays connus (inconnus ignorés, doublons retirés, au plus <see cref="MaxCountries"/>).
    /// Une notation dont aucun pays n'est connu vaut automatique.
    /// </summary>
    public static (bool International, IReadOnlyList<string> Countries)? ParseSpec(string? spec)
    {
        var s = spec?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length > 200)
        {
            return null;
        }

        if (s == InternationalSpec || s.Equals("international", StringComparison.OrdinalIgnoreCase))
        {
            return (true, Array.Empty<string>());
        }

        var list = s.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(Clean).OfType<string>()
            .Distinct(StringComparer.Ordinal).Take(MaxCountries).ToList();
        return list.Count > 0 ? (false, list) : null;
    }

    private static readonly Lazy<Dictionary<string, string[]>> CountryLanguages = new(() =>
    {
        var res = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var c in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(c.Name).TwoLetterISORegionName.ToUpperInvariant();
                var lang = c.TwoLetterISOLanguageName.ToLowerInvariant();
                if (region.Length == 2 && lang.Length == 2)
                {
                    (res.TryGetValue(region, out var set) ? set : res[region] = new SortedSet<string>(StringComparer.Ordinal)).Add(lang);
                }
            }
            catch (ArgumentException)
            {
                // culture sans région reconnue
            }
        }

        return res.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
    });

    /// <summary>
    /// Langues parlées dans les pays d'un compte (deux lettres, cultures de .NET : France → fr ; Belgique → fr, nl ; Suisse
    /// → de, fr, it, rm) ; aucune en mode international. Préférence de départ de l'origine des titres (<see cref="Origin"/>).
    /// </summary>
    public static IReadOnlyList<string> LanguagesOf(RegionGuess region)
        => region.International ? Array.Empty<string>()
            : region.Countries.SelectMany(c => CountryLanguages.Value.GetValueOrDefault(c) ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Noms de pays tels que TMDb et Jellyfin les écrivent (pays de production) quand ils diffèrent du nom anglais de .NET.
    /// </summary>
    private static readonly Dictionary<string, string[]> TmdbNames = new(StringComparer.Ordinal)
    {
        ["US"] = new[] { "United States of America", "USA" }, ["GB"] = new[] { "United Kingdom", "UK" }, ["KR"] = new[] { "South Korea", "Korea" }, // i18n:skip
        ["KP"] = new[] { "North Korea" }, ["HK"] = new[] { "Hong Kong" }, ["MO"] = new[] { "Macao", "Macau" }, ["CZ"] = new[] { "Czech Republic", "Czechia" }, // i18n:skip
        ["RU"] = new[] { "Russia" }, ["TW"] = new[] { "Taiwan" }, ["VN"] = new[] { "Vietnam" }, ["IR"] = new[] { "Iran" }, // i18n:skip
        ["SY"] = new[] { "Syria" }, ["LA"] = new[] { "Laos" }, ["BO"] = new[] { "Bolivia" }, ["VE"] = new[] { "Venezuela" }, // i18n:skip
        ["TZ"] = new[] { "Tanzania" }, ["MD"] = new[] { "Moldova" }, ["MK"] = new[] { "North Macedonia", "Macedonia" }, // i18n:skip
        ["PS"] = new[] { "Palestine", "Palestinian Territory" }, ["CI"] = new[] { "Cote D'Ivoire", "Côte d'Ivoire", "Ivory Coast" }, // i18n:skip
        ["CD"] = new[] { "Congo" }, ["TR"] = new[] { "Turkey", "Türkiye" }, ["BA"] = new[] { "Bosnia and Herzegovina" }, // i18n:skip
    };

    /// <summary>
    /// Origines du pays d'un compte au format de <see cref="Origin.Keys"/> : ses langues (« l:fr ») et ses noms (« c:france »,
    /// avec ceux de TMDb) ; aucune en mode international. La préférence de départ de l'origine des titres.
    /// </summary>
    public static IReadOnlyList<string> HomeOrigins(RegionGuess region)
    {
        var res = new List<string>();
        foreach (var l in LanguagesOf(region))
        {
            res.Add("l:" + l);
        }

        foreach (var c in region.International ? Array.Empty<string>() : region.Countries)
        {
            var names = new List<string>(TmdbNames.GetValueOrDefault(c) ?? Array.Empty<string>());
            try
            {
                names.Add(new RegionInfo(c).EnglishName);
            }
            catch (ArgumentException)
            {
                // pays inconnu de .NET : seulement les noms de TMDb
            }

            foreach (var n in names)
            {
                var k = "c:" + n.Trim().ToLowerInvariant();
                if (!res.Contains(k))
                {
                    res.Add(k);
                }
            }
        }

        return res;
    }

    /// <summary>Notation propre d'une liste lue par <see cref="ParseSpec"/> ; null pour automatique.</summary>
    public static string? NormalizeSpec(string? spec) => ParseSpec(spec) is { } p ? (p.International ? InternationalSpec : string.Join(",", p.Countries)) : null;

    private static readonly Lazy<HashSet<string>> Known = new(() => new HashSet<string>(Zones.Value.Values, StringComparer.Ordinal));

    /// <summary>
    /// Code pays ISO 3166-1 alpha-2 propre (« fr » → « FR ») ; null s'il n'en a pas la forme ou n'est pas un pays connu
    /// (« UK » n'existe pas : c'est « GB » ; un code inconnu ne correspondrait à aucune date de sortie).
    /// </summary>
    public static string? Clean(string? country)
    {
        var c = country?.Trim().ToUpperInvariant();
        return c is { Length: 2 } && Known.Value.Contains(c) ? c : null;
    }

    private static readonly Lazy<Dictionary<string, string>> Iso6392T = new(() =>
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            if (c.ThreeLetterISOLanguageName.Length == 3 && c.TwoLetterISOLanguageName.Length == 2)
            {
                res.TryAdd(c.ThreeLetterISOLanguageName, c.TwoLetterISOLanguageName);
            }
        }

        return res;
    });

    /// <summary>Pays d'un fuseau horaire IANA (« Europe/Paris » → « FR ») ; null pour un fuseau sans pays (« UTC »).</summary>
    public static string? OfTimeZone(string? zone)
        => zone is { Length: > 0 and <= 64 } && Zones.Value.TryGetValue(zone.Trim(), out var c) ? c : null;

    /// <summary>
    /// Pays d'une langue : sa région si elle en a une (« fr-CA » → « CA », « pt_BR » → « BR »), sinon le pays par défaut
    /// de la langue (ISO 639-1 ou 639-2, « fre » → « FR ») ; null pour l'anglais seul ou une langue inconnue.
    /// </summary>
    public static (string? Country, bool FromRegion) OfLanguage(string? tag)
    {
        var t = tag?.Trim().Replace('_', '-');
        if (string.IsNullOrEmpty(t) || t.Length > 35)
        {
            return (null, false);
        }

        var parts = t.Split('-');
        // région : deux lettres après la langue (« zh-Hant-TW » : la région est après l'écriture)
        foreach (var p in parts.Skip(1))
        {
            if (Clean(p) is { } r)
            {
                return (r, true);
            }
        }

        var lang = parts[0].ToLowerInvariant();
        if (lang.Length == 3)
        {
            lang = Iso6392B.TryGetValue(lang, out var b) ? b : Iso6392T.Value.TryGetValue(lang, out var two) ? two : lang;
        }

        return (LanguageCountry.TryGetValue(lang, out var c) ? c : null, false);
    }

    /// <summary>
    /// Pays d'un compte : celui choisi (par le compte ou l'administrateur) s'il y en a un, sinon le plus désigné par les
    /// indices (à égalité, l'ordre des indices), sinon <paramref name="fallback"/>.
    /// </summary>
    public static RegionGuess Detect(string? chosen, IEnumerable<RegionClue> clues, string fallback)
    {
        var list = clues.Where(c => Clean(c.Country) is not null && c.Weight > 0).ToList();
        if (ParseSpec(chosen) is { } fixedSpec)
        {
            return new RegionGuess(fixedSpec.Countries, fixedSpec.International, false, list);
        }

        var best = list.Select((c, i) => (C: Clean(c.Country)!, c.Weight, Lang: LanguageSources.Contains(c.Source), i)).GroupBy(x => x.C)
            .Select(g => (Country: g.Key, W: g.Where(x => !x.Lang).Sum(x => x.Weight) + Math.Min(LanguageCap, g.Where(x => x.Lang).Sum(x => x.Weight)),
                First: g.Min(x => x.i)))
            .OrderByDescending(g => g.W).ThenBy(g => g.First).FirstOrDefault();
        return best.Country is null ? new RegionGuess(new[] { Clean(fallback) ?? "US" }, false, false, list) : new RegionGuess(new[] { best.Country }, false, true, list);
    }

    /// <summary>
    /// Indices d'un compte : fuseau horaire et langues du navigateur (la première compte plus), langues audio et
    /// sous-titres préférées, puis ceux du serveur (pays et langue des métadonnées, fuseau de la machine), plus faibles :
    /// ils valent pour tous les comptes et gardent souvent leurs valeurs par défaut.
    /// </summary>
    /// <param name="uiLanguage">Langue de l'interface Jellyfin du compte (choisie dans Jellyfin, parfois avec région : « pt-BR »).</param>
    public static List<RegionClue> Clues(string? browserZone, IEnumerable<string>? browserLanguages, string? audioLanguage, string? subtitleLanguage,
        string? serverCountry, string? serverLanguage, string? serverZone, string? uiLanguage = null)
    {
        var res = new List<RegionClue>();
        if (OfLanguage(uiLanguage) is ({ } ui, var uiRegion))
        {
            res.Add(new RegionClue("ui", ui, uiRegion ? BrowserRegionWeight : LanguageWeight));
        }

        if (OfTimeZone(browserZone) is { } z)
        {
            res.Add(new RegionClue("timezone", z, TimeZoneWeight));
        }

        int i = 0;
        foreach (var l in (browserLanguages ?? Array.Empty<string>()).Take(4))
        {
            var (c, region) = OfLanguage(l);
            if (c is not null)
            {
                // la première langue du navigateur est celle de l'interface ; les suivantes comptent moitié moins
                res.Add(new RegionClue("browser", c, (region ? BrowserRegionWeight : LanguageWeight) / (i == 0 ? 1 : 2)));
            }

            i++;
        }

        foreach (var (src, l, w) in new[] { ("audio", audioLanguage, AudioWeight), ("subtitles", subtitleLanguage, LanguageWeight) })
        {
            if (OfLanguage(l).Country is { } c)
            {
                res.Add(new RegionClue(src, c, w));
            }
        }

        if (Clean(serverCountry) is { } sc)
        {
            res.Add(new RegionClue("server", sc, ServerWeight));
        }

        if (OfLanguage(serverLanguage).Country is { } sl)
        {
            res.Add(new RegionClue("serverlang", sl, ServerWeight));
        }

        if (OfTimeZone(serverZone) is { } sz)
        {
            res.Add(new RegionClue("server", sz, ServerWeight));
        }

        return res;
    }

    private static Dictionary<string, string> LoadZones()
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var s = typeof(Regions).Assembly.GetManifestResourceStream("RecoPlus.Engine.zones.txt");
        if (s is null)
        {
            return res;
        }

        using var r = new StreamReader(s);
        while (r.ReadLine() is { } line)
        {
            var p = line.Split(' ');
            if (p.Length == 2 && !line.StartsWith('#'))
            {
                res[p[0]] = p[1];
            }
        }

        return res;
    }
}
