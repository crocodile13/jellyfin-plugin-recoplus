using System;
using System.Collections.Generic;
using System.Linq;
using RecoPlus.Engine;
using Xunit;

namespace RecoPlus.Tests;

/// <summary>Pourquoi un titre est proposé (fiche du titre) : vrai et net, sinon rien.</summary>
public class ReasonsTests
{
    private const double Now = 1_790_000_000;

    private static Title M(string id, string name, int year, string genre, string director, TitleKind kind = TitleKind.Movie)
        => new()
        {
            Id = id,
            Kind = kind,
            Name = name,
            Year = year,
            Genres = new[] { genre },
            Tags = new[] { "k" + genre },
            People = new Dictionary<string, IReadOnlyList<string>> { ["Director"] = new[] { director } },
        };

    [Fact]
    public void EachTitleGetsItsTrueReasonOrNone()
    {
        var titles = new[]
        {
            M("chihiro", "Chihiro", 2001, "Animation", "Miyazaki"), M("kiki", "Kiki", 1989, "Animation", "Miyazaki"),
            M("wanted", "Wanted", 2010, "Animation", "Miyazaki"), M("ponyo", "Ponyo", 2008, "Animation", "Miyazaki"),
            M("shining", "Shining", 1980, "Horror", "Kubrick"), M("doc", "Doc", 2020, "Documentary", "X"),
            M("ts1", "Toy Story", 1995, "Family", "Lasseter"), M("ts2", "Toy Story 2", 1999, "Family", "Lasseter"),
            M("ts3", "Toy Story 3", 2010, "Family", "Lasseter"), M("show", "Show", 2018, "Drama", "Y", TitleKind.Series),
            M("trend", "Trend", 2024, "Action", "Z"), M("new", "New", 2025, "Comedy", "W"),
        }.ToDictionary(t => t.Id, t => t, StringComparer.Ordinal);
        var seen = new Interaction { Title = "chihiro", Played = true, Completion = 1, Start = Now - 86400, Last = Now - 86400 };
        var h = new Dictionary<string, Interaction>(StringComparer.Ordinal)
        {
            ["chihiro"] = seen,
            ["wanted"] = new Interaction { Title = "wanted", Requested = Now - 86400 },
            ["ts1"] = new Interaction { Title = "ts1", Played = true, Completion = 1, Start = Now - 86400, Last = Now - 86400 },
            ["ts2"] = new Interaction { Title = "ts2", Played = true, Completion = 1, Start = Now - 86400, Last = Now - 86400 },
            ["show"] = new Interaction { Title = "show", Episodes = 5, Played = true, Start = Now - 86400, Last = Now - 86400 },
        };
        var users = new Dictionary<string, Dictionary<string, Interaction>>(StringComparer.Ordinal) { ["u"] = h };
        var ds = new Dataset { Titles = titles, Users = users, Now = Now };
        ds.Collections["toy"] = new List<string> { "ts1", "ts2", "ts3" };
        ds.LinkCollections();
        var s = new EngineSettings();
        var ctx = new Context(ds, Now, users, s);
        var vec = Core.BuildVectors(titles, s);
        var rows = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["PourToi"] = new() { "kiki", "doc" },
            ["ParceQue1"] = new() { "ponyo" },
            ["ParceQue2"] = new() { "kiki" },
            ["Tendances"] = new() { "trend" },
            ["Nouveautes"] = new() { "new" },
        };
        var anchors = new Dictionary<string, string>(StringComparer.Ordinal) { ["ParceQue1"] = "chihiro", ["ParceQue2"] = "wanted" };
        var r = Reasons.For(ctx, vec, "u", rows, anchors, new[] { "ts3", "show" }, row => row == HomeRows.Tendances ? Texts.Pack("En tendance cette semaine") : null);
        string R(string id) => Texts.Render(r[id], "fr");

        Assert.Equal("Suite de Toy Story 2", R("ts3"));                   // le dernier volet vu avant lui
        Assert.Equal("Nouveaux épisodes", R("show"));
        Assert.Equal("Parce que tu as aimé Chihiro", R("ponyo"));        // titre aimé de la rangée
        Assert.Equal("Parce que tu as aimé Chihiro", R("kiki"));         // première rangée qui le montre : titre aimé le plus proche
        Assert.Equal("En tendance cette semaine", R("trend"));
        Assert.Equal("Arrivé récemment", R("new"));
        Assert.False(r.ContainsKey("doc"), "sans lien net avec un titre aimé : aucune raison");
        Assert.Equal("Sequel to Toy Story 2", Texts.Render(r["ts3"], "en"));

        // rangée « Parce que … » partie d'un titre seulement demandé : « tu attends »
        var r2 = Reasons.For(ctx, vec, "u", new Dictionary<string, List<string>>(StringComparer.Ordinal) { ["ParceQue2"] = new() { "ponyo" } },
            anchors, Array.Empty<string>(), _ => null);
        Assert.Equal("Parce que tu attends Wanted", Texts.Render(r2["ponyo"], "fr"));
    }
}
