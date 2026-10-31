using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HybridLauncher.Core.Accounts;

/// <summary>
/// The sign-in tokens of the launcher accounts (Steam refresh token, Epic and GOG OAuth tokens), one small JSON per store,
/// encrypted with Windows DPAPI for the current Windows user: another user, or a copy of the file on another PC, cannot read
/// them. Never a password: the launchers' sign-in pages and Steam's own authentication take those and hand back tokens.
/// </summary>
public static class AccountTokens
{
    static string Dir => AppInfo.Ensure(Path.Combine(AppInfo.DataRoot, "accounts"));

    static string FileOf(GameStore store) => FileOf(store.ToString().ToLowerInvariant());

    static string FileOf(string name) => Path.Combine(Dir, $"{name}.bin");

    static readonly byte[] Entropy = Encoding.UTF8.GetBytes(AppInfo.Name + " accounts");
    /// <summary>Sign-ins saved before a rename were encrypted under an old name: read once, then saved under the new one.</summary>
    static readonly byte[][] LegacyEntropies =
        [Encoding.UTF8.GetBytes(AppInfo.PreviousName + " accounts"), Encoding.UTF8.GetBytes(AppInfo.LegacyName + " launcher accounts")];

    public static Dictionary<string, string>? Read(GameStore store) => Read(store.ToString().ToLowerInvariant());

    /// <summary>Another account by name (the cloud saves' "mega"): the same encrypted file, its own name.</summary>
    public static Dictionary<string, string>? Read(string store)
    {
        var file = FileOf(store);
        if (!File.Exists(file)) return null;
        try
        {
            var data = File.ReadAllBytes(file);
            byte[] bytes;
            try { bytes = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser); }
            catch (CryptographicException)
            {
                bytes = Unprotect(data);
                var legacy = JsonSerializer.Deserialize(bytes, AccountJson.Default.DictionaryStringString);
                if (legacy is not null) Write(store, legacy);
                return legacy;
            }
            return JsonSerializer.Deserialize(bytes, AccountJson.Default.DictionaryStringString);
        }
        catch (Exception ex)
        {
            Log.Warn($"{store} sign-in could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>The data under the first old name that opens it (the last one's error when none does).</summary>
    static byte[] Unprotect(byte[] data)
    {
        for (int i = 0; ; i++)
            try { return ProtectedData.Unprotect(data, LegacyEntropies[i], DataProtectionScope.CurrentUser); }
            catch (CryptographicException) when (i < LegacyEntropies.Length - 1) { }
    }

    public static void Write(GameStore store, Dictionary<string, string> values) => Write(store.ToString().ToLowerInvariant(), values);

    public static void Write(string store, Dictionary<string, string> values)
    {
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(values, AccountJson.Default.DictionaryStringString), Entropy, DataProtectionScope.CurrentUser);
        var file = FileOf(store);
        var tmp = file + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, file, overwrite: true);
    }

    public static void Delete(GameStore store) => Delete(store.ToString().ToLowerInvariant());

    public static void Delete(string store)
    {
        try { File.Delete(FileOf(store)); } catch (Exception ex) { Log.Warn($"{store} sign-out: {ex.Message}"); }
    }

    public static bool Has(GameStore store) => File.Exists(FileOf(store));

    public static bool Has(string store) => File.Exists(FileOf(store));
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
partial class AccountJson : System.Text.Json.Serialization.JsonSerializerContext;
