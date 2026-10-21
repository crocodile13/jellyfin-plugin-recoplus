using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RecoPlus.Engine;
using Xunit;

namespace RecoPlus.Tests;

/// <summary>Textes français / anglais : messages emballés, rendu, et couverture du dictionnaire.</summary>
public class TextsTests
{
    [Fact]
    public void NormalizeKeepsFrenchOnlyForFrench()
    {
        Assert.Equal("fr", Texts.Normalize("fr-FR"));
        Assert.Equal("fr", Texts.Normalize(" FR "));
        Assert.Equal("en", Texts.Normalize("en-US"));
        Assert.Equal("en", Texts.Normalize("de"));
        Assert.Equal("en", Texts.Normalize(null));
    }

    [Fact]
    public void PackedMessagesRenderInTheReadersLanguage()
    {
        var m = Texts.Pack("lue ({0} entrées)", 12);
        Assert.True(Texts.IsPacked(m));
        Assert.Equal("lue (12 entrées)", Texts.Render(m, "fr"));
        Assert.Equal("read (12 entries)", Texts.Render(m, "en"));
        // un texte simple connu est traduit, un texte inconnu reste tel quel, rien ne casse sur null
        Assert.Equal("disabled", Texts.Render("désactivé", "en"));
        Assert.Equal("Mon titre", Texts.Render("Mon titre", "en"));
        Assert.Equal(string.Empty, Texts.Render(null, "en"));
        // sans paramètre : le texte lui-même (repérable par le test de couverture)
        Assert.Equal("actif", Texts.Pack("actif"));
    }

    [Fact]
    public void NestedAndTranslatableArgumentsAreRenderedToo()
    {
        var inner = Texts.Pack("Seerr a répondu {0} sur {1}", 503, "/api/v1/status");
        var outer = Texts.Pack("indisponible : {0}", inner);
        Assert.Equal("indisponible : Seerr a répondu 503 sur /api/v1/status", Texts.Render(outer, "fr"));
        Assert.Equal("unavailable: Seerr answered 503 on /api/v1/status", Texts.Render(outer, "en"));
        var lang = Texts.Pack("Bien noté en {0} (version originale)", Texts.Translatable("japonais"));
        Assert.Equal("Highly rated in Japanese (original language)", Texts.Render(lang, "en"));
        // une donnée (titre, genre) n'est jamais traduite, même si elle ressemble à une clé
        Assert.Equal("Highly rated in série", Texts.Render(Texts.Pack("Bien noté en {0}", "série"), "en"));
        var joined = Texts.Join(" ; ", new[] { Texts.Pack("{0} absente", "GetSuggestions"), "conforme" });
        Assert.Equal("GetSuggestions missing ; compliant", Texts.Render(joined, "en"));
    }

    [Fact]
    public void ParametersAreFilledOnceAndAreNeverReinterpreted()
    {
        // un paramètre qui contient lui-même « {1} » ou le séparateur ne doit rien casser
        var m = Texts.Pack("{0} — {1} entrées", "a{1}b\u001f\u0001[", 3);
        Assert.Equal("a{1}b\u001f\u0001[ — 3 entries", Texts.Render(m, "en"));
        Assert.Equal("x {9} y", Texts.T("x {9} y", "en", 1));
        // nombres en culture invariante, quel que soit le serveur
        Assert.Equal("1.5", Texts.Render(Texts.Pack("{0}", 1.5), "fr"));
    }

    [Fact]
    public void VerdictIsRenderedInBothLanguages()
    {
        var rep = Calibrator.Sufficiency(CalibrationMethod.ChoiceModel, 30);
        Assert.True(Texts.Render(rep.Message, "fr").StartsWith("30 cas pour", StringComparison.Ordinal));
        Assert.True(Texts.Render(rep.Message, "en").StartsWith("30 cases for", StringComparison.Ordinal));
        Assert.Equal("insufficient", Texts.Render(rep.Level, "en"));
    }

    // ------------------------------------------------------------------ dictionnaire

    private static string Root()
    {
        var env = Environment.GetEnvironmentVariable("RECOPLUS_ROOT");
        if (!string.IsNullOrEmpty(env))
        {
            return env;
        }

        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "src", "RecoPlus.Engine", "Texts.cs")))
            {
                return d.FullName;
            }
        }

        throw new InvalidOperationException("racine du dépôt introuvable (RECOPLUS_ROOT)");
    }

    private static readonly Regex Literal = new(@"(\$?@?)""((?:[^""\\\n]|\\.)*)""", RegexOptions.Compiled);
    private static readonly Regex French = new(@"[a-zà-ÿ]{2,}[ '’][a-zà-ÿ«»]|[à-ÿ]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Skip = new(@"^\s*//|_log\.|\bLog(Information|Warning|Error|Debug|Trace|Critical)\b|CommandText|\[Route|\[Http|GetManifestResourceStream|i18n:skip", RegexOptions.Compiled);
    private static readonly Regex Call = new(@"(?:Texts\.(?:Pack|T|Translatable)|\bL|\bMsg)\(\s*(?:[^""()]*\?\s*)?""((?:[^""\\\n]|\\.)*)""(?:\s*:\s*""((?:[^""\\\n]|\\.)*)"")?", RegexOptions.Compiled);

    /// <summary>
    /// Textes français des sources C# : tout littéral qui ressemble à du français (hors journal, commentaires et lignes
    /// marquées <c>i18n:skip</c>) et tout texte passé à Texts.Pack / T / Translatable, L ou Msg.
    /// </summary>
    private static Dictionary<string, string> SourceTexts()
    {
        var res = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (Skip.IsMatch(lines[i]))
                {
                    continue;
                }

                var where = Path.GetFileName(file) + ":" + (i + 1);
                foreach (Match m in Literal.Matches(lines[i]))
                {
                    if (French.IsMatch(m.Groups[2].Value))
                    {
                        Assert.True(m.Groups[1].Value.Length == 0 || !m.Groups[1].Value.Contains('$'),
                            "texte interpolé, donc intraduisible (utiliser Texts.Pack) : " + where);
                        res.TryAdd(Regex.Unescape(m.Groups[2].Value), where);
                    }
                }

                foreach (Match m in Call.Matches(lines[i]))
                {
                    foreach (var g in new[] { m.Groups[1], m.Groups[2] }.Where(g => g.Success && Regex.IsMatch(g.Value, "[A-Za-zÀ-ÿ]")))
                    {
                        res.TryAdd(Regex.Unescape(g.Value), where);
                    }
                }
            }
        }

        return res;
    }

    [Fact]
    public void EveryDisplayedTextOfTheServerHasAnEnglishTranslation()
    {
        var texts = SourceTexts();
        Assert.True(texts.Count > 150, "textes trouvés : " + texts.Count);
        // libellés construits à l'exécution
        foreach (var name in Calibrator.Knobs.Select(k => k.Name).Concat(Calibrator.Choices.Select(c => c.Name)).Concat(ChoiceModel.Weights.Select(w => w.Name))
                     .Concat(Exploration.LanguageNames.Values).Concat(new[] { "insuffisant", "limite", "suffisant" }))
        {
            texts.TryAdd(name, "exécution");
        }

        var missing = texts.Where(kv => !Texts.English.ContainsKey(kv.Key)).Select(kv => kv.Value + " « " + kv.Key + " »").ToList();
        Assert.True(missing.Count == 0, "sans traduction anglaise :\n" + string.Join("\n", missing));
    }

    [Fact]
    public void TranslationsKeepTheirPlaceholders()
    {
        var ph = new Regex(@"\{[\w:]+\}");
        foreach (var (fr, en) in Texts.English)
        {
            Assert.False(string.IsNullOrWhiteSpace(en), "traduction vide : " + fr);
            // {titre} et {title} désignent le même titre aimé (les deux sont acceptés)
            static List<string> Of(Regex r, string t) => r.Matches(t).Select(m => m.Value == "{title}" ? "{titre}" : m.Value).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            var a = Of(ph, fr);
            var b = Of(ph, en);
            Assert.True(a.SequenceEqual(b), "paramètres différents : « " + fr + " » → « " + en + " »");
        }
    }
}
