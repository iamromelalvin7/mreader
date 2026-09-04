using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace MDreader.Services;

public sealed record RecoverySnapshot(string? FilePath, string Text, DateTime SavedAtUtc);

public static class RecoveryService
{
    private static readonly string RecoveryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MDreader", "recovery.json");

    public static async Task SaveAsync(string? filePath, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RecoveryPath)!);
        string json = JsonSerializer.Serialize(new RecoverySnapshot(filePath, text, DateTime.UtcNow));
        await File.WriteAllTextAsync(RecoveryPath + ".tmp", json);
        File.Move(RecoveryPath + ".tmp", RecoveryPath, overwrite: true);
    }

    public static async Task<RecoverySnapshot?> LoadAsync()
    {
        try
        {
            if (!File.Exists(RecoveryPath)) return null;
            return JsonSerializer.Deserialize<RecoverySnapshot>(await File.ReadAllTextAsync(RecoveryPath));
        }
        catch
        {
            return null;
        }
    }

    public static void Clear()
    {
        if (File.Exists(RecoveryPath)) File.Delete(RecoveryPath);
        if (File.Exists(RecoveryPath + ".tmp")) File.Delete(RecoveryPath + ".tmp");
    }
}
