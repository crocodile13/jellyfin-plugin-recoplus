using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RecoPlus.Engine;

/// <summary>
/// Textes affichés, en français (langue source), anglais, allemand ou espagnol. Le texte français sert de clé : les
/// dictionnaires <c>english.json</c>, <c>german.json</c>, <c>spanish.json</c> (ressources incorporées ; jamais « .de. »
/// dans le nom, MSBuild en ferait un assembly satellite) donnent ses traductions ; un texte absent d'un dictionnaire
/// allemand ou espagnol se rabat sur l'anglais, et un texte absent de l'anglais reste tel quel. Un message qui dépend de valeurs est un modèle à paramètres {0}, {1}… Pour être traduit au moment de
/// l'affichage (dans la langue de celui qui le lit, pas celle du serveur au moment où il a été produit), il est gardé
/// « emballé » (<see cref="Pack"/>) : modèle et paramètres, rendus par <see cref="Render"/>. Un paramètre peut être
/// lui-même un message emballé (imbrication sans limite) ou un libellé à traduire (<see cref="Translatable"/>).
/// </summary>
public static class Texts
{
    /// <summary>Marque d'un message emballé ; suivie d'un tableau JSON [modèle, paramètres…].</summary>
    private const char Mark = '\u0001';

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Langues de l'interface : le français (source), puis celles qui ont un dictionnaire.</summary>
    public static IReadOnlyList<string> Languages { get; } = new[] { "fr", "en", "de", "es" };

    private static readonly Dictionary<string, Lazy<IReadOnlyDictionary<string, string>>> Dicts = new(StringComparer.Ordinal)
    {
        ["en"] = new(() => Load("RecoPlus.Engine.english.json")),
        ["de"] = new(() => Load("RecoPlus.Engine.german.json")),
        ["es"] = new(() => Load("RecoPlus.Engine.spanish.json")),
    };

    /// <summary>Dictionnaire français → anglais.</summary>
    public static IReadOnlyDictionary<string, string> English => Dicts["en"].Value;

    /// <summary>Dictionnaire français → <paramref name="lang"/> (vide en français, langue source, ou langue inconnue).</summary>
    public static IReadOnlyDictionary<string, string> Dictionary(string lang)
        => Dicts.TryGetValue(lang, out var d) ? d.Value : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Langue de l'interface d'après un code quelconque (fr-FR, de-AT, es-419, pt…) : l'anglais pour une langue sans dictionnaire.</summary>
    public static string Normalize(string? lang)
    {
        var l = string.IsNullOrWhiteSpace(lang) ? string.Empty : lang.Trim();
        var two = l.Length >= 2 ? l[..2].ToLowerInvariant() : string.Empty;
        return two is "fr" or "de" or "es" && (l.Length == 2 || l[2] is '-' or '_') ? two : "en";
    }

    /// <summary>Texte dans la langue voulue (le français est la langue source ; allemand ou espagnol absent : l'anglais).</summary>
    public static string T(string fr, string lang)
    {
        if (lang == "fr")
        {
            return fr;
        }

        if (Dictionary(lang).TryGetValue(fr, out var t) && t.Length > 0)
        {
            return t;
        }

        return lang != "en" && English.TryGetValue(fr, out var en) && en.Length > 0 ? en : fr;
    }

    /// <summary>Modèle traduit puis rempli ({0}, {1}… remplacés par les paramètres ; un paramètre emballé est rendu lui aussi).</summary>
    public static string T(string fr, string lang, params object?[] args)
    {
        var a = new string[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            a[i] = args[i] is string s && IsPacked(s) ? Render(s, lang) : Invariant(args[i]);
        }

        return Fill(T(fr, lang), a);
    }

    /// <summary>Libellé passé en paramètre et traduit lui aussi au rendu (nom de méthode, de langue…).</summary>
    public static string Translatable(string fr) => Mark + JsonSerializer.Serialize(new[] { fr }, Json);

    /// <summary>Message emballé : modèle français et paramètres, traduit seulement au rendu.</summary>
    /// <remarks>Un paramètre texte déjà emballé (ou <see cref="Translatable"/>) est imbriqué tel quel ; les autres sont figés (culture invariante).</remarks>
    public static string Pack(string fr, params object?[] args)
    {
        if (args.Length == 0)
        {
            return fr;
        }

        var arr = new JsonArray { fr };
        foreach (var a in args)
        {
            arr.Add(a is string s && IsPacked(s) ? JsonNode.Parse(s[1..]) : JsonValue.Create(Invariant(a)));
        }

        return Mark + arr.ToJsonString(Json);
    }

    /// <summary>Textes (emballés ou non) mis bout à bout avec un séparateur, chacun rendu dans la langue du lecteur.</summary>
    public static string Join(string separator, IReadOnlyList<string> parts)
    {
        var tpl = new System.Text.StringBuilder();
        for (int i = 0; i < parts.Count; i++)
        {
            tpl.Append(i > 0 ? separator : string.Empty).Append('{').Append(i.ToString(CultureInfo.InvariantCulture)).Append('}');
        }

        var args = new object?[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            args[i] = IsPacked(parts[i]) ? parts[i] : Translatable(parts[i]);
        }

        return parts.Count == 1 ? parts[0] : Pack(tpl.ToString(), args);
    }

    /// <summary>Le texte est un message emballé.</summary>
    public static bool IsPacked(string? s) => s is { Length: > 1 } && s[0] == Mark && s[1] == '[';

    /// <summary>Rendu d'un texte (emballé ou non) dans la langue voulue ; un texte simple est traduit s'il est connu.</summary>
    public static string Render(string? packed, string lang)
    {
        if (string.IsNullOrEmpty(packed))
        {
            return string.Empty;
        }

        if (!IsPacked(packed))
        {
            return T(packed, lang);
        }

        try
        {
            return JsonNode.Parse(packed[1..]) is JsonArray arr ? RenderArray(arr, lang) : packed;
        }
        catch (JsonException)
        {
            return packed[1..];
        }
    }

    private static string RenderArray(JsonArray arr, string lang)
    {
        if (arr.Count == 0)
        {
            return string.Empty;
        }

        var tpl = arr[0]?.GetValue<string>() ?? string.Empty;
        if (arr.Count == 1)
        {
            return T(tpl, lang);
        }

        var args = new string[arr.Count - 1];
        for (int i = 1; i < arr.Count; i++)
        {
            args[i - 1] = arr[i] switch
            {
                JsonArray nested => RenderArray(nested, lang),
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                var n => n?.ToJsonString() ?? string.Empty,
            };
        }

        return Fill(T(tpl, lang), args);
    }

    private static string Invariant(object? a) => Convert.ToString(a, CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Remplit {0}, {1}… en une passe : un paramètre qui contient lui-même « {1} » n'est pas re-rempli.</summary>
    private static string Fill(string s, string[] args)
    {
        if (args.Length == 0 || s.IndexOf('{', StringComparison.Ordinal) < 0)
        {
            return s;
        }

        var sb = new System.Text.StringBuilder(s.Length + 32);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '{')
            {
                int j = s.IndexOf('}', i + 1);
                if (j > i + 1 && int.TryParse(s.AsSpan(i + 1, j - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int k) && k < args.Length)
                {
                    sb.Append(args[k]);
                    i = j;
                    continue;
                }
            }

            sb.Append(s[i]);
        }

        return sb.ToString();
    }

    private static IReadOnlyDictionary<string, string> Load(string resource)
    {
        using var s = typeof(Texts).Assembly.GetManifestResourceStream(resource);
        if (s is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using var r = new StreamReader(s);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(r.ReadToEnd()) ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
