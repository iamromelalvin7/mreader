using System;
using Microsoft.Win32;

namespace MDreader.Services;

public static class FileAssociationService
{
    private const string ProgId = "MDreader.Document";
    private const string AppExeName = "MDreader.exe";

    public static void RegisterIfNeeded()
    {
        string? exePath = Environment.ProcessPath;
        if (exePath is null) return;

        string expectedCommand = $"\"{exePath}\" \"%1\"";

        using var key = Registry.CurrentUser.OpenSubKey(
            $@"SOFTWARE\Classes\Applications\{AppExeName}\shell\open\command");
        string? storedCommand = key?.GetValue(null) as string;

        if (storedCommand != expectedCommand)
            Register(exePath, expectedCommand);
    }

    private static void Register(string exePath, string openCommand)
    {
        // App registration — makes MDreader appear in every "Open with" dialog
        using (var appKey = Registry.CurrentUser.CreateSubKey(
            $@"SOFTWARE\Classes\Applications\{AppExeName}"))
            appKey.SetValue("FriendlyAppName", "MDreader");

        using (var cmdKey = Registry.CurrentUser.CreateSubKey(
            $@"SOFTWARE\Classes\Applications\{AppExeName}\shell\open\command"))
            cmdKey.SetValue(null, openCommand);

        // Supported types — Windows filters "Open with" suggestions by extension
        using (var typesKey = Registry.CurrentUser.CreateSubKey(
            $@"SOFTWARE\Classes\Applications\{AppExeName}\SupportedTypes"))
        {
            typesKey.SetValue(".md", "");
            typesKey.SetValue(".markdown", "");
            typesKey.SetValue(".txt", "");
        }

        // ProgId — the named document type MDreader owns
        using (var progIdKey = Registry.CurrentUser.CreateSubKey(
            $@"SOFTWARE\Classes\{ProgId}"))
            progIdKey.SetValue(null, "Markdown Document");

        using (var iconKey = Registry.CurrentUser.CreateSubKey(
            $@"SOFTWARE\Classes\{ProgId}\DefaultIcon"))
            iconKey.SetValue(null, $"{exePath},0");

        using (var progCmdKey = Registry.CurrentUser.CreateSubKey(
            $@"SOFTWARE\Classes\{ProgId}\shell\open\command"))
            progCmdKey.SetValue(null, openCommand);

        // Pin ProgId to .md so it shows in "Open with" for that extension specifically
        using (var mdKey = Registry.CurrentUser.CreateSubKey(
            @"SOFTWARE\Classes\.md\OpenWithProgids"))
            mdKey.SetValue(ProgId, "", RegistryValueKind.String);

        using (var mdxKey = Registry.CurrentUser.CreateSubKey(
            @"SOFTWARE\Classes\.markdown\OpenWithProgids"))
            mdxKey.SetValue(ProgId, "", RegistryValueKind.String);

        // App Paths — lets Windows locate MDreader.exe by name (Start menu, Run dialog, etc.)
        using (var appPathKey = Registry.CurrentUser.CreateSubKey(
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{AppExeName}"))
            appPathKey.SetValue(null, exePath);

        // Notify Explorer that file associations changed so it refreshes icons immediately
        NativeMethods.SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x0000, IntPtr.Zero, IntPtr.Zero);
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    internal static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
