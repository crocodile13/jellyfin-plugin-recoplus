using System;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.RecoPlus.Data;
using Xunit;

namespace RecoPlus.Tests;

public class LogTailTests
{
    private const string Sample =
        "[18:04:37] [WRN] [52] Jellyfin.Plugin.HomeScreenSections.Library.ITranslationManager: No translation found for key 'RecoPlusFilms'\n" +
        "[18:04:52] [INF] [58] Jellyfin.Plugin.RecoPlus.Service.RecoWorker: Reco+ : recalcul de tous les comptes mis en file\n" +
        "[18:05:00] [ERR] [61] Jellyfin.Plugin.RecoPlus.Service.RecoService: Reco+ : échec du calcul\n" +
        "System.InvalidOperationException: boom\n" +
        "   at Jellyfin.Plugin.RecoPlus.Service.RecoService.ComputeCore()\n" +
        "[18:05:01] [INF] [12] Emby.Server.Implementations.HttpServer: GET /Items?api_key=abcdef0123456789 X-Emby-Token: zzz999 done\n" +
        "[18:05:02] [WRN] [12] Some.Other.Component: Reco+ mentioned here\n";

    [Fact]
    public void ParseKeepsRecoEntriesWithTheirStackTrace()
    {
        var e = LogTail.Parse(Sample.Split('\n'), all: false);
        Assert.Equal(new[] { "RecoWorker", "RecoService", "Component" }, e.Select(x => x.Category.Split('.').Last()));
        Assert.Contains("InvalidOperationException", e[1].Message);
        Assert.Contains("ComputeCore", e[1].Message);
        Assert.Equal("ERR", e[1].Level);
        Assert.Equal("18:05:00", e[1].Time);
        Assert.Equal(5, LogTail.Parse(Sample.Split('\n'), all: true).Count);
    }

    [Fact]
    public void SecretsAreMasked()
    {
        var all = LogTail.Parse(Sample.Split('\n'), all: true);
        var http = all.Single(x => x.Category.EndsWith("HttpServer", StringComparison.Ordinal)).Message;
        Assert.DoesNotContain("abcdef0123456789", http);
        Assert.DoesNotContain("zzz999", http);
        Assert.Contains("api_key=***", http);
        Assert.Equal("Authorization: Bearer ***", LogTail.Redact("Authorization: Bearer abc.def"));
        Assert.Equal("{\"password\":\"***\"}", LogTail.Redact("{\"password\":\"hunter2\"}"));
    }

    [Fact]
    public void ReadFollowsTheFileFromACursor()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rp-logtail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "log_20261020.log");
            File.WriteAllText(f, Sample);
            var a = LogTail.Read(dir, null, all: false);
            Assert.Equal(3, a.Entries.Count);
            Assert.Equal("log_20261020.log", a.File);
            Assert.Empty(LogTail.Read(dir, a.Cursor, all: false).Entries);                     // rien de neuf

            File.AppendAllText(f, "[18:06:00] [INF] [3] Jellyfin.Plugin.RecoPlus.Service.RecoService: Reco+ : calcul complet terminé\n[18:06:01] [INF] [3] Jellyfin.Plugin.RecoPlus.Service.RecoService: ligne incompl");
            var b = LogTail.Read(dir, a.Cursor, all: false);
            Assert.Single(b.Entries);                                                           // ligne incomplète gardée pour plus tard
            File.AppendAllText(f, "ète\n");
            var c = LogTail.Read(dir, b.Cursor, all: false);
            Assert.Equal("ligne incomplète", c.Entries.Single().Message);

            // nouveau fichier (jour suivant) : lu depuis son début ; un curseur farfelu ne sert jamais de chemin
            var g = Path.Combine(dir, "log_20261021.log");
            File.WriteAllText(g, "[00:00:01] [INF] [3] Jellyfin.Plugin.RecoPlus.Service.RecoWorker: nouveau jour\n");
            File.SetLastWriteTimeUtc(g, DateTime.UtcNow.AddMinutes(1));
            Assert.Equal("nouveau jour", LogTail.Read(dir, c.Cursor, all: false).Entries.Single().Message);
            Assert.Equal("nouveau jour", LogTail.Read(dir, "../../etc/passwd|0", all: false).Entries.Single().Message);
            Assert.Empty(LogTail.Read(Path.Combine(dir, "absent"), null, all: false).Entries);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
