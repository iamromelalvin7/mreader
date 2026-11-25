using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MDreader.Models;
using MDreader.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace MDreader.Views;

public sealed partial class MainWindow : Window
{
    private enum SaveDecision { Save, Discard, Cancel }

    /// <summary>Read = rendered preview only, Split = source + preview, Edit = source only.</summary>
    private enum ViewMode { Read, Split, Edit }

    private readonly DocumentState _document = new();
    private readonly DispatcherTimer _previewRenderTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _outlineTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _recoveryTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly string? _initialFilePath;
    private AppSettings _settings = new();
    private bool _suppressDirty;
    private bool _webViewReady;
    private bool _allowNextPreviewNavigation;
    private ViewMode _viewMode = ViewMode.Split;
    // Where Ctrl+E and the preview's corner button go back to when leaving Read mode.
    private ViewMode _lastSourceMode = ViewMode.Split;
    // Derived from _viewMode/_focusMode in UpdateAdaptivePanes; gates preview rendering work.
    private bool _previewVisible = true;
    private bool _focusMode;
    private bool _allowClose;
    private bool _closeDialogOpen;
    private bool _isDraggingSplitter;
    private double _splitterDragStartX;
    private double _splitterDragStartEditorWidth;
    private double _splitterDragStartPreviewWidth;
    private int _previewRenderVersion;
    // WinUI's TextBox normalizes every line break to a bare "\r" internally. Remember what the file
    // on disk used so Save writes it back unchanged instead of converting the file to CR-only endings.
    private string _lineEnding = "\n";
    private GridLength _lastEditorWidth = new(1, GridUnitType.Star);
    private GridLength _lastPreviewWidth = new(1, GridUnitType.Star);
    private double _previewScrollRatio;
    private IReadOnlyList<HeadingInfo> _headings = Array.Empty<HeadingInfo>();

    public MainWindow(string? initialFilePath = null)
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Closing += MainWindow_Closing;
        _document.DirtyChanged += (_, _) => UpdateTitle();
        _initialFilePath = initialFilePath;

        _previewRenderTimer.Tick += async (_, _) =>
        {
            _previewRenderTimer.Stop();
            await RenderPreviewAsync();
        };
        _outlineTimer.Tick += (_, _) =>
        {
            _outlineTimer.Stop();
            RefreshOutline();
        };
        _recoveryTimer.Tick += async (_, _) =>
        {
            if (_document.IsDirty)
            {
                try { await RecoveryService.SaveAsync(_document.CurrentFilePath, RawEditor.Text); }
                catch { /* Recovery is best-effort; normal Save still reports failures. */ }
            }
        };

        UpdateTitle();
        Activated += MainWindow_Activated;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        try
        {
            _settings = await SettingsService.LoadAsync();
            ApplySettings();
            _recoveryTimer.Start();

            if (_initialFilePath is not null)
                await LoadDocumentAsync(_initialFilePath);

            await InitializePreviewAsync();
            RefreshOutline();
            UpdateStatus();
            RawEditor.Focus(FocusState.Programmatic);

            if (_initialFilePath is null)
            {
                // ContentDialog needs an attached XamlRoot. Queue recovery after the
                // activation/layout pass instead of blocking window activation on Loaded.
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
                {
                    try { await OfferRecoveryAsync(); }
                    catch (Exception ex)
                    {
                        CrashLogService.Write("Recovery prompt", ex);
                        ShowError("Could not restore the recovery draft", ex);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            CrashLogService.Write("MainWindow activation", ex);
            ShowError("MDreader could not finish starting", ex);
        }
    }

    private async Task InitializePreviewAsync()
    {
        try
        {
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MDreader", "WebView2");
            // The WinRT projection used by WinUI exposes only the parameterless factory.
            // WebView2 honors this process-scoped override as its user-data-folder input.
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder, EnvironmentVariableTarget.Process);
            var environment = await CoreWebView2Environment.CreateAsync();
            await PreviewView.EnsureCoreWebView2Async(environment);
            PreviewView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            PreviewView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            PreviewView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            PreviewView.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            PreviewView.CoreWebView2.NavigationStarting += PreviewView_NavigationStarting;
            // Bundled KaTeX (JS, CSS, fonts) for offline math rendering. A fixed second host,
            // separate from the per-document "mdreader.local" mapping, so it never moves.
            string katexFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "katex");
            if (Directory.Exists(katexFolder))
                PreviewView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    MarkdownRenderService.MathAssetHost, katexFolder, CoreWebView2HostResourceAccessKind.Allow);
            // When the preview has keyboard focus, XAML KeyboardAccelerators never see the keys
            // (WebView2 is its own input surface), so forward Ctrl+E from inside the page.
            await PreviewView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "document.addEventListener('keydown', e => { if (e.ctrlKey && !e.shiftKey && !e.altKey && e.key.toLowerCase() === 'e') { e.preventDefault(); window.chrome.webview.postMessage('toggle-read-edit'); } });");
            PreviewView.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                if (args.TryGetWebMessageAsString() == "toggle-read-edit")
                    DispatcherQueue.TryEnqueue(ToggleReadEdit);
            };
            _webViewReady = true;
            PreviewUnavailableText.Visibility = Visibility.Collapsed;
            UpdatePreviewFolderMapping();
            await RenderPreviewAsync();
        }
        catch (Exception ex)
        {
            _webViewReady = false;
            PreviewUnavailableText.Text = $"Preview unavailable: {ex.Message}";
            PreviewUnavailableText.Visibility = Visibility.Visible;
            CrashLogService.Write("Preview initialization", ex);
            ShowError("Preview could not start", ex);
        }
    }

    private void UpdatePreviewFolderMapping()
    {
        if (!_webViewReady || _document.CurrentFilePath is null) return;
        string? folder = Path.GetDirectoryName(_document.CurrentFilePath);
        if (folder is null) return;
        PreviewView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "mdreader.local", folder, CoreWebView2HostResourceAccessKind.Allow);
    }

    private void PreviewView_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!args.IsUserInitiated)
        {
            // NavigateToString represents the page as a data URL. Large Markdown files
            // can produce URLs beyond System.Uri's parsing limit, so authorize our
            // one-shot internal navigation by its fixed prefix before parsing the URI.
            bool isExpectedPreview = _allowNextPreviewNavigation
                && args.Uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase);
            _allowNextPreviewNavigation = false;
            if (isExpectedPreview || args.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;

            args.Cancel = true;
            return;
        }

        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? uri))
        {
            args.Cancel = true;
            return;
        }

        if (uri.Host.Equals("mdreader.local", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(uri.Fragment))
        {
            args.Cancel = true;
            string id = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
            string encodedId = System.Text.Json.JsonSerializer.Serialize(id);
            _ = sender.ExecuteScriptAsync($"document.getElementById({encodedId})?.scrollIntoView({{behavior:'smooth'}})");
            return;
        }
        if (uri.Scheme == "about" && !string.IsNullOrEmpty(uri.Fragment)) return;
        args.Cancel = true;
        if (uri.Scheme is "https" or "http" or "mailto")
        {
            try { Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true }); }
            catch (Exception ex) { ShowError("Could not open link", ex); }
        }
        else
        {
            ShowMessage("Blocked link", $"MDreader blocked the unsafe link scheme “{uri.Scheme}”.", InfoBarSeverity.Warning);
        }
    }

    private async Task<bool> RenderPreviewAsync()
    {
        if (!_webViewReady || !_previewVisible) return false;

        // Markdig can take hundreds of milliseconds on large, table-heavy files. Keep
        // that work off the UI thread, and only navigate if this is still the newest
        // requested render when conversion finishes.
        int renderVersion = ++_previewRenderVersion;
        double scrollRatio = 0;
        try
        {
            string json = await PreviewView.CoreWebView2.ExecuteScriptAsync(
                "document.documentElement.scrollHeight <= innerHeight ? 0 : scrollY / (document.documentElement.scrollHeight - innerHeight)");
            if (double.TryParse(json.Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out double ratio))
                scrollRatio = Math.Clamp(ratio, 0, 1);
        }
        catch { /* The previous page may still be navigating. Start the new page at the top. */ }

        if (renderVersion != _previewRenderVersion || !_webViewReady || !_previewVisible) return false;

        string markdown = RawEditor.Text;
        bool darkTheme = IsDarkTheme();
        string? baseHref = _document.CurrentFilePath is null ? null : "https://mdreader.local/";

        try
        {
            string html = await Task.Run(() =>
                MarkdownRenderService.RenderToHtml(markdown, darkTheme, scrollRatio, baseHref));

            if (renderVersion != _previewRenderVersion || !_webViewReady || !_previewVisible) return false;

            _previewScrollRatio = scrollRatio;
            _allowNextPreviewNavigation = true;
            PreviewView.NavigateToString(html);
            return true;
        }
        catch (Exception ex)
        {
            if (renderVersion == _previewRenderVersion)
            {
                CrashLogService.Write("Preview render", ex);
                ShowError("Preview could not update", ex);
            }
            return false;
        }
    }

    private bool IsDarkTheme() => RootGrid.ActualTheme == ElementTheme.Dark;

    private void RawEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressDirty) _document.MarkDirty();
        WelcomePanel.Visibility = _viewMode != ViewMode.Read && string.IsNullOrEmpty(RawEditor.Text) ? Visibility.Visible : Visibility.Collapsed;
        QueueDocumentRefresh();
    }

    private void RawEditor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateStatus();
        SyncPreviewToCaret();
    }

    private async void SyncPreviewToCaret()
    {
        if (!_webViewReady || !_previewVisible || RawEditor.Text.Length == 0) return;
        _previewScrollRatio = Math.Clamp((double)RawEditor.SelectionStart / RawEditor.Text.Length, 0, 1);
        try
        {
            string ratio = _previewScrollRatio.ToString(CultureInfo.InvariantCulture);
            await PreviewView.CoreWebView2.ExecuteScriptAsync(
                $"window.scrollTo({{top:{ratio} * Math.max(0, document.documentElement.scrollHeight - innerHeight), behavior:'smooth'}})");
        }
        catch { /* Preview may be navigating between debounced renders. */ }
    }

    private void QueueDocumentRefresh()
    {
        // Invalidate any conversion that is currently running for older text. The
        // debounce below will render the newest snapshot after typing pauses.
        _previewRenderVersion++;
        UpdateStatus();
        _outlineTimer.Stop();
        _outlineTimer.Start();
        if (_previewVisible)
        {
            _previewRenderTimer.Stop();
            _previewRenderTimer.Start();
        }
    }

    private void RefreshOutline()
    {
        _headings = MarkdownDocumentService.GetHeadings(RawEditor.Text);
        OutlineList.ItemsSource = _headings;
    }

    private void OutlineList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OutlineList.SelectedItem is not HeadingInfo heading) return;
        if (_viewMode == ViewMode.Read)
        {
            // No caret to move in Read mode: scroll the rendered page to the matching heading instead.
            ScrollPreviewToHeading(OutlineList.SelectedIndex);
            return;
        }
        RawEditor.SelectionStart = Math.Min(heading.CharacterIndex, RawEditor.Text.Length);
        RawEditor.SelectionLength = 0;
        RawEditor.Focus(FocusState.Programmatic);
    }

    private async void ScrollPreviewToHeading(int headingIndex)
    {
        if (!_webViewReady || headingIndex < 0) return;
        // The outline lists ATX (#) headings in document order, which matches the order of
        // rendered <h1>-<h6> elements (it skips fenced code, as Markdig does).
        string script = $"(() => {{ const h = document.querySelectorAll('h1,h2,h3,h4,h5,h6')[{headingIndex}]; if (h) h.scrollIntoView({{behavior:'smooth', block:'start'}}); }})()";
        try { await PreviewView.CoreWebView2.ExecuteScriptAsync(script); }
        catch { /* Preview may be mid-navigation; the user can click again. */ }
    }

    private void UpdateTitle()
    {
        string dirtyMarker = _document.IsDirty ? "*" : "";
        Title = $"{_document.CurrentFileName}{dirtyMarker} - MDreader";
    }

    private void UpdateStatus()
    {
        string beforeCaret = RawEditor.Text[..Math.Min(RawEditor.SelectionStart, RawEditor.Text.Length)];
        int line = 1;
        for (int i = 0; i < beforeCaret.Length; i++)
        {
            if (beforeCaret[i] == '\r')
            {
                line++;
                if (i + 1 < beforeCaret.Length && beforeCaret[i + 1] == '\n') i++;
            }
            else if (beforeCaret[i] == '\n') line++;
        }
        int lastBreak = Math.Max(beforeCaret.LastIndexOf('\n'), beforeCaret.LastIndexOf('\r'));
        int column = beforeCaret.Length - lastBreak;
        PositionStatusText.Text = $"Ln {line}, Col {column}";
        int words = MarkdownDocumentService.CountWords(RawEditor.Text);
        WordCountStatusText.Text = $"{words:N0} {(words == 1 ? "word" : "words")}";
        // Report the document's on-disk line ending, not the TextBox's internal "\r" normalization.
        FileStatusText.Text = $"UTF-8 · {(_lineEnding == "\r\n" ? "CRLF" : _lineEnding == "\r" ? "CR" : "LF")}";
        ZoomStatusText.Text = $"{Math.Round(RawEditor.FontSize / 14d * 100):N0}%";
    }

    private async void NewButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await CanReplaceDocumentAsync()) return;
        _suppressDirty = true;
        RawEditor.Text = string.Empty;
        _suppressDirty = false;
        _lineEnding = "\n";
        _document.Reset();
        RecoveryService.Clear();
        UpdateTitle();
        QueueDocumentRefresh();
        if (_viewMode == ViewMode.Read) SetViewMode(_lastSourceMode, remember: false); // a new document is for writing
        RawEditor.Focus(FocusState.Programmatic);
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await CanReplaceDocumentAsync()) return;
        try
        {
            string? path = await FileService.PickOpenFilePathAsync(this);
            if (path is not null) await LoadDocumentAsync(path);
        }
        catch (Exception ex) { ShowError("Could not open file", ex); }
    }

    private async Task LoadDocumentAsync(string path)
    {
        try
        {
            string text = await FileService.ReadTextAsync(path);
            _lineEnding = MarkdownDocumentService.DetectLineEndings(text) switch { "CRLF" => "\r\n", "CR" => "\r", _ => "\n" };
            _suppressDirty = true;
            RawEditor.Text = text;
            _suppressDirty = false;
            _document.CurrentFilePath = Path.GetFullPath(path);
            _document.LastKnownWriteTimeUtc = FileService.GetLastWriteTimeUtc(path);
            _document.MarkClean();
            RecoveryService.Clear();
            SettingsService.AddRecentFile(_settings, _document.CurrentFilePath);
            await SettingsService.SaveAsync(_settings);
            UpdatePreviewFolderMapping();
            UpdateTitle();
            QueueDocumentRefresh();
            // Opening a file (double-click, Open, Recent, drag-drop) always shows it rendered first.
            SetViewMode(ViewMode.Read, remember: false);
        }
        catch (Exception ex) { ShowError("Could not open file", ex); }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e) => await SaveAsync();
    private async void SaveAsButton_Click(object sender, RoutedEventArgs e) => await SaveAsAsync();

    private async Task<bool> SaveAsync()
    {
        if (_document.CurrentFilePath is null) return await SaveAsAsync();
        try
        {
            DateTime? diskTime = FileService.GetLastWriteTimeUtc(_document.CurrentFilePath);
            if (_document.LastKnownWriteTimeUtc is not null && diskTime is not null && diskTime != _document.LastKnownWriteTimeUtc)
            {
                var dialog = new ContentDialog
                {
                    Title = "File changed outside MDreader",
                    Content = "Another app modified this file. Overwrite it, save your copy under a new name, or cancel?",
                    PrimaryButtonText = "Overwrite",
                    SecondaryButtonText = "Save As",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot
                };
                ContentDialogResult result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Secondary) return await SaveAsAsync();
                if (result != ContentDialogResult.Primary) return false;
            }

            await FileService.WriteTextAsync(_document.CurrentFilePath, TextForDisk());
            _document.LastKnownWriteTimeUtc = FileService.GetLastWriteTimeUtc(_document.CurrentFilePath);
            _document.MarkClean();
            RecoveryService.Clear();
            UpdateTitle();
            ShowMessage("Saved", _document.CurrentFileName, InfoBarSeverity.Success);
            return true;
        }
        catch (Exception ex)
        {
            ShowError("Could not save file", ex);
            return false;
        }
    }

    private async Task<bool> SaveAsAsync()
    {
        try
        {
            string suggested = _document.CurrentFileName == "Untitled" ? "Untitled.md" : _document.CurrentFileName;
            string? path = await FileService.PickSaveFilePathAsync(this, suggested);
            if (path is null) return false;
            await FileService.WriteTextAsync(path, TextForDisk());
            _document.CurrentFilePath = path;
            _document.LastKnownWriteTimeUtc = FileService.GetLastWriteTimeUtc(path);
            _document.MarkClean();
            RecoveryService.Clear();
            SettingsService.AddRecentFile(_settings, path);
            await SettingsService.SaveAsync(_settings);
            UpdatePreviewFolderMapping();
            UpdateTitle();
            ShowMessage("Saved", _document.CurrentFileName, InfoBarSeverity.Success);
            return true;
        }
        catch (Exception ex)
        {
            ShowError("Could not save file", ex);
            return false;
        }
    }

    /// <summary>The editor text with every line break converted back to the document's own line ending.</summary>
    private string TextForDisk() =>
        System.Text.RegularExpressions.Regex.Replace(RawEditor.Text, "\r\n|\r|\n", _lineEnding);

    private async Task<bool> CanReplaceDocumentAsync()
    {
        if (!_document.IsDirty) return true;
        SaveDecision decision = await ConfirmSaveChangesAsync();
        return decision switch
        {
            SaveDecision.Save => await SaveAsync(),
            SaveDecision.Discard => true,
            _ => false
        };
    }

    private async Task<SaveDecision> ConfirmSaveChangesAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Save changes?",
            Content = $"Save changes to {_document.CurrentFileName} before continuing?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don’t save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => SaveDecision.Save,
            ContentDialogResult.Secondary => SaveDecision.Discard,
            _ => SaveDecision.Cancel
        };
    }

    private async void MainWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_closeDialogOpen) return;
        _closeDialogOpen = true;
        bool canClose = await CanReplaceDocumentAsync();
        _closeDialogOpen = false;
        if (!canClose) return;
        _allowClose = true;
        _recoveryTimer.Stop();
        await PersistSettingsAsync();
        Close();
    }

    private async Task OfferRecoveryAsync()
    {
        RecoverySnapshot? snapshot = await RecoveryService.LoadAsync();
        if (snapshot is null || string.IsNullOrEmpty(snapshot.Text)) return;
        var dialog = new ContentDialog
        {
            Title = "Recover unsaved work?",
            Content = $"MDreader found an autosaved draft from {snapshot.SavedAtUtc.ToLocalTime():g}.",
            PrimaryButtonText = "Recover",
            SecondaryButtonText = "Discard",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _suppressDirty = true;
            RawEditor.Text = snapshot.Text;
            _suppressDirty = false;
            _document.CurrentFilePath = snapshot.FilePath;
            _document.LastKnownWriteTimeUtc = FileService.GetLastWriteTimeUtc(snapshot.FilePath);
            _document.MarkDirty();
            UpdateTitle();
        }
        else RecoveryService.Clear();
    }

    private void ReadModeToggle_Click(object sender, RoutedEventArgs e) => SetViewMode(ViewMode.Read);
    private void SplitModeToggle_Click(object sender, RoutedEventArgs e) => SetViewMode(ViewMode.Split);
    private void EditModeToggle_Click(object sender, RoutedEventArgs e) => SetViewMode(ViewMode.Edit);

    private void ToggleReadEdit_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleReadEdit();
    }

    private void ToggleReadEdit() => SetViewMode(_viewMode == ViewMode.Read ? _lastSourceMode : ViewMode.Read);

    private static ViewMode ParseSourceMode(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out ViewMode mode) && mode != ViewMode.Read ? mode : ViewMode.Split;

    /// <summary>
    /// The single place that changes what the workspace shows. Choosing Split or Edit remembers it as
    /// the preferred source view (<paramref name="remember"/>); programmatic switches pass false.
    /// </summary>
    private void SetViewMode(ViewMode mode, bool remember = true)
    {
        ViewMode previous = _viewMode;
        if (previous == ViewMode.Split && mode != ViewMode.Split)
        {
            // Keep the user's dragged split ratio for when they come back to Split.
            _lastEditorWidth = WorkspaceGrid.ColumnDefinitions[1].Width;
            _lastPreviewWidth = WorkspaceGrid.ColumnDefinitions[3].Width;
        }

        _viewMode = mode;
        if (mode != ViewMode.Read) _lastSourceMode = mode;

        // Toggle buttons flip their own IsChecked on click; re-assert so exactly one stays checked.
        ReadModeToggle.IsChecked = mode == ViewMode.Read;
        SplitModeToggle.IsChecked = mode == ViewMode.Split;
        EditModeToggle.IsChecked = mode == ViewMode.Edit;

        UpdateAdaptivePanes();

        if (mode != ViewMode.Read && previous == ViewMode.Read)
            RawEditor.Focus(FocusState.Programmatic);

        if (remember && mode != ViewMode.Read && _settings.SourceMode != mode.ToString())
        {
            _settings.SourceMode = mode.ToString();
            _ = PersistSettingsAsync();
        }
    }

    private void ColumnSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isDraggingSplitter = true;
        _splitterDragStartX = e.GetCurrentPoint(WorkspaceGrid).Position.X;
        _splitterDragStartEditorWidth = WorkspaceGrid.ColumnDefinitions[1].ActualWidth;
        _splitterDragStartPreviewWidth = WorkspaceGrid.ColumnDefinitions[3].ActualWidth;
        ColumnSplitter.CapturePointer(e.Pointer);
    }

    private void ColumnSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingSplitter) return;
        ResizePanes(e.GetCurrentPoint(WorkspaceGrid).Position.X - _splitterDragStartX);
    }

    private void ResizePanes(double delta)
    {
        const double minPaneWidth = 180;
        delta = Math.Max(delta, minPaneWidth - _splitterDragStartEditorWidth);
        delta = Math.Min(delta, _splitterDragStartPreviewWidth - minPaneWidth);
        WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(_splitterDragStartEditorWidth + delta, GridUnitType.Star);
        WorkspaceGrid.ColumnDefinitions[3].Width = new GridLength(_splitterDragStartPreviewWidth - delta, GridUnitType.Star);
        _lastEditorWidth = WorkspaceGrid.ColumnDefinitions[1].Width;
        _lastPreviewWidth = WorkspaceGrid.ColumnDefinitions[3].Width;
    }

    private void ColumnSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDraggingSplitter = false;
        ColumnSplitter.ReleasePointerCapture(e.Pointer);
    }

    private void ColumnSplitter_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
        _splitterDragStartEditorWidth = WorkspaceGrid.ColumnDefinitions[1].ActualWidth;
        _splitterDragStartPreviewWidth = WorkspaceGrid.ColumnDefinitions[3].ActualWidth;
        ResizePanes(e.Key == VirtualKey.Left ? -20 : 20);
        e.Handled = true;
    }

    private void ExpandPreviewButton_Click(object sender, RoutedEventArgs e) => ToggleReadEdit();

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateAdaptivePanes();

    /// <summary>Derives every pane's visibility and the column widths from the view mode, focus mode and window width.</summary>
    private void UpdateAdaptivePanes()
    {
        double width = RootGrid.ActualWidth;
        bool editorVisible = _viewMode != ViewMode.Read;
        // Focus mode is a distraction-free writing mode, so it drops the preview from Split.
        bool previewVisible = _viewMode == ViewMode.Read || (_viewMode == ViewMode.Split && !_focusMode);

        bool previewWasVisible = _previewVisible;
        _previewVisible = previewVisible;
        if (!previewVisible) _previewRenderVersion++; // cancel any in-flight render

        RawEditor.Visibility = editorVisible ? Visibility.Visible : Visibility.Collapsed;
        WelcomePanel.Visibility = editorVisible && string.IsNullOrEmpty(RawEditor.Text) ? Visibility.Visible : Visibility.Collapsed;
        PreviewPane.Visibility = previewVisible ? Visibility.Visible : Visibility.Collapsed;
        ColumnSplitter.Visibility = editorVisible && previewVisible ? Visibility.Visible : Visibility.Collapsed;
        OutlinePane.Visibility = !_focusMode && _settings.OutlineVisible && width >= 980 ? Visibility.Visible : Visibility.Collapsed;
        // Formatting buttons act on the source text, so they're only offered when the source is on screen.
        FormattingPane.Visibility = editorVisible && !_focusMode && _settings.FormattingVisible && width >= 780 ? Visibility.Visible : Visibility.Collapsed;

        WorkspaceGrid.ColumnDefinitions[1].Width = !editorVisible ? new GridLength(0)
            : previewVisible ? _lastEditorWidth : new GridLength(1, GridUnitType.Star);
        WorkspaceGrid.ColumnDefinitions[3].Width = !previewVisible ? new GridLength(0)
            : editorVisible ? _lastPreviewWidth : new GridLength(1, GridUnitType.Star);
        // The preview pane draws a left divider for Split; in Read mode it sits flush.
        PreviewPane.BorderThickness = new Thickness(editorVisible ? 1 : 0, 0, 0, 0);

        bool reading = _viewMode == ViewMode.Read;
        ExpandPreviewGlyph.Glyph = reading ? "\uE70F" : "\uE740"; // Edit pencil vs. full-screen
        ExpandPreviewLabel.Text = reading ? "Edit" : "Read";
        string tip = reading ? "Show Markdown source (Ctrl+E)" : "Read full width (Ctrl+E)";
        ToolTipService.SetToolTip(ExpandPreviewButton, tip);
        ModeStatusText.Text = reading ? "Reading · Ctrl+E to edit"
            : _viewMode == ViewMode.Split ? "Split · Ctrl+E to read"
            : "Editing · Ctrl+E to read";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExpandPreviewButton, tip);

        if (previewVisible && !previewWasVisible) _ = RenderPreviewAsync();
    }

    private void OutlineToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.OutlineVisible = OutlineToggle.IsChecked == true;
        UpdateAdaptivePanes();
        _ = PersistSettingsAsync();
    }

    private void FocusModeToggle_Click(object sender, RoutedEventArgs e)
    {
        _focusMode = FocusModeToggle.IsChecked == true;
        UpdateAdaptivePanes();
    }

    private void FindButton_Click(object sender, RoutedEventArgs e)
    {
        FindBar.Visibility = FindBar.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (FindBar.Visibility == Visibility.Visible)
        {
            FindTextBox.Focus(FocusState.Programmatic);
            FindTextBox.SelectAll();
        }
    }

    private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateFindStatus();
    private void FindNextButton_Click(object sender, RoutedEventArgs e) => FindNext(forward: true);
    private void FindPreviousButton_Click(object sender, RoutedEventArgs e) => FindNext(forward: false);

    private void FindNext(bool forward)
    {
        string needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) return;
        StringComparison comparison = MatchCaseCheckBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int index;
        if (forward)
        {
            int start = Math.Min(RawEditor.SelectionStart + RawEditor.SelectionLength, RawEditor.Text.Length);
            index = RawEditor.Text.IndexOf(needle, start, comparison);
            if (index < 0) index = RawEditor.Text.IndexOf(needle, comparison);
        }
        else
        {
            int start = Math.Max(0, RawEditor.SelectionStart - 1);
            index = RawEditor.Text.LastIndexOf(needle, start, comparison);
            if (index < 0) index = RawEditor.Text.LastIndexOf(needle, comparison);
        }
        if (index < 0) return;
        RawEditor.SelectionStart = index;
        RawEditor.SelectionLength = needle.Length;
        RawEditor.Focus(FocusState.Programmatic);
        UpdateFindStatus();
    }

    private void ReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        StringComparison comparison = MatchCaseCheckBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (string.Equals(RawEditor.SelectedText, FindTextBox.Text, comparison)) RawEditor.SelectedText = ReplaceTextBox.Text;
        FindNext(forward: true);
    }

    private void ReplaceAllButton_Click(object sender, RoutedEventArgs e)
    {
        string needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) return;
        StringComparison comparison = MatchCaseCheckBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int count = 0;
        int index = 0;
        var result = new System.Text.StringBuilder();
        while (index < RawEditor.Text.Length)
        {
            int match = RawEditor.Text.IndexOf(needle, index, comparison);
            if (match < 0) break;
            result.Append(RawEditor.Text, index, match - index).Append(ReplaceTextBox.Text);
            index = match + needle.Length;
            count++;
        }
        if (count == 0) return;
        result.Append(RawEditor.Text, index, RawEditor.Text.Length - index);
        RawEditor.SelectAll();
        RawEditor.SelectedText = result.ToString();
        FindStatusText.Text = $"Replaced {count:N0}";
    }

    private void UpdateFindStatus()
    {
        string needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) { FindStatusText.Text = string.Empty; return; }
        StringComparison comparison = MatchCaseCheckBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int count = 0;
        for (int index = 0; (index = RawEditor.Text.IndexOf(needle, index, comparison)) >= 0; index += needle.Length) count++;
        FindStatusText.Text = $"{count:N0} {(count == 1 ? "match" : "matches")}";
    }

    private void RecentFilesFlyout_Opening(object sender, object e)
    {
        RecentFilesFlyout.Items.Clear();
        foreach (string path in _settings.RecentFiles.Where(File.Exists))
        {
            var item = new MenuFlyoutItem { Text = Path.GetFileName(path) };
            ToolTipService.SetToolTip(item, path);
            item.Click += async (_, _) =>
            {
                if (await CanReplaceDocumentAsync()) await LoadDocumentAsync(path);
            };
            RecentFilesFlyout.Items.Add(item);
        }
        if (RecentFilesFlyout.Items.Count == 0)
            RecentFilesFlyout.Items.Add(new MenuFlyoutItem { Text = "No recent files", IsEnabled = false });
    }

    private async void ImageButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_document.CurrentFilePath is null && !await SaveAsAsync()) return;
            string? imagePath = await FileService.PickImageFilePathAsync(this);
            if (imagePath is null) return;
            await InsertImageFromPathAsync(imagePath);
        }
        catch (Exception ex) { ShowError("Could not insert image", ex); }
    }

    private async Task InsertImageFromPathAsync(string imagePath)
    {
        if (_document.CurrentFilePath is null) return;
        string relativePath = await FileService.CopyImageToDocumentAssetsAsync(imagePath, _document.CurrentFilePath);
        string alt = Path.GetFileNameWithoutExtension(imagePath).Replace('-', ' ');
        ApplyResult(new EditResult(
            RawEditor.Text.Insert(RawEditor.SelectionStart, $"![{alt}]({relativePath})"),
            RawEditor.SelectionStart + 2, alt.Length));
        UpdatePreviewFolderMapping();
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            IReadOnlyList<IStorageItem> items = await FileService.ToTask(e.DataView.GetStorageItemsAsync());
            if (items.FirstOrDefault() is not StorageFile file) return;
            string extension = Path.GetExtension(file.Path).ToLowerInvariant();
            if (extension is ".md" or ".markdown" or ".txt")
            {
                if (await CanReplaceDocumentAsync()) await LoadDocumentAsync(file.Path);
            }
            else if (extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg")
            {
                if (_document.CurrentFilePath is null && !await SaveAsAsync()) return;
                await InsertImageFromPathAsync(file.Path);
            }
        }
        catch (Exception ex) { ShowError("Could not process dropped file", ex); }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void ExportHtmlButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(_document.CurrentFileName) + ".html";
            string? path = await FileService.PickExportFilePathAsync(this, name, "HTML document", ".html");
            if (path is null) return;
            await FileService.WriteTextAsync(path, MarkdownRenderService.RenderToHtml(RawEditor.Text, false, standalone: true));
            ShowMessage("Export complete", path, InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowError("Could not export HTML", ex); }
    }

    private async void ExportPdfButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_webViewReady) throw new InvalidOperationException("Preview is not available.");
            string name = Path.GetFileNameWithoutExtension(_document.CurrentFileName) + ".pdf";
            string? path = await FileService.PickExportFilePathAsync(this, name, "PDF document", ".pdf");
            if (path is null) return;
            _previewRenderTimer.Stop();
            if (!await RenderPreviewAsync())
                throw new InvalidOperationException("The document changed while its preview was being prepared. Try exporting again.");
            await Task.Delay(250);
            CoreWebView2PrintSettings printSettings = PreviewView.CoreWebView2.Environment.CreatePrintSettings();
            bool printed = await PreviewView.CoreWebView2.PrintToPdfAsync(path, printSettings);
            if (!printed) throw new IOException("WebView2 could not create the PDF.");
            ShowMessage("Export complete", path, InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowError("Could not export PDF", ex); }
    }

    private void CopyHtmlButton_Click(object sender, RoutedEventArgs e)
    {
        string html = MarkdownRenderService.RenderToHtml(RawEditor.Text, IsDarkTheme(), standalone: true);
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(html);
        package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(html));
        Clipboard.SetContent(package);
        Clipboard.Flush();
        ShowMessage("Copied", "Rendered HTML is on the clipboard.", InfoBarSeverity.Success);
    }

    private void TableOfContentsButton_Click(object sender, RoutedEventArgs e)
    {
        string toc = MarkdownDocumentService.BuildTableOfContents(RawEditor.Text);
        if (string.IsNullOrWhiteSpace(toc))
        {
            ShowMessage("No headings", "Add headings before generating a table of contents.", InfoBarSeverity.Informational);
            return;
        }
        string insertion = "## Table of contents\n\n" + toc + "\n";
        ApplyResult(new EditResult(RawEditor.Text.Insert(RawEditor.SelectionStart, insertion), RawEditor.SelectionStart + insertion.Length, 0));
    }

    private void NewWindowButton_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = Environment.ProcessPath!, UseShellExecute = true }); }
        catch (Exception ex) { ShowError("Could not open a new window", ex); }
    }

    private void WordWrapToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.WordWrap = WordWrapToggle.IsChecked == true;
        RawEditor.TextWrapping = _settings.WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        RawEditor.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,
            _settings.WordWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        _ = PersistSettingsAsync();
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => ChangeZoom(1);
    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => ChangeZoom(-1);
    private void ChangeZoom(double amount)
    {
        RawEditor.FontSize = Math.Clamp(RawEditor.FontSize + amount, 10, 28);
        _settings.EditorFontSize = RawEditor.FontSize;
        UpdateStatus();
        _ = PersistSettingsAsync();
    }

    private void SystemThemeItem_Click(object sender, RoutedEventArgs e) => SetTheme("System");
    private void LightThemeItem_Click(object sender, RoutedEventArgs e) => SetTheme("Light");
    private void DarkThemeItem_Click(object sender, RoutedEventArgs e) => SetTheme("Dark");

    private void SetTheme(string theme)
    {
        _settings.Theme = theme;
        RootGrid.RequestedTheme = theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        SystemThemeItem.IsChecked = theme == "System";
        LightThemeItem.IsChecked = theme == "Light";
        DarkThemeItem.IsChecked = theme == "Dark";
        _ = RenderPreviewAsync();
        _ = PersistSettingsAsync();
    }

    private void ApplySettings()
    {
        RawEditor.FontSize = Math.Clamp(_settings.EditorFontSize, 10, 28);
        WordWrapToggle.IsChecked = _settings.WordWrap;
        RawEditor.TextWrapping = _settings.WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        OutlineToggle.IsChecked = _settings.OutlineVisible;
        SetTheme(_settings.Theme);

        // A file opened by double-click starts in Read (LoadDocumentAsync switches to it). A blank
        // start has nothing to read yet, so it opens in the user's preferred source view.
        _lastSourceMode = ParseSourceMode(_settings.SourceMode);
        SetViewMode(_initialFilePath is null ? _lastSourceMode : ViewMode.Read, remember: false);
    }

    private async Task PersistSettingsAsync()
    {
        try { await SettingsService.SaveAsync(_settings); }
        catch { /* Preferences are non-critical. */ }
    }

    private static bool IsModifierDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private async void RawEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsModifierDown(VirtualKey.Control);
        bool shift = IsModifierDown(VirtualKey.Shift);
        bool alt = IsModifierDown(VirtualKey.Menu);
        if (!ctrl) return;
        if (e.Key == VirtualKey.V && Clipboard.GetContent().Contains(StandardDataFormats.Bitmap))
        {
            e.Handled = true;
            try
            {
                if (_document.CurrentFilePath is null && !await SaveAsAsync()) return;
                string? relativePath = await FileService.SaveClipboardImageToDocumentAssetsAsync(_document.CurrentFilePath!);
                if (relativePath is not null)
                {
                    const string altText = "pasted image";
                    string markdown = $"![{altText}]({relativePath})";
                    ApplyResult(new EditResult(RawEditor.Text.Insert(RawEditor.SelectionStart, markdown), RawEditor.SelectionStart + 2, altText.Length));
                    UpdatePreviewFolderMapping();
                }
            }
            catch (Exception ex) { ShowError("Could not paste image", ex); }
            return;
        }
        switch (e.Key)
        {
            case VirtualKey.B when !shift && !alt: ApplyWrap("**", "**"); break;
            case VirtualKey.I when !shift && !alt: ApplyWrap("_", "_"); break;
            case VirtualKey.U when !shift && !alt: ApplyWrap("<u>", "</u>"); break;
            case VirtualKey.K when !shift && !alt: ApplyResult(MarkdownFormattingService.InsertLink(RawEditor.Text, RawEditor.SelectionStart, RawEditor.SelectionLength)); break;
            case VirtualKey.K when shift: ApplyWrap("```\n", "\n```"); break;
            case VirtualKey.Number1 when alt: ApplyHeader(1); break;
            case VirtualKey.Number2 when alt: ApplyHeader(2); break;
            case VirtualKey.Number3 when alt: ApplyHeader(3); break;
            case VirtualKey.L when shift && !alt: ApplyList(false); break;
            case (VirtualKey)0xC0 when !shift && !alt: ApplyWrap("`", "`"); break;
            default: return;
        }
        e.Handled = true;
    }

    private void BoldButton_Click(object sender, RoutedEventArgs e) => ApplyWrap("**", "**");
    private void ItalicButton_Click(object sender, RoutedEventArgs e) => ApplyWrap("_", "_");
    private void UnderlineButton_Click(object sender, RoutedEventArgs e) => ApplyWrap("<u>", "</u>");
    private void StrikethroughButton_Click(object sender, RoutedEventArgs e) => ApplyWrap("~~", "~~");
    private void InlineCodeButton_Click(object sender, RoutedEventArgs e) => ApplyWrap("`", "`");
    private void CodeBlockButton_Click(object sender, RoutedEventArgs e) => ApplyWrap("```\n", "\n```");
    private void H1Button_Click(object sender, RoutedEventArgs e) => ApplyHeader(1);
    private void H2Button_Click(object sender, RoutedEventArgs e) => ApplyHeader(2);
    private void H3Button_Click(object sender, RoutedEventArgs e) => ApplyHeader(3);
    private void BulletedListButton_Click(object sender, RoutedEventArgs e) => ApplyList(false);
    private void NumberedListButton_Click(object sender, RoutedEventArgs e) => ApplyList(true);
    private void TaskListButton_Click(object sender, RoutedEventArgs e) => ApplyResult(MarkdownFormattingService.ToggleTaskList(RawEditor.Text, RawEditor.SelectionStart, RawEditor.SelectionLength));
    private void BlockquoteButton_Click(object sender, RoutedEventArgs e) => ApplyResult(MarkdownFormattingService.ToggleBlockquote(RawEditor.Text, RawEditor.SelectionStart, RawEditor.SelectionLength));
    private void LinkButton_Click(object sender, RoutedEventArgs e) => ApplyResult(MarkdownFormattingService.InsertLink(RawEditor.Text, RawEditor.SelectionStart, RawEditor.SelectionLength));
    private void HorizontalRuleButton_Click(object sender, RoutedEventArgs e) => ApplyResult(MarkdownFormattingService.InsertHorizontalRule(RawEditor.Text, RawEditor.SelectionStart));
    private void TableButton_Click(object sender, RoutedEventArgs e) => ApplyResult(MarkdownFormattingService.InsertTable(RawEditor.Text, RawEditor.SelectionStart));
    private void ApplyWrap(string prefix, string suffix) => ApplyResult(MarkdownFormattingService.ToggleWrap(RawEditor.Text, RawEditor.SelectionStart, RawEditor.SelectionLength, prefix, suffix));
    private void ApplyHeader(int level) => ApplyResult(MarkdownFormattingService.ToggleHeader(RawEditor.Text, RawEditor.SelectionStart, level));
    private void ApplyList(bool ordered) => ApplyResult(MarkdownFormattingService.ToggleListPrefix(RawEditor.Text, RawEditor.SelectionStart, RawEditor.SelectionLength, ordered));

    private void ApplyResult(EditResult result)
    {
        string oldText = RawEditor.Text;
        string newText = result.NewText;
        int changeStart = 0;
        int oldEnd = oldText.Length;
        int newEnd = newText.Length;
        while (changeStart < oldEnd && changeStart < newEnd && oldText[changeStart] == newText[changeStart]) changeStart++;
        while (oldEnd > changeStart && newEnd > changeStart && oldText[oldEnd - 1] == newText[newEnd - 1]) { oldEnd--; newEnd--; }
        RawEditor.SelectionStart = changeStart;
        RawEditor.SelectionLength = oldEnd - changeStart;
        RawEditor.SelectedText = newText[changeStart..newEnd];
        RawEditor.SelectionStart = result.NewSelectionStart;
        RawEditor.SelectionLength = result.NewSelectionLength;
        RawEditor.Focus(FocusState.Programmatic);
    }

    private void ShowError(string title, Exception exception) => ShowMessage(title, exception.Message, InfoBarSeverity.Error);

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        NotificationBar.Title = title;
        NotificationBar.Message = message;
        NotificationBar.Severity = severity;
        NotificationBar.IsOpen = true;
    }
}
