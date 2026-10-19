using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.RecoPlus.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RecoPlus.Tests;

public class JellyfinEnhancedReaderTests
{
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static string Dir()
    {
        var d = Path.Combine(Path.GetTempPath(), "recoplus-je-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void ReadsGlobalHidesOfMoviesAndSeriesAndRatings()
    {
        var d = Dir();
        try
        {
            Directory.CreateDirectory(Path.Combine(d, Alice.ToString("N")));
            // écrit par Newtonsoft (PascalCase), identifiant TMDb en texte ou en nombre selon les versions
            File.WriteAllText(Path.Combine(d, Alice.ToString("N"), "hidden-content.json"), @"{
              ""Items"": {
                ""a"": { ""ItemId"": ""11111111-1111-1111-1111-111111111111"", ""Type"": ""Movie"", ""TmdbId"": ""603"", ""HideScope"": ""global"" },
                ""b"": { ""ItemId"": """", ""Type"": ""Series"", ""TmdbId"": 1399 },
                ""c"": { ""ItemId"": ""22222222-2222-2222-2222-222222222222"", ""Type"": ""Episode"", ""TmdbId"": ""5"", ""HideScope"": ""global"" },
                ""d"": { ""ItemId"": ""33333333-3333-3333-3333-333333333333"", ""Type"": ""Series"", ""HideScope"": ""continuewatching"" }
              },
              ""Settings"": { ""Enabled"": true }
            }");
            File.WriteAllText(Path.Combine(d, "reviews.json"), @"{ ""Reviews"": {
              ""x"": { ""UserId"": """ + Alice.ToString("N") + @""", ""TmdbId"": ""27205"", ""MediaType"": ""movie"", ""Rating"": 4.5 },
              ""y"": { ""UserId"": """ + Alice.ToString("N") + @""", ""TmdbId"": 1396, ""MediaType"": ""tv"", ""Rating"": 1 },
              ""z"": { ""UserId"": """ + Alice.ToString("N") + @""", ""TmdbId"": ""1"", ""MediaType"": ""movie"" },
              ""w"": { ""UserId"": ""cccccccccccccccccccccccccccccccc"", ""TmdbId"": ""2"", ""MediaType"": ""movie"", ""Rating"": 5 }
            } }");

            var r = new JellyfinEnhancedReader(d, NullLogger<JellyfinEnhancedReader>.Instance);
            var all = r.ReadAll(new[] { Alice, Bob });
            var a = all[Alice.ToString("N")];
            Assert.Equal(new[] { "11111111111111111111111111111111" }, a.HiddenItems.ToArray());
            Assert.Equal(new[] { "movie:603", "tv:1399" }, a.HiddenTmdb.OrderBy(x => x, StringComparer.Ordinal).ToArray());
            Assert.Equal(4.5, a.Ratings["movie:27205"]);
            Assert.Equal(1.0, a.Ratings["tv:1396"]);
            Assert.False(a.Ratings.ContainsKey("movie:1"), "avis sans note ignoré");
            Assert.Empty(all[Bob.ToString("N")].HiddenItems);
            Assert.Contains("2 titres masqués", r.State, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(d, true);
        }
    }

    [Fact]
    public void BrokenOrMissingFilesNeverThrow()
    {
        var d = Dir();
        try
        {
            Directory.CreateDirectory(Path.Combine(d, Alice.ToString("N")));
            File.WriteAllText(Path.Combine(d, Alice.ToString("N"), "hidden-content.json"), "{ \"Items\": [ oups");
            var r = new JellyfinEnhancedReader(d, NullLogger<JellyfinEnhancedReader>.Instance);
            var all = r.ReadAll(new[] { Alice });
            Assert.Empty(all[Alice.ToString("N")].HiddenItems);
            Assert.Contains("illisible", r.State, StringComparison.Ordinal);

            var none = new JellyfinEnhancedReader(Path.Combine(d, "absent"), NullLogger<JellyfinEnhancedReader>.Instance);
            Assert.Empty(none.ReadAll(new[] { Alice }));
            Assert.Contains("absent", none.State, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(d, true);
        }
    }
}
