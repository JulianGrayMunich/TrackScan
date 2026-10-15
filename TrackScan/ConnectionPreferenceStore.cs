#region Local Connection Preferences
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrackScan;

internal sealed record ConnectionPreferences(string ConnectionString, int? ProjectId);

internal static class ConnectionPreferenceStore
{
    private static string SettingsPath => Path.Combine(path1: Environment.GetFolderPath(folder: Environment.SpecialFolder.LocalApplicationData), path2: "GNA Software", path3: "TrackScan", path4: "connection.dat");

    public static ConnectionPreferences Load()
    {
        if (!File.Exists(path: SettingsPath)) return new(ConnectionString: string.Empty, ProjectId: null);
        byte[] bytes = ProtectedData.Unprotect(encryptedData: File.ReadAllBytes(path: SettingsPath), optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<ConnectionPreferences>(json: Encoding.UTF8.GetString(bytes: bytes))
            ?? throw new InvalidOperationException(message: "Saved connection settings are empty.");
    }

    public static void Save(ConnectionPreferences preferences)
    {
        string path = SettingsPath;
        Directory.CreateDirectory(path: Path.GetDirectoryName(path: path) ?? throw new InvalidOperationException(message: "Missing settings directory."));
        byte[] bytes = ProtectedData.Protect(userData: Encoding.UTF8.GetBytes(s: JsonSerializer.Serialize(value: preferences)), optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
        string temporary = path + "." + Guid.NewGuid().ToString(format: "N") + ".tmp";
        try
        {
            File.WriteAllBytes(path: temporary, bytes: bytes);
            File.Move(sourceFileName: temporary, destFileName: path, overwrite: true);
        }
        finally
        {
            if (File.Exists(path: temporary)) File.Delete(path: temporary);
        }
    }
}
#endregion
