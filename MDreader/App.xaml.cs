using System;
using Microsoft.UI.Xaml;
using MDreader.Services;
using MDreader.Views;

namespace MDreader;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) => CrashLogService.Write("Unhandled UI exception", args.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Register with Windows so MDreader appears in "Open with" for .md files.
        // Runs on every launch but skips the registry writes when the path hasn't changed.
        FileAssociationService.RegisterIfNeeded();

        // Unpackaged Win32 launch (e.g. double-clicking a .md file via "Open with",
        // or the registry [Registry] "open" command) passes the file path as a plain
        // process command-line argument, not via LaunchActivatedEventArgs - that type
        // is populated for packaged/UWP-style activation kinds, which this app doesn't use.
        string[] cmdArgs = Environment.GetCommandLineArgs();
        string? initialFilePath = cmdArgs.Length > 1 && System.IO.File.Exists(cmdArgs[1]) ? cmdArgs[1] : null;

        try
        {
            _window = new MainWindow(initialFilePath);
            _window.Activate();
        }
        catch (Exception ex)
        {
            CrashLogService.Write("Application launch", ex);
            throw;
        }
    }
}
