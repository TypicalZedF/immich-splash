using System.IO.Compression;
using ImmichSplash.Core.Import;
using ImmichSplash.Core.Sync;

namespace ImmichSplash.Core.Tests;

public class GoogleTakeoutTests
{
    static string Json(long taken, double lat = 0, double lon = 0, string description = "", bool favorite = false) =>
        $$"""{"title":"x","description":"{{description}}","photoTakenTime":{"timestamp":"{{taken}}"},"geoData":{"latitude":{{lat}},"longitude":{{lon}}},"favorited":{{(favorite ? "true" : "false")}}}""";

    [Fact]
    public void Sidecars_are_found_however_Google_named_them()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "Takeout", "Google Photos");
        void Write(string relative, string text)
        {
            var full = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        temp.File("Takeout/Google Photos/Trip/plain.jpg");
        Write("Trip/plain.jpg.json", Json(1_600_000_000, 35.68, 139.69, "Tokyo", favorite: true));
        temp.File("Takeout/Google Photos/Trip/new.jpg");
        Write("Trip/new.jpg.supplemental-metadata.json", Json(1_600_000_100));
        temp.File("Takeout/Google Photos/Trip/copy(1).jpg");
        Write("Trip/copy.jpg.supplemental-metadata(1).json", Json(1_600_000_200));
        temp.File("Takeout/Google Photos/Trip/plain-edited.jpg");
        const string longName = "PXL_20230101_123456789.PORTRAIT.ORIGINAL.jpg";
        temp.File($"Takeout/Google Photos/Trip/{longName}");
        Write("Trip/PXL_20230101_123456789.PORTRAIT.ORIGINAL.jpg.suppl.json", Json(1_600_000_300));
        Write("Trip/metadata.json", """{"title":"Japan 2020","date":{"timestamp":"1600000000"}}""");
        temp.File("Takeout/Google Photos/Photos from 2020/loose.mp4");

        var items = GoogleTakeout.Scan([Path.Combine(temp.Path, "Takeout")], MediaTypes.Default).ToDictionary(i => i.FileName);

        Assert.Equal(6, items.Count);
        var plain = items["plain.jpg"].Metadata!;
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_600_000_000), plain.Taken);
        Assert.Equal(35.68, plain.Latitude);
        Assert.Equal("Tokyo", plain.Description);
        Assert.True(plain.Favorite);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_600_000_100), items["new.jpg"].Metadata!.Taken);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_600_000_200), items["copy(1).jpg"].Metadata!.Taken);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_600_000_000), items["plain-edited.jpg"].Metadata!.Taken);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_600_000_300), items[longName].Metadata!.Taken);
        Assert.Null(items["new.jpg"].Metadata!.Latitude); // 0,0 means no place
        Assert.Equal("Japan 2020", items["plain.jpg"].Album);
        Assert.Null(items["loose.mp4"].Album);
    }

    [Fact]
    public async Task A_zip_imports_with_its_dates_places_and_album()
    {
        using var temp = new TempDir();
        var zipPath = Path.Combine(temp.Path, "takeout-001.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            void Add(string name, byte[] data)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
            Add("Takeout/Google Photos/Party/a.jpg", Guid.NewGuid().ToByteArray());
            Add("Takeout/Google Photos/Party/a.jpg.json", System.Text.Encoding.UTF8.GetBytes(Json(1_500_000_000, 51.5, -0.12)));
            Add("Takeout/Google Photos/Party/metadata.json", """{"title":"Party"}"""u8.ToArray());
        }
        var server = new FakeImmichServer();
        using var client = new Api.ImmichClient(new Uri("https://immich.test/api/"), new Api.AuthCredential(Api.AuthKind.ApiKey, "key"), server);

        var items = GoogleTakeout.Scan([zipPath], MediaTypes.Default);
        var result = await GoogleTakeout.ImportAsync(client, items, new Api.BandwidthLimiter(0), null, CancellationToken.None);

        Assert.Equal(1, result.Uploaded);
        Assert.Equal(1, result.Albums);
        Assert.Contains(server.AssetUpdates, u => u.Contains("\"latitude\":51.5", StringComparison.Ordinal));
        var again = await GoogleTakeout.ImportAsync(client, items, new Api.BandwidthLimiter(0), null, CancellationToken.None);
        Assert.Equal(0, again.Uploaded);
        Assert.Equal(1, again.AlreadyThere);
    }
}
