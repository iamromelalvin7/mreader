using System;
using System.IO;

namespace MDreader.Services;

public static class CrashLogService
{
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MDreader", "crash.log");

    public static void Write(string context, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:O}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never become a second startup failure.
        }
    }
}
