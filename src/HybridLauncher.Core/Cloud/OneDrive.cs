using Microsoft.Win32;

namespace HybridLauncher.Core.Cloud;

/// <summary>
/// OneDrive for cloud saves through Windows' own OneDrive app: the saves go into the OneDrive folder it keeps in sync, so there
/// is nothing to sign in to here and nothing to install on most PCs (OneDrive ships with Windows). The folder comes from the
/// app's own registry entries (a personal account first, then a work or school one) or its environment variables.
/// </summary>
public static class OneDrive
{
    /// <summary>The OneDrive folder on this PC, or null when the OneDrive app is not set up.</summary>
    public static string? Folder()
    {
        foreach (var account in new[] { "Personal", "Business1" })
            if (Registry.GetValue($@"HKEY_CURRENT_USER\Software\Microsoft\OneDrive\Accounts\{account}", "UserFolder", null) is string dir && Directory.Exists(dir))
                return dir;
        foreach (var name in new[] { "OneDriveConsumer", "OneDrive", "OneDriveCommercial" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } dir && Directory.Exists(dir))
                return dir;
        return null;
    }

    /// <summary>The e-mail address of the account the OneDrive app is signed in with, when it says.</summary>
    public static string? Email()
    {
        foreach (var account in new[] { "Personal", "Business1" })
            if (Registry.GetValue($@"HKEY_CURRENT_USER\Software\Microsoft\OneDrive\Accounts\{account}", "UserEmail", null) is string mail && mail.Length > 0)
                return mail;
        return null;
    }

    /// <summary>The OneDrive app, to open it when it is not set up yet.</summary>
    public static string? AppPath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "OneDrive", "OneDrive.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft OneDrive", "OneDrive.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft OneDrive", "OneDrive.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
