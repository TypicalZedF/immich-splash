using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ImmichSplash.App.Services;
using ImmichSplash.App.ViewModels;
using ImmichSplash.App.Views;
using ImmichSplash.Core.Api;
using ImmichSplash.Core.Platform;
using ImmichSplash.Core.Settings;
using ImmichSplash.Core.Sync;
using Material.Icons;

namespace ImmichSplash.DevTools;

/// <summary>Renders every screen with realistic sample data, for design review.</summary>
static class Screens
{
    static string _root = "";

    /// <summary>Screens to save (name contains any of these); empty = all. The others still run, unsaved.</summary>
    public static string[] Only = [];
    /// <summary>Only the "light" or "dark" pass; empty = both.</summary>
    public static string Theme = "";
    /// <summary>Saved size relative to the window (0.5 = a quarter of the pixels, cheaper to review).</summary>
    public static double Scale = 1;

    static bool Wanted(string name) => Only.Length == 0 || Only.Any(o => name.Contains(o, StringComparison.OrdinalIgnoreCase));

    static void Save(Avalonia.Media.Imaging.Bitmap? frame, string dir, string name)
    {
        if (frame is null)
            return;
        if (Scale is > 0 and < 1)
        {
            var size = new PixelSize(Math.Max(1, (int)(frame.PixelSize.Width * Scale)), Math.Max(1, (int)(frame.PixelSize.Height * Scale)));
            // Headless frames can't be scaled directly; re-decode the PNG at the smaller width instead.
            using var png = new MemoryStream();
            frame.Save(png);
            png.Position = 0;
            using var scaled = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(png, size.Width, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
            scaled.Save(Path.Combine(dir, name + ".png"));
        }
        else
            frame.Save(Path.Combine(dir, name + ".png"));
    }

    public static void PrepareData()
    {
        // Demo folders live at a neutral path (never the real user profile) so screenshots don't reveal names.
        _root = OperatingSystem.IsWindows()
            ? Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "ImmichDemo")
            : Path.Combine(Path.GetTempPath(), "ImmichDemo");
        var data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "immich-splash-screens", Guid.NewGuid().ToString("N"))).FullName;
        Environment.SetEnvironmentVariable("IMMICH_SPLASH_DATA_DIR", data);

        var pictures = Directory.CreateDirectory(Path.Combine(_root, "Pictures")).FullName;
        var camera = Directory.CreateDirectory(Path.Combine(_root, "Phone Camera")).FullName;
        var videos = Directory.CreateDirectory(Path.Combine(_root, "Videos")).FullName;

        var settings = new AppSettings
        {
            ServerUrl = "https://photos.example.com/api/",
            WebUrl = "https://photos.example.com/",
            ServerVersion = "v3.3.0",
            UserId = "u1",
            UserName = "Alex Morgan",
            UserEmail = "alex@example.com",
            Folders =
            [
                new SyncFolder { Id = "pictures", Path = pictures },
                new SyncFolder { Id = "camera", Path = camera, AlbumName = "Phone", ExcludePatterns = ["Screenshots", "*.png"] },
                new SyncFolder { Id = "videos", Path = videos, IncludePhotos = false },
            ],
        };
        File.WriteAllText(Path.Combine(data, "settings.json"),
            JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
    }

    static void Seed()
    {
        var db = AppServices.Database;
        void Add(string folder, string dir, int done, int pending, int failed)
        {
            var files = Enumerable.Range(0, done + pending + failed)
                .Select(i => new ScannedFile(Path.Combine(_root, dir, $"IMG_{i:0000}.jpg"), folder, 1000 + i, 1000 + i))
                .ToList();
            db.Upsert(files);
            foreach (var f in files.Take(done)) db.MarkDone(f.Path, FileStatus.Uploaded, Guid.NewGuid().ToString());
            foreach (var f in files.Skip(done + pending)) db.MarkFailed(f.Path, "The file is too large for the server (check your reverse proxy upload limit).", DateTime.UtcNow.AddMinutes(30));
        }
        Add("pictures", "Pictures", 2814, 0, 0);
        Add("camera", "Phone Camera", 1203, 41, 2);
        Add("videos", "Videos", 96, 4, 0);

        // Photos tab: a few October photos came from this PC (both), and a few new files aren't backed up yet.
        for (var i = 0; i < 23; i += 3)
        {
            var path = Path.Combine(_root, "Phone Camera", $"PXL_202610{i:00}.jpg");
            db.Upsert([new ScannedFile(path, "camera", 3_000_000, DateTime.UtcNow.AddDays(-30).Ticks)]);
            db.MarkDone(path, FileStatus.Uploaded, $"202610-{i}");
        }
        var colors = new[] { (0xFF7AA6C2u, 0xFFE8C07Du), (0xFF5D8A66u, 0xFFCFE3B0u), (0xFFB86B77u, 0xFFF2D0A4u) };
        for (var i = 0; i < 4; i++)
        {
            var path = Path.Combine(_root, "Phone Camera", i == 3 ? "VID_20261013_091500.mp4" : $"PXL_20261013_0{i}1500.jpg");
            if (i < 3)
                WriteDemoPhoto(path, colors[i].Item1, colors[i].Item2);
            else
                File.WriteAllBytes(path, new byte[1024]);
            var when = new DateTime(2026, 10, 29, 12 - i, 0, 0, DateTimeKind.Utc); // newest in the demo timeline
            File.SetLastWriteTimeUtc(path, when);
            db.Upsert([new ScannedFile(path, "camera", new FileInfo(path).Length, when.Ticks)]);
            if (i == 2)
                db.MarkFailed(path, "The server is unreachable.", DateTime.UtcNow.AddMinutes(30));
        }

        var activity = AppServices.Activity;
        activity.Add(ActivityKind.Info, "Setup complete — backing up 3 folders");
        activity.Add(ActivityKind.Success, "Backed up 1,204 new files · 2,910 already on the server");
        activity.Add(ActivityKind.Warning, "Skipped notes.heic — file type not supported by the server", Path.Combine(_root, "Pictures", "notes.heic"));
        activity.Add(ActivityKind.Error, "Couldn't upload VID_2041.mp4: The file is too large for the server (check your reverse proxy upload limit).", Path.Combine(_root, "Phone Camera", "VID_2041.mp4"));
        foreach (var name in new[] { "IMG_4410.HEIC", "IMG_4411.HEIC", "PXL_20261011_174512.jpg", "VID_20261011_180233.mp4", "IMG_4412.HEIC" })
            activity.Add(ActivityKind.Uploaded, name, Path.Combine(_root, "Phone Camera", name));
    }

    static void WriteDemoPhoto(string path, uint top, uint bottom)
    {
        using var bitmap = new SkiaSharp.SKBitmap(900, 600);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        using (var paint = new SkiaSharp.SKPaint())
        {
            paint.Shader = SkiaSharp.SKShader.CreateLinearGradient(new(0, 0), new(0, 600), [new SkiaSharp.SKColor(top), new SkiaSharp.SKColor(bottom)], SkiaSharp.SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, 900, 600, paint);
            paint.Shader = null;
            paint.Color = new SkiaSharp.SKColor(0x55FFFFFF);
            canvas.DrawCircle(640, 220, 90, paint);
        }
        File.WriteAllBytes(path, ImmichSplash.App.Services.Images.ToJpeg(bitmap));
    }

    static SyncStatus Uploading => new()
    {
        State = SyncState.Uploading,
        Counts = new StatusCounts(45, 4113, 0, 2, 0),
        RunTotal = 45,
        RunDone = 17,
        BytesPerSecond = 8.4 * 1024 * 1024,
        Remaining = TimeSpan.FromMinutes(134),
        Active =
        [
            new ActiveUpload(Path.Combine(_root, "Phone Camera", "VID_20261011_180233.mp4"), 184_000_000, 121_000_000),
            new ActiveUpload(Path.Combine(_root, "Phone Camera", "PXL_20261011_174512.jpg"), 4_200_000, 1_100_000),
        ],
    };

    static SyncStatus Synced => new()
    {
        State = SyncState.Idle,
        Counts = new StatusCounts(0, 4113, 0, 0, 0),
        LastSync = DateTimeOffset.Now.AddMinutes(-3),
    };

    public static void Generate(string outDir)
    {
        // Headless has no graphics card: videos are drawn into memory by mpv.
        ImmichSplash.App.Program.GpuRequested = false;
        AppServices.Initialize();
        Seed();

        // Talk to a pretend server so the library, avatar and storage show real-looking content.
        var demo = new DemoServer();
        demo.Prepare();
        MapTiles.Handler = demo;
        AppServices.Account.UseClient(new ImmichClient(new Uri("https://photos.example.com/api/"), new AuthCredential(AuthKind.ApiKey, "demo"), demo));
        Pump(TimeSpan.FromSeconds(1));

        foreach (var (variant, suffix) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            if (Theme.Length > 0 && Theme != suffix)
                continue;
            Application.Current!.RequestedThemeVariant = variant;

            // Onboarding: server, sign in, folders
            ImmichSplash.Core.Services.LocalServerFinder.Override = _ => Task.FromResult<IReadOnlyList<Uri>>([new Uri("http://192.168.1.20:2283/")]);
            var main = new MainWindowViewModel();
            main.ShowOnboarding(resumeSignIn: false);
            var onboarding = (OnboardingViewModel)main.Content;
            Shot(main, outDir, $"01-welcome-{suffix}");

            onboarding.ServerHost = "photos.example.com";
            onboarding.ServerVersion = "v3.3.0";
            onboarding.OffersSso = true;
            onboarding.SsoButtonText = "Sign in with Authentik";
            onboarding.Step = 1;
            Shot(main, outDir, $"02-sign-in-{suffix}", height: 820);
            onboarding.IsWaitingForBrowser = true;
            Shot(main, outDir, $"02b-sso-waiting-{suffix}");
            onboarding.IsWaitingForBrowser = false;

            onboarding.Step = 2;
            onboarding.FolderChoices.Add(new FolderChoiceViewModel(Path.Combine(_root, "Pictures"), MaterialIconKind.ImageMultipleOutline) { IsSelected = true, Detail = "2,814 photos and videos" });
            onboarding.FolderChoices.Add(new FolderChoiceViewModel(Path.Combine(_root, "Videos"), MaterialIconKind.VideoOutline) { IsSelected = true, Detail = "100 photos and videos" });
            onboarding.FolderChoices.Add(new FolderChoiceViewModel(Path.Combine(_root, "Phone Camera"), MaterialIconKind.FolderOutline) { IsSelected = false, Detail = "1,246 photos and videos" });
            Shot(main, outDir, $"03-folders-{suffix}", height: 900);

            // Main app
            main.ShowShell();
            var shell = (ShellViewModel)main.Content;
            Pump(TimeSpan.FromSeconds(1));
            var overview = (OverviewViewModel)Page(shell, ShellPage.Overview);
            overview.Apply(Uploading);
            Shot(main, outDir, $"10-overview-uploading-{suffix}", height: 860);
            overview.Apply(Synced);
            Shot(main, outDir, $"11-overview-synced-{suffix}", height: 860);

            // Search: suggestions for people, places and albums as you type, and history.
            if (suffix == "light")
            {
                shell.OnSearchFocused();
                Pump(TimeSpan.FromSeconds(1));
                shell.SearchText = "Li";
                Pump(TimeSpan.FromMilliseconds(300));
                Console.WriteLine($"Search 'Li' suggests: {string.Join(" | ", shell.Suggestions.Select(x => $"{x.Title} ({x.Subtitle})"))}");
                Shot(main, outDir, "13-search-suggestions", height: 760, settle: TimeSpan.FromSeconds(0.5));
                shell.ChooseCommand.Execute(shell.Suggestions.First(x => x.Subtitle == "Lisbon, Portugal" || x.Title == "Lisbon"));
                Pump(TimeSpan.FromSeconds(1));
                shell.SearchText = "";
                shell.OnSearchFocused();
                Console.WriteLine($"Search history: {string.Join(" | ", shell.Suggestions.Select(x => $"{x.Title} ({x.Subtitle})"))}");
                shell.CloseSuggestions();
                ((PhotosViewModel)Page(shell, ShellPage.Photos)).BackCommand.Execute(null); // back to the timeline
                Pump(TimeSpan.FromSeconds(1));
            }
            shell.Navigate(ShellPage.Photos);
            Shot(main, outDir, $"15-photos-{suffix}", height: 860, settle: TimeSpan.FromSeconds(3));
            if (suffix == "light")
            {
                // Recent days are grouped by day: pretend today is the demo's newest day.
                var timeline = (PhotosViewModel)Page(shell, ShellPage.Photos);
                PhotosViewModel.Today = () => new DateTime(2026, 9, 28, 12, 0, 0);
                timeline.RefreshCommand.Execute(null);
                Pump(TimeSpan.FromSeconds(2));
                var headers = timeline.Rows.OfType<MonthHeaderViewModel>().Select(h => h.Title).Take(10).ToList();
                Console.WriteLine($"Recent days: {string.Join(" | ", headers)}");
                Shot(main, outDir, "15d-photos-recent-days", height: 860, settle: TimeSpan.FromSeconds(2));
                // (The demo has a later month first, and no photos dated the 27th, so no "Yesterday".)
                var recent = headers.SkipWhile(h => h == "October").ToList();
                if (recent.Count < 4 || recent[0] != "Today" || recent[1] != "Saturday" || !recent.Contains("Mon, Sep 21"))
                    throw new InvalidOperationException("Recent days aren't grouped by day");
                // Select all: one photo, then everything shown.
                var firstRow = timeline.Rows.OfType<AssetRowViewModel>().First();
                firstRow.Cells[0].ToggleSelectCommand.Execute(null);
                timeline.SelectAllCommand.Execute(null);
                var waitAll = DateTime.UtcNow;
                while (timeline.IsSelectingAll && DateTime.UtcNow - waitAll < TimeSpan.FromSeconds(20))
                    Pump(TimeSpan.FromMilliseconds(200));
                Pump(TimeSpan.FromMilliseconds(300));
                Shot(main, outDir, "15e-photos-select-all", height: 860, settle: TimeSpan.FromSeconds(1));
                var everything = timeline.Rows.OfType<AssetRowViewModel>().Select(r => r.Bucket).Distinct()
                    .Sum(b => b.GetAssetsAsync(CancellationToken.None).Result.Count(a => !a.IsLocalOnly)); // all loaded by now
                var shownUnticked = timeline.Rows.OfType<AssetRowViewModel>().SelectMany(r => r.Cells).Count(c => c.Asset is not null && c.CanSelect && !c.IsSelected);
                Console.WriteLine($"Select all: {timeline.SelectionCount} of {everything} backed-up photos; shown but not ticked: {shownUnticked}");
                if (timeline.SelectionCount != everything || shownUnticked != 0)
                    throw new InvalidOperationException("Select all didn't select everything");
                timeline.ClearSelectionCommand.Execute(null);
                PhotosViewModel.Today = () => DateTime.Now;
                timeline.RefreshCommand.Execute(null);
                Pump(TimeSpan.FromSeconds(2));
            }
            // Hover the date rail between labels (where hovering used to do nothing).
            Shot(main, outDir, $"15b-photos-rail-hover-{suffix}", height: 860, settle: TimeSpan.FromSeconds(1),
                interact: w => w.MouseMove(new Avalonia.Point(1060 - 40, 520)));
            // Click near the bottom of the rail: jumps to the oldest months.
            Shot(main, outDir, $"15c-photos-rail-jump-{suffix}", height: 860, settle: TimeSpan.FromSeconds(1), interact: w =>
            {
                var at = new Avalonia.Point(1060 - 40, 800);
                w.MouseMove(at);
                w.MouseDown(at, Avalonia.Input.MouseButton.Left);
                w.MouseUp(at, Avalonia.Input.MouseButton.Left);
                Pump(TimeSpan.FromSeconds(1.5));
                w.MouseMove(new Avalonia.Point(700, 500));
            });
            // Keyboard: a focused tile shows a ring; every button has a name a screen reader can read.
            Shot(main, outDir, $"15f-keyboard-focus-{suffix}", height: 860, settle: TimeSpan.FromSeconds(1), interact: w =>
            {
                var tile = w.GetVisualDescendants().OfType<Avalonia.Controls.Button>().First(b => b.Classes.Contains("tile") && b.IsEffectivelyVisible);
                tile.Focus(Avalonia.Input.NavigationMethod.Tab);
            });
            var photos = (PhotosViewModel)Page(shell, ShellPage.Photos);
            var cell = photos.Rows.OfType<AssetRowViewModel>().SelectMany(row => row.Cells).First(c => c.Asset is not null && c.Index == 4);
            photos.OpenViewer(cell);
            Shot(main, outDir, $"16-viewer-{suffix}", height: 760, settle: TimeSpan.FromSeconds(2));
            photos.Viewer?.ToggleInfoCommand.Execute(null);
            Shot(main, outDir, $"16b-viewer-info-{suffix}", height: 760, settle: TimeSpan.FromSeconds(1));
            photos.Viewer?.ToggleInfoCommand.Execute(null);
            photos.Viewer?.CloseCommand.Execute(null);
            // A video plays right in the viewer (streamed from the server in pieces).
            if (Environment.GetEnvironmentVariable("IMMICH_DEMO_VIDEO") is not null && suffix == "dark")
            {
                var videoCell = photos.Rows.OfType<AssetRowViewModel>().SelectMany(row => row.Cells).First(c => c.Asset is { IsImage: false, IsLocalOnly: false });
                photos.OpenViewer(videoCell);
                Console.WriteLine($"Video opening: {photos.Viewer?.VideoLoadingText}");
                Shot(main, outDir, $"16d0-viewer-video-loading-{suffix}", height: 760, settle: TimeSpan.FromSeconds(0.2));
                Shot(main, outDir, $"16d-viewer-video-{suffix}", height: 760, settle: TimeSpan.FromSeconds(3));
                var viewer = photos.Viewer!;
                var frames = 0;
                viewer.VideoFrameUpdated += () => frames++;
                Pump(TimeSpan.FromSeconds(1));
                if (viewer.VideoFrame is { } vf)
                    unsafe
                    {
                        using var fb = vf.Lock();
                        var px = (uint*)((byte*)fb.Address + fb.RowBytes * (fb.Size.Height / 2)) + fb.Size.Width / 3;
                        Console.WriteLine($"Video frame: {frames} repaints in 1s; a pixel = {*px:X8}");
                    }
                Console.WriteLine($"Video: loading={viewer.VideoLoadingText ?? "done"} started={viewer.IsVideoStarted} playing={viewer.IsVideoPlaying} frame={viewer.VideoFrame?.PixelSize} time={viewer.VideoTimeText} error={viewer.VideoError} requests={demo.VideoRequests}");
                viewer.VideoPosition = 0.75; // seek
                Pump(TimeSpan.FromSeconds(1.5));
                Console.WriteLine($"Video after seek: time={viewer.VideoTimeText} position={viewer.VideoPosition:0.00}");
                viewer.PlayPauseCommand.Execute(null);
                Shot(main, outDir, $"16e-viewer-video-paused-{suffix}", height: 760, settle: TimeSpan.FromSeconds(1));
                Console.WriteLine($"Video paused: playing={viewer.IsVideoPlaying}");
                viewer.CloseCommand.Execute(null);
            }
            // A photo on this PC that isn't backed up yet.
            var local = photos.Rows.OfType<AssetRowViewModel>().SelectMany(row => row.Cells).First(c => c.Asset is { IsLocalOnly: true, IsImage: true });
            photos.OpenViewer(local);
            Shot(main, outDir, $"16c-viewer-local-{suffix}", height: 760, settle: TimeSpan.FromSeconds(2));
            photos.Viewer?.CloseCommand.Execute(null);

            // The privacy check: runs the real on-device models over the demo library (on the CPU here),
            // then shows what it suggests for the locked folder.
            if (suffix == "light" && Environment.GetEnvironmentVariable("IMMICH_SCREENS_QUICK") is null)
            {
                AppServices.Settings.Update(s => { s.PrivacyScanEnabled = true; s.PrivacyScanUseGpu = false; });
                // Turning it off mid-check says "Off" and stays that way (a photo finishing must not flip it back).
                var waitStart = DateTime.UtcNow;
                while (AppServices.PrivacyScan.Status.State != ImmichSplash.Core.Privacy.PrivacyScanState.Scanning && DateTime.UtcNow - waitStart < TimeSpan.FromSeconds(40))
                    Pump(TimeSpan.FromMilliseconds(500));
                Pump(TimeSpan.FromSeconds(2));
                AppServices.Settings.Update(s => s.PrivacyScanEnabled = false);
                Pump(TimeSpan.FromSeconds(4));
                Console.WriteLine($"Privacy check turned off mid-scan: {AppServices.PrivacyScan.Status.State} after {AppServices.PrivacyScan.Status.Checked} checked");
                if (AppServices.PrivacyScan.Status.State != ImmichSplash.Core.Privacy.PrivacyScanState.Off)
                    throw new InvalidOperationException("Turning the privacy check off didn't show it as off");
                // The full check runs as a one-time check: it stops when done and says so.
                AppServices.PrivacyScan.ScanOnce();
                var started = DateTime.UtcNow;
                while (AppServices.PrivacyScan.Status.State is not (ImmichSplash.Core.Privacy.PrivacyScanState.Done or ImmichSplash.Core.Privacy.PrivacyScanState.Error)
                       && DateTime.UtcNow - started < TimeSpan.FromMinutes(6))
                    Pump(TimeSpan.FromSeconds(1));
                var scan = AppServices.PrivacyScan.Status;
                AppServices.Settings.Update(s => s.ThumbnailSize = s.ThumbnailSize); // any settings change must leave a finished one-time check alone
                Pump(TimeSpan.FromMilliseconds(300));
                Console.WriteLine($"One-time check: {scan.State}, one-time {scan.OneTime}; after a settings change: {AppServices.PrivacyScan.Status.State}");
                if (!scan.OneTime || AppServices.PrivacyScan.Status is not { State: ImmichSplash.Core.Privacy.PrivacyScanState.Done, OneTime: true })
                    throw new InvalidOperationException("The one-time check didn't finish as one");
                var flagged = AppServices.Database.GetPrivacySuggestions();
                Console.WriteLine($"Privacy check: {scan.State} {scan.Checked}/{scan.Total} in {(DateTime.UtcNow - started).TotalSeconds:0}s; flagged: {string.Join(", ", flagged.Select(f => $"{f.AssetId}={(ImmichSplash.Core.Privacy.SensitiveKind)f.Kinds}"))} {scan.Detail}");
            }
            shell.Navigate(ShellPage.Utilities);
            var privacyPage = (PhotosViewModel)Page(shell, ShellPage.Utilities);
            privacyPage.OpenPrivacyCommand.Execute(null);
            Shot(main, outDir, $"19p-privacy-{suffix}", height: 860, settle: TimeSpan.FromSeconds(2));
            if (suffix == "light" && privacyPage.Privacy is { } review && Environment.GetEnvironmentVariable("IMMICH_SCREENS_QUICK") is null)
            {
                review.ShowDetails = true;
                Shot(main, outDir, "19p3-privacy-how-it-works", height: 2100, settle: TimeSpan.FromSeconds(1));
                review.ShowDetails = false;
                review.ShowPictures = true;
                Shot(main, outDir, "19p2-privacy-shown", height: 860, settle: TimeSpan.FromSeconds(0.5));
                review.ShowDetails = true;
                Shot(main, outDir, "19p4-privacy-narrow", width: 760, height: 700, settle: TimeSpan.FromSeconds(1));
                review.ShowDetails = false;
                // The "How sure" slider: at 90% only the card (95%) stays; the prescription (80%) comes back at 0.
                foreach (var step in new[] { 30.0, 55, 75, 90 })
                {
                    review.MinConfidence = step;
                    Pump(TimeSpan.FromMilliseconds(20));
                }
                Pump(TimeSpan.FromMilliseconds(300));
                var atNinety = review.Items.Count;
                var cardTile = review.Items.FirstOrDefault();
                review.MinConfidence = 0;
                Pump(TimeSpan.FromMilliseconds(300));
                Console.WriteLine($"Privacy filter: {atNinety} at 90%, {review.Items.Count} at any; card tile kept: {ReferenceEquals(cardTile, review.Items.FirstOrDefault())}");
                if (atNinety != 1 || review.Items.Count != 2 || !ReferenceEquals(cardTile, review.Items[0]))
                    throw new InvalidOperationException("The confidence filter didn't work");
                // Move the card to the locked folder, delete the prescription.
                review.Items.First(i => i.Kinds.HasFlag(ImmichSplash.Core.Privacy.SensitiveKind.PaymentCard)).IsSelected = true;
                review.MoveToLockedCommand.Execute(null);
                Pump(TimeSpan.FromMilliseconds(500));
                main.Dialog?.ConfirmCommand.Execute(null);
                Pump(TimeSpan.FromSeconds(1));
                review.DeleteCommand.Execute(null);
                Pump(TimeSpan.FromMilliseconds(500));
                main.Dialog?.ConfirmCommand.Execute(null);
                Pump(TimeSpan.FromSeconds(1));
                Console.WriteLine($"Privacy review: {review.Count} left; locked requests: {demo.LockedRequests}; trash requests: {demo.TrashRequests}; suggestions in database: {AppServices.Database.CountPrivacySuggestions()}");
                if (review.Count != 0 || demo.LockedRequests != 1 || demo.TrashRequests != 1)
                    throw new InvalidOperationException("Moving suggestions to the locked folder didn't work");
            }
            privacyPage.BackCommand.Execute(null);

            // Utilities: the list, the duplicates review and large files.
            shell.Navigate(ShellPage.Utilities);
            var utilities = (PhotosViewModel)Page(shell, ShellPage.Utilities);
            Shot(main, outDir, $"19-utilities-{suffix}", height: 760, settle: TimeSpan.FromSeconds(1.5));
            utilities.OpenDuplicatesCommand.Execute(null);
            Shot(main, outDir, $"19b-duplicates-{suffix}", height: 860, settle: TimeSpan.FromSeconds(2));
            utilities.BackCommand.Execute(null);
            utilities.OpenScreenCapturesCommand.Execute(null);
            Shot(main, outDir, $"19c2-screenshots-{suffix}", height: 760, settle: TimeSpan.FromSeconds(2));
            utilities.BackCommand.Execute(null);
            utilities.OpenLargeFilesCommand.Execute(null);
            Shot(main, outDir, $"19c-large-files-{suffix}", height: 760, settle: TimeSpan.FromSeconds(2));
            utilities.BackCommand.Execute(null);
            shell.Navigate(ShellPage.Photos);

            // Select a few photos, then share them.
            foreach (var c in photos.Rows.OfType<AssetRowViewModel>().SelectMany(row => row.Cells).Where(c => c.Asset is not null && c.CanSelect).Take(3))
                c.ToggleSelectCommand.Execute(null);
            Shot(main, outDir, $"17-photos-selected-{suffix}", height: 860, settle: TimeSpan.FromSeconds(1));
            photos.ShareSelectionCommand.Execute(null);
            Shot(main, outDir, $"18-share-{suffix}", height: 760);
            photos.Panel = null;
            photos.ClearSelectionCommand.Execute(null);

            // The sidebar lists the albums used last.
            AppServices.Settings.Update(s => s.RecentAlbumIds = ["a2", "a1", "a3"]);
            Pump(TimeSpan.FromSeconds(2));
            shell.Navigate(ShellPage.Albums);
            Shot(main, outDir, $"19a-albums-{suffix}", height: 760, settle: TimeSpan.FromSeconds(2));
            // "Create album" is a dialog over the window.
            ((PhotosViewModel)Page(shell, ShellPage.Albums)).CreateAlbumCommand.Execute(null);
            Shot(main, outDir, $"19a2-create-album-{suffix}", height: 760, settle: TimeSpan.FromMilliseconds(600));
            main.CloseOverlay(main.Overlay);
            // Who an album is shared with, and its links.
            ((PhotosViewModel)Page(shell, ShellPage.Albums)).ShareAlbumFromCard(new ImmichSplash.Core.Api.AlbumInfo("a1", "Summer in Lisbon"));
            Pump(TimeSpan.FromSeconds(2));
            Shot(main, outDir, $"19a4-share-manager-{suffix}", height: 760, settle: TimeSpan.FromMilliseconds(600));
            main.CloseOverlay(main.Overlay);
            // The editor: turn, flip and crop.
            ((PhotosViewModel)Page(shell, ShellPage.Photos)).OpenEditor(new ImmichSplash.Core.Api.TimelineAsset("202609-1", true, false, 1.5, null, null));
            Pump(TimeSpan.FromSeconds(3));
            if (main.Overlay is PhotoEditorViewModel editor)
            {
                editor.RotateRightCommand.Execute(null);
                editor.SelectAspectCommand.Execute(editor.Aspects[4]);
                Pump(TimeSpan.FromMilliseconds(600));
            }
            Shot(main, outDir, $"19a5-editor-{suffix}", height: 760, settle: TimeSpan.FromMilliseconds(400));
            main.CloseOverlay(main.Overlay);
            // Toasts: small messages over the page.
            var toastPage = (PhotosViewModel)Page(shell, ShellPage.Albums);
            toastPage.ShowNotice(Tr.T("Copied to clipboard"));
            toastPage.ShowNotice(Tr.T("Couldn't copy the picture. {0}", "No connection."));
            Shot(main, outDir, $"19a3-toasts-{suffix}", height: 760, settle: TimeSpan.FromMilliseconds(400));
            if (suffix == "light")
            {
                // An open album: its header has Download, Share and Delete.
                var openPage = (PhotosViewModel)Page(shell, ShellPage.Albums);
                openPage.Albums.Cast<LibraryCardViewModel>().First().OpenCommand.Execute(null);
                Pump(TimeSpan.FromSeconds(1.5));
                Shot(main, outDir, "19a1-album-open", height: 760, settle: TimeSpan.FromSeconds(1));
                openPage.BackCommand.Execute(null);
                Pump(TimeSpan.FromSeconds(1));

                // Deleting an album that a backed-up folder fills: warns, then stops the folder's album too.
                var albumsPage = (PhotosViewModel)Page(shell, ShellPage.Albums);
                var phone = albumsPage.Albums.First(a => a.Title == "Phone");
                phone.DeleteCommand.Execute(null);
                Pump(TimeSpan.FromMilliseconds(300));
                Shot(main, outDir, "19a2-delete-album-warning", height: 760);
                main.Dialog?.ConfirmCommand.Execute(null);
                Pump(TimeSpan.FromSeconds(1));
                var folderAlbum = AppServices.Settings.Current.Folders.First(f => f.Id == "camera").AlbumName;
                Console.WriteLine($"Album delete: server saw [{string.Join(", ", demo.DeletedAlbums)}]; folder album now '{folderAlbum}'; albums shown {albumsPage.Albums.Count}");
                if (demo.DeletedAlbums is not ["a5"] || folderAlbum is not null || albumsPage.Albums.Any(a => a.Title == "Phone"))
                    throw new InvalidOperationException("Deleting an album didn't work");
                var sharingNames = ((PhotosViewModel)Page(shell, ShellPage.Sharing)).SharedAlbums.Select(a => a.Title).ToList();
                Console.WriteLine($"Shared albums (v3 format): {string.Join(", ", sharingNames)}");
            }
            shell.Navigate(ShellPage.Explore);
            Shot(main, outDir, $"19b-explore-{suffix}", height: 860, settle: TimeSpan.FromSeconds(2));
            shell.Navigate(ShellPage.Sharing);
            Shot(main, outDir, $"19c-sharing-{suffix}", height: 860, settle: TimeSpan.FromSeconds(2));
            if (suffix == "light")
            {
                // Deleting a shared link: confirm, then it's gone from the list (checked, not just pictured).
                var sharing = (PhotosViewModel)Page(shell, ShellPage.Sharing);
                var before = sharing.SharedLinks.Count;
                sharing.SharedLinks[0].DeleteCommand.Execute(null);
                Pump(TimeSpan.FromMilliseconds(300));
                (main.Dialog as DialogViewModel)?.ConfirmCommand.Execute(null);
                Pump(TimeSpan.FromSeconds(1));
                Console.WriteLine($"Shared link delete: {before} -> {sharing.SharedLinks.Count} links; server saw {demo.Deleted.Count} delete(s): {string.Join(", ", demo.Deleted)}");
                if (sharing.SharedLinks.Count != before - 1 || demo.Deleted.Count != 1)
                    throw new InvalidOperationException("Deleting a shared link didn't work");
            }
            shell.Navigate(ShellPage.Map);
            Shot(main, outDir, $"19d-map-{suffix}", height: 760, settle: TimeSpan.FromSeconds(2));
            Shot(main, outDir, $"19d2-map-usa-{suffix}", height: 760, settle: TimeSpan.FromSeconds(1), interact: w =>
                Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w).OfType<ImmichSplash.App.Controls.MapView>().First().CenterOn(39, -96, 4.2));
            Shot(main, outDir, $"19d3-map-world-{suffix}", height: 760, settle: TimeSpan.FromSeconds(1), interact: w =>
                Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w).OfType<ImmichSplash.App.Controls.MapView>().First().CenterOn(18, 40, 2.4));
            {
                var mapPage = (PhotosViewModel)Page(shell, ShellPage.Map);
                ImmichSplash.App.Controls.MapView MapOf(MainWindow w) =>
                    Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w).OfType<ImmichSplash.App.Controls.MapView>().First();
                // Up close, single photos show as thumbnails.
                Shot(main, outDir, $"19d4-map-thumbnails-{suffix}", height: 760, settle: TimeSpan.FromSeconds(2), interact: w =>
                {
                    MapOf(w).CenterOn(30.27, -97.74, 12.2);
                    Pump(TimeSpan.FromSeconds(2));
                });
                mapPage.MapHeat = true;
                Shot(main, outDir, $"19d5-map-heat-{suffix}", height: 760, settle: TimeSpan.FromSeconds(1), interact: w => MapOf(w).CenterOn(39, -96, 4.2));
                mapPage.MapHeat = false;
                mapPage.ShowMapPhotos(mapPage.Markers.Take(60).ToList());
                Shot(main, outDir, $"19d6-map-panel-{suffix}", height: 760, settle: TimeSpan.FromSeconds(2), interact: w => MapOf(w).CenterOn(39, -96, 4.2));
                mapPage.CloseMapPanelCommand.Execute(null);
                mapPage.OpenMapSettingsCommand.Execute(null);
                Shot(main, outDir, $"19d7-map-settings-{suffix}", height: 860, settle: TimeSpan.FromSeconds(1));
                mapPage.Panel = null;
            }
            shell.Navigate(ShellPage.Trash);
            Shot(main, outDir, $"19f-trash-{suffix}", height: 760, settle: TimeSpan.FromSeconds(2));
            shell.Navigate(ShellPage.Locked);
            Shot(main, outDir, $"19g-locked-{suffix}", height: 760, settle: TimeSpan.FromSeconds(1));
            shell.SearchText = "beach";
            shell.SearchCommand.Execute(null);
            Shot(main, outDir, $"19e-search-{suffix}", height: 760, settle: TimeSpan.FromSeconds(2));
            {
                // Backing out of a search: the × closes the results; another page clears the box;
                // clicking the current page in the sidebar again goes back to its start.
                var searched = (PhotosViewModel)Page(shell, ShellPage.Photos);
                var opened = searched.Drill is not null && shell.HasSearchText && !shell.IsSuggestionsOpen;
                shell.ClearSearchCommand.Execute(null);
                Pump(TimeSpan.FromMilliseconds(300));
                var cleared = searched.Drill is null && shell.SearchText == "";
                shell.SearchText = "beach";
                shell.SearchCommand.Execute(null);
                Pump(TimeSpan.FromMilliseconds(300));
                searched.BackCommand.Execute(null);
                var backClears = shell.SearchText == "";
                shell.SearchText = "beach";
                shell.SearchCommand.Execute(null);
                Pump(TimeSpan.FromMilliseconds(300));
                shell.Navigate(ShellPage.Map);
                var leaveClears = shell.SearchText == "" && !shell.IsSuggestionsOpen;
                shell.Navigate(ShellPage.Photos);
                shell.ReturnToStart();
                var reclick = searched.Drill is null;
                Console.WriteLine($"Search back-out: opened {opened}, × {cleared}, Back clears box {backClears}, leaving clears box {leaveClears}, sidebar re-click {reclick}");
                if (!(opened && cleared && backClears && leaveClears && reclick))
                    throw new InvalidOperationException("Backing out of a search didn't work");
            }
            shell.Navigate(ShellPage.Photos);

            shell.Navigate(ShellPage.Folders);
            ((FoldersViewModel)Page(shell, ShellPage.Folders)).Folders[1].IsExpanded = true;
            Shot(main, outDir, $"20-folders-{suffix}", height: 900);

            shell.Navigate(ShellPage.Activity);
            Shot(main, outDir, $"30-activity-{suffix}", height: 860);

            AppServices.Settings.Update(s =>
            {
                s.WallpaperEnabled = true;
                s.AutoSwitchServer = true;
                s.LocalNetworkName = "Morgan Home";
                s.LocalServerUrl = "http://192.168.1.20:2283";
                s.ExternalServerUrls = ["https://photos.example.com", "https://immich.tail1234.ts.net"];
            });
            NetworkStatus.Override = new NetworkSnapshot("Morgan Home", false);
            shell.Navigate(ShellPage.Settings);
            Shot(main, outDir, $"40-settings-{suffix}", height: 2600, settle: TimeSpan.FromSeconds(1));
            AppServices.Settings.Update(s => { s.WallpaperEnabled = false; s.AutoSwitchServer = false; });

            // Profile picture: choose from the library, then crop.
            var photo = new ProfilePhotoViewModel(main);
            main.ShowOverlay(photo);
            Shot(main, outDir, $"42-profile-pick-{suffix}", height: 760, settle: TimeSpan.FromSeconds(3));
            var pick = photo.Library.Rows.OfType<AssetRowViewModel>().SelectMany(row => row.Cells).Where(c => c.Asset is { IsImage: true }).Skip(2).First();
            photo.Library.OpenViewer(pick);
            Pump(TimeSpan.FromSeconds(2));
            photo.Cropper?.ZoomAt(1.6, 200, 150);
            Shot(main, outDir, $"43-profile-crop-{suffix}", height: 760);
            main.CloseOverlay(photo);

            AppServices.Settings.Update(s => s.AppLock = AppLockMode.Password);
            main.Lock();
            Shot(main, outDir, $"50-locked-password-{suffix}");
            WindowsHello.FakePrompt = () => Task.FromResult(HelloResult.Canceled); // never pop up the real Windows Hello
            AppServices.Settings.Update(s => s.AppLock = AppLockMode.WindowsHello);
            main.ShowShell();
            main.Lock();
            Shot(main, outDir, $"51-locked-hello-{suffix}");
            AppServices.Settings.Update(s => s.AppLock = AppLockMode.Off);
            main.ShowShell(ShellPage.Settings);

            main.Dialog = new DialogViewModel("Stop backing up “Phone Camera”?",
                "Photos and videos already uploaded stay in Immich. New files in this folder won't be backed up anymore.",
                "Remove folder", "Cancel", true);
            Shot(main, outDir, $"41-dialog-{suffix}");
            main.Dialog = null;
            // Dragging photos over the window, then dropping a folder.
            main.IsDragOver = true;
            Shot(main, outDir, $"44-drag-hint-{suffix}");
            main.IsDragOver = false;
            var dropped = Directory.CreateDirectory(Path.Combine(_root, "Dropped")).FullName;
            foreach (var name in new[] { "IMG_2001.jpg", "IMG_2002.jpg", "VID_2003.mp4", "notes.txt" })
                File.WriteAllBytes(Path.Combine(dropped, name), [1, 2, 3]);
            main.OfferUpload([dropped]);
            Shot(main, outDir, $"45-drop-upload-{suffix}", settle: TimeSpan.FromSeconds(1));
            main.CloseOverlay(main.Overlay);
            // Asked once after first-time setup: how updates should work.
            main.ShowOverlay(new UpdatePreferencesViewModel(main));
            Shot(main, outDir, $"46-update-prefs-{suffix}");
            main.CloseOverlay(main.Overlay);
            main.Dispose();
        }
        // Settings opens on the first click (it isn't in the sidebar, which used to "unselect" it),
        // and switching the language rebuilds the window on the Settings page. Checked in a live window.
        {
            var liveMain = new MainWindowViewModel();
            liveMain.ShowShell();
            var live = new MainWindow { DataContext = liveMain, Width = 1060, Height = 760 };
            live.Show();
            Pump(TimeSpan.FromSeconds(1));
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var liveShell = (ShellViewModel)liveMain.Content;
                liveShell.Navigate(ShellPage.Photos);
                Pump(TimeSpan.FromMilliseconds(300));
                liveShell.OpenSettingsCommand.Execute(null);
                Pump(TimeSpan.FromMilliseconds(500));
                if (liveShell.CurrentPage is not SettingsViewModel)
                    throw new InvalidOperationException($"Settings didn't open (attempt {attempt + 1}); showing {liveShell.CurrentPage?.GetType().Name ?? "nothing"}");
            }
            Tr.SetLanguage("de");
            liveMain.ShowShell(ShellPage.Settings); // what App.ApplyLanguage does
            Pump(TimeSpan.FromSeconds(1));
            var rebuilt = (ShellViewModel)liveMain.Content;
            Console.WriteLine($"Settings opens: ok; after switching language the page is {rebuilt.CurrentPage?.GetType().Name ?? "nothing"}");
            if (rebuilt.CurrentPage is not SettingsViewModel)
                throw new InvalidOperationException("Settings went blank after switching the language");
            if (Wanted("93-language-switched"))
                Save(live.CaptureRenderedFrame(), outDir, "93-language-switched");
            Tr.SetLanguage("en");
            live.DataContext = null;
            live.Close();
            liveMain.Dispose();
        }

        // A translated pass (German has the longest words) to check layouts.
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        Tr.SetLanguage("de");
        var german = new MainWindowViewModel();
        german.ShowShell();
        var germanShell = (ShellViewModel)german.Content;
        Pump(TimeSpan.FromSeconds(1));
        ((OverviewViewModel)Page(germanShell, ShellPage.Overview)).Apply(Uploading);
        Shot(german, outDir, "90-de-overview", height: 860);
        germanShell.Navigate(ShellPage.Photos);
        Shot(german, outDir, "91-de-photos", height: 760, settle: TimeSpan.FromSeconds(2));
        germanShell.Navigate(ShellPage.Settings);
        Shot(german, outDir, "92-de-settings", height: 2300, settle: TimeSpan.FromSeconds(1));
        german.Dispose();
        Tr.SetLanguage("en");

        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        Console.WriteLine($"Screenshots written to {Path.GetFullPath(outDir)}");
    }

    static ViewModelBase Page(ShellViewModel shell, ShellPage page) => shell.NavItems.First(n => n.Page == page).ViewModel;

    /// <summary>Runs the UI loop for a while so async loads (thumbnails, avatar) can finish.</summary>
    static void Pump(TimeSpan duration)
    {
        var end = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(25);
        }
    }

    /// <summary>Lists visible buttons a screen reader would announce without a name.</summary>
    static void ReportUnnamedButtons(Avalonia.Controls.Window window, string page)
    {
        var unnamed = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .Where(b => b.IsEffectivelyVisible && b.TemplatedParent is null)
            .Where(b => Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(b).GetName() is not { Length: > 0 } name
                || name.StartsWith("Avalonia.", StringComparison.Ordinal) || name.StartsWith("Material.", StringComparison.Ordinal))
            .Select(b => $"[{string.Join('.', b.Classes)}] {b.Content?.GetType().Name} cmd={(b.Command as object)?.GetType().Name} in {b.FindAncestorOfType<Avalonia.Controls.UserControl>()?.GetType().Name}")
            .ToList();
        if (unnamed.Count == 0)
            return;
        Console.WriteLine($"Unnamed buttons on {page}: {unnamed.Count}");
        foreach (var group in unnamed.GroupBy(u => u).OrderByDescending(g => g.Count()).Take(15))
            Console.WriteLine($"  {group.Count()} x {group.Key}");
    }

    static void Shot(MainWindowViewModel vm, string dir, string name, int width = 1060, int height = 720, TimeSpan? settle = null,
        Action<MainWindow>? interact = null)
    {
        var window = new MainWindow { DataContext = vm, Width = width, Height = height };
        window.Show();
        for (var i = 0; i < 30; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        if (settle is { } wait)
            Pump(wait);
        if (interact is not null)
        {
            interact(window);
            Pump(TimeSpan.FromMilliseconds(300));
        }
        var frame = window.CaptureRenderedFrame();
        // Unwanted screens are still shown (later screens depend on it), just not saved.
        if (Wanted(name))
        {
            Save(frame, dir, name);
            ReportUnnamedButtons(window, name);
        }
        window.DataContext = null;
        window.Close();
    }
}
