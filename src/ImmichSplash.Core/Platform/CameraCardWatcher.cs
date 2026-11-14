namespace ImmichSplash.Core.Platform;

/// <summary>A memory card or USB drive with photos on it (a DCIM folder, as cameras and phones write).</summary>
public sealed record CameraCard(string Root, string Label, string PhotosFolder)
{
    /// <summary>Stays the same each time the same card is put in (for "don't ask for this card again").</summary>
    public string Key => $"{Label}|{new DriveInfo(Root) switch { var d when d.IsReady => d.TotalSize, _ => 0 }}";
}

/// <summary>
/// Notices camera cards and USB drives as they're plugged in, by looking at the drives every few seconds
/// (cheap, and the same on Windows and Linux). Cards already in when the app starts don't count.
/// </summary>
public sealed class CameraCardWatcher : IDisposable
{
    readonly Timer _timer;
    HashSet<string>? _known;

    public event Action<CameraCard>? CardInserted;

    public CameraCardWatcher(TimeSpan? every = null)
    {
        var interval = every ?? TimeSpan.FromSeconds(4);
        _timer = new Timer(_ => Check(), null, TimeSpan.Zero, interval);
    }

    void Check()
    {
        try
        {
            var now = Removable().ToDictionary(c => c.Root, StringComparer.OrdinalIgnoreCase);
            if (_known is null)
            {
                _known = [.. now.Keys];
                return;
            }
            foreach (var (root, card) in now)
                if (_known.Add(root))
                    CardInserted?.Invoke(card);
            _known.RemoveWhere(r => !now.ContainsKey(r));
        }
        catch (Exception ex)
        {
            Log.Info($"Checking for camera cards failed: {ex.Message}");
        }
    }

    /// <summary>Removable drives that have a DCIM folder.</summary>
    public static IEnumerable<CameraCard> Removable()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            string root;
            string? dcim;
            try
            {
                if (!drive.IsReady)
                    continue;
                root = drive.RootDirectory.FullName;
                var removable = OperatingSystem.IsWindows()
                    ? drive.DriveType == DriveType.Removable
                    : root.StartsWith("/media/", StringComparison.Ordinal) || root.StartsWith("/run/media/", StringComparison.Ordinal);
                if (!removable)
                    continue;
                dcim = Directory.EnumerateDirectories(root).FirstOrDefault(d => string.Equals(Path.GetFileName(d), "DCIM", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                continue;
            }
            if (dcim is null)
                continue;
            var label = "";
            try { label = drive.VolumeLabel; } catch { /* some file systems have none */ }
            if (string.IsNullOrWhiteSpace(label))
                label = OperatingSystem.IsWindows() ? root.TrimEnd('\\') : Path.GetFileName(root.TrimEnd('/'));
            yield return new CameraCard(root, label, dcim);
        }
    }

    public void Dispose() => _timer.Dispose();
}
