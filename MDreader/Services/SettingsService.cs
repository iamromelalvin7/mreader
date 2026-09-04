using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MDreader.Services;

public sealed class AppSettings
{
    public List<string> RecentFiles { get; set; } = new();
    public string Theme { get; set; } = "System";
    public double EditorFontSize { get; set; } = 14;
    public bool WordWrap { get; set; } = true;
    public bool OutlineVisible { get; set; } = true;
    public bool FormattingVisible { get; set; } = true;
    public string? LastFilePath { get; set; }
}

public static class SettingsService
{
    private static readonly string AppFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MDreader");
    private static readonly string SettingsPath = Path.Combine(AppFolder, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly SemaphoreSlim SaveLock = new(1, 1);

    public static async Task<AppSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            string json = await File.ReadAllTextAsync(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static async Task SaveAsync(AppSettings settings)
    {
        await SaveLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(AppFolder);
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            string temporaryPath = SettingsPath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            SaveLock.Release();
        }
    }

    public static void AddRecentFile(AppSettings settings, string path)
    {
        settings.RecentFiles.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        settings.RecentFiles.Insert(0, path);
        settings.RecentFiles = settings.RecentFiles.Where(File.Exists).Take(10).ToList();
        settings.LastFilePath = path;
    }
}
