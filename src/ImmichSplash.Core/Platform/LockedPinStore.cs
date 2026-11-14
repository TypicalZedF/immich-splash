using System.Security.Cryptography;
using System.Text;

namespace ImmichSplash.Core.Platform;

/// <summary>
/// The locked folder's PIN, kept on this PC only when you tick "Remember on this PC", so moving photos into the
/// locked folder (or opening it) can be confirmed with Windows Hello instead of typing the PIN every time.
/// Windows: encrypted with DPAPI (only this Windows user can read it). Linux: a file only you can read (0600).
/// </summary>
public static class LockedPinStore
{
    static readonly byte[] Entropy = "ImmichSplash.LockedPin.v1"u8.ToArray();

    public static string FilePath => Path.Combine(AppPaths.DataDir, "locked-pin.bin");

    public static bool Exists(string? path = null) => File.Exists(path ?? FilePath);

    /// <summary>The remembered PIN, if it belongs to <paramref name="userId"/>.</summary>
    public static string? Load(string? userId, string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (!File.Exists(path))
                return null;
            var bytes = File.ReadAllBytes(path);
            if (OperatingSystem.IsWindows())
                bytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            var parts = Encoding.UTF8.GetString(bytes).Split('\n');
            return parts.Length == 2 && parts[0] == (userId ?? "") ? parts[1] : null;
        }
        catch (Exception ex)
        {
            Log.Warn("The remembered PIN could not be read", ex);
            return null;
        }
    }

    public static void Save(string? userId, string pin, string? path = null)
    {
        path ??= FilePath;
        var bytes = Encoding.UTF8.GetBytes($"{userId}\n{pin}");
        if (OperatingSystem.IsWindows())
            bytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    public static void Delete(string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not forget the remembered PIN", ex);
        }
    }
}
