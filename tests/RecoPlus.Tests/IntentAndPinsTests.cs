using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.RecoPlus.Data;
using Jellyfin.Plugin.RecoPlus.Seerr;
using RecoPlus.Engine;
using Xunit;

namespace RecoPlus.Tests;

/// <summary>
/// Demandes, titres aimés sans être vus, marquages en masse et titres placés en tête (1.0.29) : une demande participe
/// aux goûts sans se recommander elle-même ; ce qui est en tête, ce sont les nouveautés de ce que le compte suit.
/// </summary>
public class IntentAndPinsTests
{
    private const double Now = 1_790_000_000;
    private const double Day = Core.Day;

    // ------------------------------------------------------------------ signaux

    [Fact]
    public void ALikeWithoutAWatchCountsButLessThanAWatchedLike()
    {
        var unseen = new Interaction { Title = "m", Favorite = true, Last = Now - Day };
        double Sig(double share) => Core.Signal(unseen, TitleKind.Movie, 0, Now, new EngineSettings { LikedUnseen = share });
        Assert.True(Sig(0.5) > 0);
        Assert.Equal(Sig(1) * 0.5, Sig(0.5), 12);
        Assert.Equal(0, Sig(0));

        var watched = new Interaction { Title = "m", Favorite = true, Played = true, Completion = 1, Start = Now - (2 * Day), Last = Now - Day };
        Assert.True(Core.Signal(watched, TitleKind.Movie, 0, Now, new EngineSettings()) > Sig(0.5), "vu et aimé pèse plus qu'aimé seul");
        // un pouce négatif sans visionnage reste un rejet franc (la réduction ne vaut que pour les goûts)
        var disliked = new Interaction { Title = "m", Rating = -1 };
        Assert.True(Core.Signal(disliked, TitleKind.Movie, 0, Now, new EngineSettings()) < 0);
    }

    [Fact]
    public void BulkMarkedEndsKeepTheRealStartOrBecomeUnknown()
    {
        var h = new List<Interaction>();
        for (int i = 0; i < 5; i++)
        {
            // commencés à des mois d'écart, « marqués comme vus » ensemble aujourd'hui
            h.Add(new Interaction { Title = "started" + i, Played = true, Completion = 1, Start = Now - ((100 + (20 * i)) * Day), Last = Now - 3600 + (10 * i) });
        }

        // jamais commencés ici, marqués dans la même minute : date inconnue
        h.Add(new Interaction { Title = "nostart", Played = true, Completion = 1, Last = Now - 3600 + 60 });
        // regardé pour de vrai, loin de la grappe
        h.Add(new Interaction { Title = "real", Played = true, Completion = 1, Start = Now - (3 * Day), Last = Now - (3 * Day) + 7200 });
        Core.MarkBackfill(h);

        for (int i = 0; i < 5; i++)
        {
            var x = h.Single(y => y.Title == "started" + i);
            Assert.False(x.Backfill);
            Assert.Equal(x.Start, x.Last);    // date de visionnage : le vrai début, pas le jour du marquage
        }

        Assert.True(h.Single(y => y.Title == "nostart").Backfill);
        var real = h.Single(y => y.Title == "real");
        Assert.False(real.Backfill);
        Assert.Equal(Now - (3 * Day) + 7200, real.Last);
    }

    // ------------------------------------------------------------------ demandes

    [Fact]
    public void IntentHoldsATitleBackThenLetsItComeBack()
    {
        var w = new EngineSettings { RequestHalfLifeDays = 21 };
        var t = new Title { Id = "a", Kind = TitleKind.Movie, Added = Now - (100 * Day) };
        Assert.Equal(1.0, Recommender.IntentFactor(null, t, Now, w));
        Assert.Equal(1.0, Recommender.IntentFactor(new Interaction { Title = "a" }, t, Now, w));
        Assert.Equal(0.0, Recommender.IntentFactor(new Interaction { Title = "a", Requested = Now }, t, Now, w), 12);
        Assert.Equal(0.5, Recommender.IntentFactor(new Interaction { Title = "a", Requested = Now - (21 * Day) }, t, Now, w), 12);
        Assert.Equal(0.5, Recommender.IntentFactor(new Interaction { Title = "a", Watchlisted = Now - (21 * Day) }, t, Now, w), 12);
        // demandé il y a longtemps mais arrivé hier : c'est l'arrivée qui compte (il ne l'a pas encore eu sous la main)
        var late = new Title { Id = "a", Kind = TitleKind.Movie, Added = Now - Day };
        Assert.True(Recommender.IntentFactor(new Interaction { Title = "a", Requested = Now - (200 * Day) }, late, Now, w) < 0.05);
    }

    [Fact]
    public void ARequestIsHeldBackForOneHalfLifeThenRanked()
    {
        var w = new EngineSettings { RequestHalfLifeDays = 21 };
        var t = new Title { Id = "a", Kind = TitleKind.Movie, Added = Now - (100 * Day) };
        Assert.True(Recommender.HeldBack(new Interaction { Title = "a", Requested = Now - (5 * Day) }, t, Now, w));
        Assert.False(Recommender.HeldBack(new Interaction { Title = "a", Requested = Now - (22 * Day) }, t, Now, w));
        Assert.False(Recommender.HeldBack(new Interaction { Title = "a" }, t, Now, w));
        Assert.False(Recommender.HeldBack(null, t, Now, w));
        // demandé il y a longtemps, arrivé il y a 5 jours : encore retenu
        var late = new Title { Id = "a", Kind = TitleKind.Movie, Added = Now - (5 * Day) };
        Assert.True(Recommender.HeldBack(new Interaction { Title = "a", Requested = Now - (300 * Day) }, late, Now, w));
    }

    private static (Context Ctx, Dictionary<string, SparseVector> Vec, List<string> Cands, EngineSettings S) Library(double requestedAt)
    {
        var titles = new Dictionary<string, Title>(StringComparer.Ordinal);
        for (int i = 0; i < 30; i++)
        {
            var id = "t" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            titles[id] = new Title { Id = id, Kind = TitleKind.Movie, Name = id, Genres = new[] { i % 2 == 0 ? "Horreur" : "Comédie" }, Tags = new[] { "k" + (i % 5) }, Added = Now - (300 * Day) };
        }

        Interaction L(string id) => new() { Title = id, Played = true, Completion = 1, Start = Now - (6 * Day), Last = Now - (5 * Day) };
        var h = new Dictionary<string, Interaction>(StringComparer.Ordinal) { ["t00"] = L("t00"), ["t02"] = L("t02"), ["t04"] = L("t04") };
        h["t10"] = new Interaction { Title = "t10", Requested = requestedAt };
        var users = new Dictionary<string, Dictionary<string, Interaction>>(StringComparer.Ordinal) { ["u"] = h };
        var ds = new Dataset { Titles = titles, Users = users, Now = Now };
        var s = new EngineSettings();
        var ctx = new Context(ds, Now, users, s);
        var cands = titles.Keys.Where(k => k != "t00" && k != "t02" && k != "t04").OrderBy(k => k, StringComparer.Ordinal).ToList();
        return (ctx, Core.BuildVectors(titles, s), cands, s);
    }

    [Fact]
    public void ARequestNeverRecommendsItselfButMayComeBackLater()
    {
        var (ctx, vec, cands, s) = Library(Now - 3600);
        var plain = Recommender.Score(ctx, vec, "u", cands, s, prior: Recommender.ColdStart(ctx.Sig["u"]));
        var all = Recommender.ScoreAll(ctx, vec, "u", cands, s);
        // les autres titres : inchangés (la demande compte toujours pour les goûts)
        Assert.All(cands.Where(c => c != "t10"), c => Assert.Equal(plain[c], all[c], 12));
        // le titre demandé : écarté juste après la demande, et jamais en tête grâce à sa propre demande
        Assert.True(all["t10"] < plain["t10"]);
        Assert.True(all["t10"] <= cands.Where(c => c != "t10").Max(c => all[c]));

        // longtemps après, toujours pas vu : il revient à son score « sans sa demande »
        var (ctx2, vec2, cands2, s2) = Library(Now - (365 * Day));
        var without = new Dictionary<string, double>(ctx2.Sig["u"], StringComparer.Ordinal);
        without.Remove("t10");
        var bare = Recommender.Score(ctx2, vec2, "u", cands2, s2, signals: without, prior: Recommender.ColdStart(ctx2.Sig["u"]));
        var later = Recommender.ScoreAll(ctx2, vec2, "u", cands2, s2);
        Assert.Equal(bare["t10"], later["t10"], 3);   // demandé il y a un an, arrivé il y a 300 jours : ½^(300/21) près
        Assert.True(later["t10"] > all["t10"]);
    }

    [Fact]
    public void ANewAccountGetsWhatTheHouseholdWatchesNotAnArbitraryOrder()
    {
        // sans goût connu, tous les scores de goût valent 0 : l'a priori (popularité dans le foyer, note) décide
        Assert.Equal(Recommender.ColdStartWeight, Recommender.ColdStart(null), 12);
        Assert.Equal(Recommender.ColdStartWeight / 2, Recommender.ColdStart(new Dictionary<string, double> { ["a"] = Recommender.ColdStartMass }), 12);
        Assert.True(Recommender.ColdStart(new Dictionary<string, double> { ["a"] = 80 }) < 1e-3);
        Assert.Equal(Recommender.ColdStartWeight, Recommender.ColdStart(new Dictionary<string, double> { ["a"] = -3 }), 12);   // seuls les goûts comptent

        var titles = new Dictionary<string, Title>(StringComparer.Ordinal);
        for (int i = 0; i < 6; i++)
        {
            var id = "t" + i;
            titles[id] = new Title { Id = id, Kind = TitleKind.Movie, Name = id, Genres = new[] { "g" + i }, Added = Now - (300 * Day), Community = i == 5 ? 9 : 6.5 };
        }

        var users = new Dictionary<string, Dictionary<string, Interaction>>(StringComparer.Ordinal)
        {
            ["new"] = new(StringComparer.Ordinal),
            ["a"] = new(StringComparer.Ordinal) { ["t2"] = new Interaction { Title = "t2", Played = true, Completion = 1, Start = Now - Day, Last = Now - Day } },
            ["b"] = new(StringComparer.Ordinal) { ["t2"] = new Interaction { Title = "t2", Played = true, Completion = 1, Start = Now - Day, Last = Now - Day } },
        };
        var ds = new Dataset { Titles = titles, Users = users, Now = Now };
        var w = new EngineSettings();
        var ctx = new Context(ds, Now, users, w);
        var sc = Recommender.ScoreAll(ctx, Core.BuildVectors(titles, w), "new", titles.Keys.ToList(), w);
        Assert.Equal("t2", sc.OrderByDescending(kv => kv.Value).First().Key);   // ce que le foyer regarde
        Assert.True(sc["t5"] > sc["t0"]);                                          // puis la mieux notée
    }

    // ------------------------------------------------------------------ en tête

    private sealed class World
    {
        public Dictionary<string, Title> Titles { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Interaction> H { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<(double Added, double Aired)>> Eps { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<string>> Colls { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Denied { get; } = new(StringComparer.Ordinal);

        public void Series(string id, int old, double oldAt, int fresh, double freshAt, double aired = 0)
        {
            Titles[id] = new Title { Id = id, Kind = TitleKind.Series, Name = id, Added = oldAt };
            Eps[id] = Enumerable.Repeat((oldAt, oldAt), old).Concat(Enumerable.Repeat((freshAt, aired), fresh)).ToList();
        }

        public void Movie(string id, double added, double released, string? coll = null)
        {
            Titles[id] = new Title { Id = id, Kind = TitleKind.Movie, Name = id, Added = added, Released = released };
            if (coll is not null)
            {
                if (!Colls.TryGetValue(coll, out var l))
                {
                    Colls[coll] = l = new List<string>();
                }

                l.Add(id);
            }
        }

        public List<string> Pins(int max = 5)
        {
            var users = new Dictionary<string, Dictionary<string, Interaction>>(StringComparer.Ordinal) { ["u"] = H };
            var ds = new Dataset { Titles = Titles, Users = users, Now = Now };
            foreach (var (k, v) in Colls)
            {
                ds.Collections[k] = v;
            }

            ds.LinkCollections();
            var s = new EngineSettings();
            var sig = new Context(ds, Now, users, s).Sig["u"];
            return RecoPlus.Engine.Pins.Select(ds, H, sig, id => !Denied.Contains(id), id => Eps.TryGetValue(id, out var e) ? e : null, Now, new PinOptions(max, 45, s.SagaMax));
        }
    }

    private static Interaction Watched(string id, double last, int episodes = 0)
        => new() { Title = id, Played = true, Completion = 1, Episodes = episodes, Start = last - Day, Last = last };

    [Fact]
    public void NewEpisodesOfACaughtUpSeriesGoFirstWithoutNeedingALike()
    {
        var w = new World();
        w.Series("caught", 10, Now - (200 * Day), 3, Now - (2 * Day));
        w.H["caught"] = Watched("caught", Now - (30 * Day), 10);             // vu, pas aimé : un poids positif suffit
        w.Series("halfway", 10, Now - (200 * Day), 3, Now - (2 * Day));
        w.H["halfway"] = Watched("halfway", Now - (30 * Day), 4);            // laissée en plan : pas « suivie »
        w.Series("stale", 10, Now - (200 * Day), 3, Now - (90 * Day));
        w.H["stale"] = Watched("stale", Now - (120 * Day), 10);              // nouveautés trop anciennes
        w.Series("hidden", 10, Now - (200 * Day), 3, Now - (2 * Day));
        w.H["hidden"] = Watched("hidden", Now - (30 * Day), 10);
        w.H["hidden"].Hidden = true;
        w.Series("denied", 10, Now - (200 * Day), 3, Now - (2 * Day));
        w.H["denied"] = Watched("denied", Now - (30 * Day), 10);
        w.Denied.Add("denied");
        Assert.Equal(new[] { "caught" }, w.Pins());
        Assert.Empty(w.Pins(0));
    }

    [Fact]
    public void ALikedSeriesSeenElsewhereNeedsEpisodesThatAiredRecently()
    {
        var w = new World();
        // aimée sans être vue ici : on ne sait pas jusqu'où il l'a vue, seuls les épisodes diffusés récemment sont « nouveaux »
        w.Series("fresh", 10, Now - (200 * Day), 2, Now - (3 * Day), aired: Now - (4 * Day));
        w.H["fresh"] = new Interaction { Title = "fresh", Favorite = true };
        w.Series("backlog", 10, Now - (200 * Day), 20, Now - (3 * Day), aired: Now - (2000 * Day));   // vieille saison ajoutée
        w.H["backlog"] = new Interaction { Title = "backlog", Favorite = true };
        Assert.Equal(new[] { "fresh" }, w.Pins());
    }

    [Fact]
    public void TheNextPartOfASagaGoesFirstButNeverARequestNorAFullySeenSaga()
    {
        var w = new World();
        w.Movie("p1", Now - (400 * Day), Now - (3000 * Day), "saga");
        w.Movie("p2", Now - (400 * Day), Now - (2000 * Day), "saga");
        w.Movie("p3", Now - (5 * Day), Now - (300 * Day), "saga");           // volet suivant, arrivé récemment
        w.H["p1"] = Watched("p1", Now - (100 * Day));
        w.H["p2"] = Watched("p2", Now - (90 * Day));
        Assert.Equal(new[] { "p3" }, w.Pins());

        // demandé par le compte : il sait déjà qu'il le veut
        w.H["p3"] = new Interaction { Title = "p3", Requested = Now - (40 * Day) };
        Assert.Empty(w.Pins());

        // toute la saga vue : rien à placer
        w.H["p3"] = Watched("p3", Now - Day);
        Assert.Empty(w.Pins());

        // un volet précédent pas vu : ce n'est pas le « suivant »
        var v = new World();
        v.Movie("a1", Now - (400 * Day), Now - (3000 * Day), "s");
        v.Movie("a2", Now - (400 * Day), Now - (2000 * Day), "s");
        v.Movie("a3", Now - (5 * Day), Now - (300 * Day), "s");
        v.H["a1"] = Watched("a1", Now - (100 * Day));
        Assert.Empty(v.Pins());
    }

    [Fact]
    public void ASagaLikedElsewhereOnlyPinsARecentRelease()
    {
        var w = new World();
        w.Movie("p1", Now - (400 * Day), Now - (3000 * Day), "saga");
        w.Movie("p2", Now - (5 * Day), Now - (2000 * Day), "saga");          // vieux film ajouté récemment
        w.H["p1"] = new Interaction { Title = "p1", Favorite = true };
        Assert.Empty(w.Pins().Where(id => id == "p2"));

        var n = new World();
        n.Movie("p1", Now - (400 * Day), Now - (3000 * Day), "saga");
        n.Movie("p2", Now - (5 * Day), Now - (20 * Day), "saga");            // sorti il y a 20 jours
        n.H["p1"] = new Interaction { Title = "p1", Favorite = true };
        Assert.Equal(new[] { "p2" }, n.Pins());
    }

    // ------------------------------------------------------------------ Playback Reporting, Seerr

    [Fact]
    public void LastRealPlaysTakeTheLatestStartAcrossOldFiles()
    {
        Guid u = Guid.NewGuid(), movie = Guid.NewGuid(), oldMovie = Guid.NewGuid();
        var known = new HashSet<Guid> { movie };
        var movies = HistoryMath.NameIndex(new[] { (movie, "Alien") });
        var series = HistoryMath.NameIndex(Array.Empty<(Guid, string)>());
        var rows = new[]
        {
            new PlaybackStart(u, movie, "Movie", "Alien", 1000, 1500),
            new PlaybackStart(u, oldMovie, "Movie", "Alien", 200, 3000),      // ancien fichier, relu plus tard
            new PlaybackStart(u, Guid.NewGuid(), "Movie", "Inconnu", 10, 20),
        };
        Assert.Equal(200, HistoryMath.MapPlaybackStarts(rows, known.Contains, movies, series, out _)[(u, movie)]);
        Assert.Equal(3000, HistoryMath.MapPlaybackLasts(rows, known.Contains, movies, series)[(u, movie)]);
        // dernière date inconnue (ancienne ligne) : le premier lancement en tient lieu
        Assert.Equal(1000, HistoryMath.MapPlaybackLasts(new[] { new PlaybackStart(u, movie, "Movie", "Alien", 1000) }, known.Contains, movies, series)[(u, movie)]);
    }

    [Fact]
    public void MovieRuntimeIsReadFromSeerr()
    {
        using var movie = JsonDocument.Parse("""{"title":"Short","releaseDate":"2020-01-01","runtime":12,"voteCount":80}""");
        Assert.Equal(12, SeerrClient.ParseDetails("movie", 1, movie.RootElement).Runtime);
        using var unknown = JsonDocument.Parse("""{"title":"X","releaseDate":"2020-01-01","runtime":0}""");
        Assert.Null(SeerrClient.ParseDetails("movie", 2, unknown.RootElement).Runtime);
        using var tv = JsonDocument.Parse("""{"name":"S","firstAirDate":"2020-01-01","episodeRunTime":[24]}""");
        Assert.Null(SeerrClient.ParseDetails("tv", 3, tv.RootElement).Runtime);
    }
}
