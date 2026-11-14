using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ImmichSplash.Core;

namespace ImmichSplash.App.Services;

/// <summary>
/// What's useful in a bug report, with personal details taken out: versions, how the app is set up (counts, not
/// paths), and the latest warnings from the log with your server address, user name, email and folders removed.
/// </summary>
public static partial class Diagnostics
{
    public static string Collect()
    {
        var s = AppServices.Settings.Current;
        var text = new StringBuilder();
        text.AppendLine($"{AppInfo.Name} {AppInfo.Version}{(AppInfo.SourceCommit is { } c ? $" ({c[..Math.Min(7, c.Length)]})" : "")}");
        text.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}){(Core.Platform.Flatpak.IsRunning ? ", Flatpak" : "")}");
        text.AppendLine($"Immich server: {s.ServerVersion ?? "unknown"}");
        text.AppendLine($"Sign-in: {AppServices.Account.Client?.Credential?.Kind.ToString() ?? "signed out"}");
        text.AppendLine($"Language: {Tr.Current ?? "system"}, theme: {s.Theme}");
        text.AppendLine($"Folders: {s.Folders.Count}, with albums: {s.Folders.Count(f => !string.IsNullOrWhiteSpace(f.AlbumName))}");
        text.AppendLine($"Status: {AppServices.Engine.Status.State}");
        text.AppendLine();
        text.AppendLine("Recent warnings:");
        var warnings = RecentWarnings(25);
        text.AppendLine(warnings.Count == 0 ? "(none)" : string.Join(Environment.NewLine, warnings));
        return Scrub(text.ToString());
    }

    static List<string> RecentWarnings(int max)
    {
        try
        {
            var latest = new DirectoryInfo(AppPaths.LogsDir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (latest is null)
                return [];
            using var stream = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
                if (line.Contains("WRN", StringComparison.Ordinal) || line.Contains("ERR", StringComparison.Ordinal)
                    || line.Contains("[Warn", StringComparison.OrdinalIgnoreCase) || line.Contains("[Error", StringComparison.OrdinalIgnoreCase))
                    lines.Add(line.Length > 400 ? line[..400] + "…" : line);
            return lines.TakeLast(max).ToList();
        }
        catch (Exception ex)
        {
            return [$"(couldn't read the log: {ex.GetType().Name})"];
        }
    }

    /// <summary>Takes out the server address, account, home folder, backed-up folders and any email or web address.</summary>
    public static string Scrub(string text)
    {
        var s = AppServices.Settings.Current;
        void Replace(string? value, string with)
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Length > 2)
                text = text.Replace(value, with, StringComparison.OrdinalIgnoreCase);
        }
        foreach (var folder in s.Folders)
            Replace(folder.Path, "<folder>");
        Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<home>");
        Replace(s.ServerUrl, "<server>");
        Replace(s.WebUrl, "<server>");
        if (Uri.TryCreate(s.ServerUrl, UriKind.Absolute, out var server))
            Replace(server.Host, "<server>");
        Replace(s.UserEmail, "<email>");
        Replace(s.UserName, "<name>");
        Replace(s.UserId, "<user>");
        Replace(Environment.UserName, "<user>");
        Replace(Environment.MachineName, "<pc>");
        text = Email().Replace(text, "<email>");
        text = WebAddress().Replace(text, "<link>");
        return text;
    }

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+")]
    private static partial Regex Email();

    [GeneratedRegex(@"https?://[^\s""']+")]
    private static partial Regex WebAddress();

    /// <summary>A new bug report on GitHub with the versions filled in (nothing personal).</summary>
    public static string BugReportUrl()
    {
        var s = AppServices.Settings.Current;
        var os = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
        return $"{AppInfo.ProjectUrl}/issues/new?template=bug_report.yml"
            + $"&app-version={Uri.EscapeDataString(AppInfo.Version)}"
            + $"&os={Uri.EscapeDataString(os)}"
            + (s.ServerVersion is { } v ? $"&server-version={Uri.EscapeDataString(v)}" : "");
    }

    public static string FeedbackUrl() =>
        $"{AppInfo.ProjectUrl}/issues/new?template=feedback.yml&app-version={Uri.EscapeDataString(AppInfo.Version)}";
}
