namespace TeamsObsRecorder;

/// <summary>
/// Protects the OBS password in config.json. On Windows it is encrypted with DPAPI for the
/// current user, so only this Windows account on this PC can read it. On Linux the config file
/// and folder are readable by the current user only.
/// </summary>
public static class Secrets
{
    private const string Prefix = "dpapi:";

    public static bool CanProtect => OperatingSystem.IsWindows();

    public static string Protect(string secret)
    {
#if WINDOWS
        var data = System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(secret), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(data);
#else
        throw new PlatformNotSupportedException("Password encryption is only available on Windows");
#endif
    }

    /// <summary>Decrypts a value from <see cref="Protect"/>; returns null if it can't (other user or PC).</summary>
    public static string? Unprotect(string value)
    {
#if WINDOWS
        try
        {
            if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return null;
            var data = System.Security.Cryptography.ProtectedData.Unprotect(
                Convert.FromBase64String(value[Prefix.Length..]), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(data);
        }
        catch (Exception ex)
        {
            Log.Error("Could not decrypt the OBS password (config copied from another user or PC?); enter it again in Settings", ex);
            return null;
        }
#else
        return null;
#endif
    }

    /// <summary>On Linux/macOS: make a file or folder accessible to the current user only.</summary>
    public static void RestrictToUser(string path, bool directory = false)
    {
        if (OperatingSystem.IsWindows()) return; // %APPDATA% is already private to the user
        try
        {
            File.SetUnixFileMode(path, directory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not restrict permissions on {path}", ex);
        }
    }

    /// <summary>True when the host is this computer, so the unencrypted OBS connection never leaves it.</summary>
    public static bool IsLocalHost(string host)
    {
        var h = host.Trim().Trim('[', ']');
        if (h.Length == 0 || h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return System.Net.IPAddress.TryParse(h, out var ip) && System.Net.IPAddress.IsLoopback(ip);
    }
}
