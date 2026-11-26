# MDreader — WinUI 3 Markdown Reader/Editor

## Context

This is a greenfield build, not a fix to existing code. MDreader is a Windows desktop app that reads and edits Markdown files with a Microsoft Fluent ("Microsoft-UX-style") look. A known requirement to design around from the start: in the raw markdown editing surface, `#` header markers must never appear misaligned, and a right-hand formatting pane (Bold, Headers, etc.) must be fully wired up — not just present in the UI.

Confirmed decisions:
- Platform/stack: **WinUI 3 (.NET/C#)**, native Fluent Design — most authentic Microsoft UX.
- Editor model: **raw markdown text + a right-hand toolbar** that inserts/wraps markdown syntax at the cursor/selection (not a WYSIWYG rich-text editor).
- The app also supports a rendered **Preview** view, since "reading" markdown implies seeing it formatted, not just raw text.

## Root cause of the alignment bug (designed around, not just patched)

Misaligned `#` markers in a raw editor are a textbook symptom of: a proportional font (column position ≠ character count), or a `RichEditBox` (legacy Win32 RichEdit/RTF backing store with autoformat/auto-indent behavior that injects inconsistent per-paragraph margins), or inconsistent `Padding`/margin applied per line.

**Fix:** use a plain `TextBox` (not `RichEditBox`) holding the whole document as one text blob, monospace font, and a single fixed `Padding` set once. Because it's one control with one padding value, there is structurally no way for line N to differ from line N+1.

## Tech decisions

| Decision | Choice | Why |
|---|---|---|
| UI framework | WinUI 3 (Windows App SDK), C#, packaged app | Native Fluent look |
| TFM | `net8.0-windows10.0.19041.0` | .NET 8 LTS, stable WinAppSDK support |
| Raw editor control | `TextBox`, not `RichEditBox` | Eliminates the alignment-bug class (see above); plain text is fine since toolbar only manipulates raw markdown strings |
| Markdown preview | Markdig → HTML → WebView2 `NavigateToString` | `CommunityToolkit.WinUI.UI.Controls.Markdown` was checked and rejected — it's last published Nov 2021 against the legacy pre-8.0 CommunityToolkit namespace family, floors `Microsoft.WindowsAppSDK >= 1.0.0` without being republished against current WinAppSDK, and risks binary/version conflicts. Markdig is actively maintained and dependency-light; WebView2 is the standard, low-risk rendering host. |
| MVVM toolkit | None for v1 | Single-window, single-document app; plain code-behind + one stateless service class is enough. Add `CommunityToolkit.Mvvm` later only if the app grows multi-document/tabs. |
| Syntax highlighting (colored `#`, `**`, etc.) | Deferred to v2 | `TextBox` can't do inline run coloring. Forcing it now means either reintroducing `RichEditBox` (reintroducing the alignment-risk class) or a custom-drawn overlay — out of scope until the core editor/toolbar/file-IO loop is proven. |
| Preview toggle | `AppBarToggleButton` in a `CommandBar`, swaps center-column content in place | Simpler than a Frame/page-navigation split for a single always-resident document |

## Project structure

```
MDreader/
  MDreader.sln
  MDreader/
    MDreader.csproj
    Package.appxmanifest
    App.xaml / App.xaml.cs
    Assets/                       (app icons + katex/ for math; preview CSS is inlined in MarkdownRenderService)
    Views/
      MainWindow.xaml / .xaml.cs  (single window for v1)
    Models/
      DocumentState.cs            (CurrentFilePath, IsDirty, DirtyChanged event)
    Services/
      MarkdownFormattingService.cs  (selection-wrapping logic — the toolbar engine)
      MarkdownRenderService.cs      (Markdig pipeline + Markdown→HTML)
      FileService.cs                (Open/Save/SaveAs via Windows.Storage.Pickers)
    Helpers/
      WindowHelper.cs              (HWND interop for pickers)
```

No `ViewModels/` folder for v1 — keep it lean; add later only if needed.

## NuGet packages

- `Microsoft.WindowsAppSDK` (latest stable) — WinUI 3 runtime/controls
- `Microsoft.Windows.SDK.BuildTools` (matching version) — required by template
- `Markdig` (latest stable) — Markdown→HTML for Preview
- `Microsoft.Web.WebView2` (latest stable) — explicit pin for the Preview host

Do **not** add `CommunityToolkit.WinUI.UI.Controls.Markdown` or any legacy `CommunityToolkit.WinUI.UI` (v7-era) package — see rejection rationale above.

Exact pinned versions in use (found compatible after the deployment debugging below): `Microsoft.WindowsAppSDK` 1.8.260508005, `Microsoft.Windows.SDK.BuildTools` 10.0.26100.4654, `Microsoft.Web.WebView2` 1.0.3719.77, `Markdig` 0.40.0.

## Deployment model: unpackaged + self-contained WindowsAppSDK

The csproj sets `<WindowsPackageType>None</WindowsPackageType>` and `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>`. This was a deliberate fix, not the template default — **do not remove these** without understanding why they're there:

- The VS "Single-project MSIX" template defaults to a packaged-capable app with `EnableMsixTooling=true` and no explicit `WindowsPackageType`. Running that default via plain `dotnet run` (no package identity) throws `COMException 0x80040154 (REGDB_E_CLASSNOTREG)` in the WindowsAppSDK Foundation auto-initializer (`DeploymentManagerCS.AutoInitialize`) — this happened identically across WindowsAppSDK 1.7, 1.8, and 2.2, and persisted even after installing the matching `windowsappruntimeinstall-x64.exe` redistributable both as a normal user and elevated.
- The actual `Deploy` MSBuild target (which VS uses for F5 packaged debugging) is provided by the VS IDE's packaging extension, not by plain MSBuild/SDK targets — `msbuild /t:Deploy` fails with `MSB4057: target does not exist` outside VS.
- The fix: `WindowsAppSDKSelfContained=true` bundles the WindowsAppSDK runtime DLLs (Microsoft.UI.Xaml.dll, Microsoft.Windows.ApplicationModel.WindowsAppRuntime.Projection.dll, etc.) directly into the app's own output folder, so the app loads them privately instead of resolving a system-registered runtime via Dynamic Dependencies/COM. This works from a plain `dotnet run`/`dotnet build` CLI loop with no MSIX packaging, signing certs, or `Add-AppxPackage` involved.
- Windows Developer Mode (`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock\AllowDevelopmentWithoutDevLicense=1`) was also enabled on this machine as part of this debugging, but turned out not to be the actual fix — it's harmless to leave on, but the self-contained deployment change above is what made `dotnet run` work.
- To run during development: `dotnet run -p:Platform=x64 -c Debug -f net8.0-windows10.0.19041.0 -r win-x64 --no-self-contained` from the `MDreader/MDreader` project folder. (`--no-self-contained` here refers to the .NET runtime, not the WindowsAppSDK — they're independent self-contained switches.)

## Main window layout

Root `Grid` (rows: `Auto`, `*`):
- Row 0: `CommandBar` — New, Open, Save, Save As, and a Preview `AppBarToggleButton`.
- Row 1: `Grid` (columns: `*`, `Auto` width 180):
  - Column 0: `RawEditor` (`TextBox`) and `PreviewView` (`WebView2`) occupy the **same cell**; only one is `Visible` at a time, driven by `IsPreviewMode`.
  - Column 1: `FormattingPanel` (`StackPanel`, width 180) — Bold, Italic, H1–H3, Bulleted List, Numbered List, Blockquote, Inline Code, Code Block, Link, Horizontal Rule. Hidden while in Preview mode (no live selection to format against rendered HTML).

`RawEditor` properties (the concrete alignment fix):
- `FontFamily="Cascadia Mono, Consolas, Courier New"`, fixed `FontSize` (e.g. 14), never varied per line.
- `Padding="12,8,12,8"` set once, never recalculated.
- `TextWrapping="Wrap"`, `AcceptsReturnInNewLine="True"`, `IsSpellCheckEnabled="False"`, `IsTextPredictionEnabled="False"`.
- `BorderThickness="0"` with a flat background — reads as a borderless "code surface."

**Verification step (do this before writing toolbar code):** type `# Heading` and `## Heading` on consecutive lines and confirm both `#` runs start at the same left edge. This is the direct regression test for the bug that motivated the project.

## View modes (v1.2, current behaviour — supersedes the layout notes above)

The workspace has three mutually exclusive modes, chosen with the **Read / Split / Edit** toggle group in the `CommandBar` (`MainWindow.SetViewMode` is the only place that changes them; `UpdateAdaptivePanes` derives every pane's visibility and column width from the mode):

| Mode | Shows | Notes |
|---|---|---|
| **Read** | rendered preview only, full width | Formatting pane hidden (nothing to format). Outline clicks scroll the preview to the Nth `<h1–h6>`. |
| **Split** | source + preview with the draggable splitter | Splitter ratio is remembered across mode switches. Focus mode drops the preview. |
| **Edit** | Markdown source only | |

- **Ctrl+E** flips Read ⇄ the last source mode (Split or Edit). It's a window-level `KeyboardAccelerator` *and* a JS keydown listener injected into the WebView2 (`AddScriptToExecuteOnDocumentCreatedAsync` → `postMessage('toggle-read-edit')`), because XAML accelerators don't fire while focus is inside the WebView2. Ctrl+Shift+R = Read, Ctrl+Shift+P = Split.
- The preview's corner button reads "Read" in Split and "Edit" in Read (same action as Ctrl+E).
- **Every file load (double-click, Open, Recent, drag-drop) opens in Read.** This is deliberate: an earlier draft remembered the last-used mode, which meant one Ctrl+E into Edit made the next double-click open in source view and look broken. Only the preferred *source* view is remembered: `AppSettings.SourceMode` (`"Split"` default or `"Edit"`) is what Ctrl+E/the Edit button go to. A blank start (no file) or **New** opens in that source view.

## Math rendering (KaTeX, offline)

- **Version:** KaTeX **0.19.1**, from `npm pack katex`. Bundled in `MDreader/Assets/katex/`: `katex.min.js`, `katex.min.css`, `fonts/*.woff2` (woff/ttf fallbacks deliberately left out; WebView2 always takes woff2) and `LICENSE.txt`. No auto-render: the inline script renders Markdig's elements directly. The csproj copies `Assets\katex\**` to the output (`Content` + `PreserveNewest`).
- **Source:** Markdig's Mathematics extension (part of `UseAdvancedExtensions`) turns `$..$` into `<span class="math">\(..\)</span>` and `$$..$$` into `<div class="math">\[..\]</div>`. `RenderToHtml` adds KaTeX only when the body has `class="math"`.
- **Serving:** `InitializePreviewAsync` maps a second WebView2 virtual host, `mdreader-assets.local` → `<AppContext.BaseDirectory>\Assets\katex` (`MarkdownRenderService.MathAssetHost`). It is fixed for the app's lifetime and separate from the per-document `mdreader.local` mapping. Pages get `<link>`/`<script nonce>` tags pointing at `https://mdreader-assets.local/…`; the CSS loads the fonts from `fonts/` on the same host. Nothing is inlined into the data URL.
- **Render + scroll:** the nonce'd inline script strips the `\(`/`\)`/`\[`/`\]` delimiters, calls `katex.render` (`displayMode` for `div.math`, `throwOnError: false`, theme-aware `errorColor`) and adds `.math-rendered`. It restores the scroll ratio only after `document.fonts.ready`, because typesetting and font loading change the page height.
- **CSP:** without math it is unchanged: `default-src 'none'; img-src data: https: http: file:; style-src 'unsafe-inline'; script-src 'nonce-<random>'; frame-src 'none'; media-src 'none'; object-src 'none'; base-uri https://mdreader.local; form-action 'none'`. With math it adds the KaTeX origin to `style-src` and `script-src` and adds `font-src <origin>`. The origin is `https://mdreader-assets.local` in the app and `https://cdn.jsdelivr.net` in exports.
- **Styling/fallback:** KaTeX inherits the theme's text colour. `.katex-display` gets `overflow-x: auto`, so long display math scrolls inside its own box. If KaTeX fails to load, `.math:not(.math-rendered)` shows the raw TeX in monospace on the code surface colour (display math as a pre-wrapped block), so it stays readable.
- **Export HTML / Copy HTML** call `RenderToHtml(..., standalone: true)`. This references `https://cdn.jsdelivr.net/npm/katex@0.19.1/dist/` with SRI hashes (`integrity` + `crossorigin`), because a standalone file can't reach the virtual host. Online, the math typesets; offline, the raw TeX fallback shows. **When upgrading KaTeX**, replace the files in `Assets/katex`, then update `KatexVersion` and both `sha384` hashes in `MarkdownRenderService` (`openssl dgst -sha384 -binary katex.min.js | openssl base64 -A`).
- **Headless verification** (never launch the GUI: launching any MDreader.exe re-registers the `.md` association to that exe). `dotnet run --project MDreader.Tests -- --dump-math <sample.md> <outdir>` writes `dark/light/scroll/export.html`. Load them in Playwright as `data:` URLs and `page.route('https://mdreader-assets.local/**')` → `route.fulfill({ path: <Assets/katex> + pathname })`, so the real CSP is exercised. Screenshots go in `.playwright-artifacts/` (gitignored). The unit tests in `MDreader.Tests` also assert the CSP and asset URLs.

## Raw HTML in Markdown (v1.3)
- HTML in notes renders (`<details>`/`<summary>`, `<kbd>`, `<sub>`, `<br>`, …): the pipeline no longer calls `.DisableHtml()`. `details`/`summary`/`kbd` have theme styles in the page CSS.
- **Safety, two layers:** (1) `UnsafeHtmlTag` turns the `<` of `script`, `iframe`, `frame(set)`, `object`, `embed`, `applet`, `form`, `base`, `meta`, `link`, `noscript`, `template`, `portal` into `&lt;`, so those tags show as text; (2) the CSP nonce is a random 32-hex value per render (it used to be the fixed `mdreader`, which a document could copy once HTML was allowed). Inline handlers (`onerror=`) and `javascript:` URLs stay blocked by the CSP; `NavigationStarting` still cancels non-user navigations.

## Opening files by double-click

`App.OnLaunched` reads the path from `Environment.GetCommandLineArgs()[1]` (unpackaged apps don't get it via `LaunchActivatedEventArgs`) and `MainWindow` loads it on first activation. The registry command is `"…\MDreader.exe" "%1"` (written by both the installer and `FileAssociationService`). **v1.0.0 did not do this**, so a double-clicked file opened as "Untitled"; fixed from v1.1.0 onward.

## Formatting toolbar — concrete design

`Services/MarkdownFormattingService.cs` is a **stateless** service: takes primitive text/selection in, returns an `EditResult` (`NewText`, `NewSelectionStart`, `NewSelectionLength`) — no reference to the `TextBox`, so it's testable without UI.

```csharp
public readonly record struct EditResult(string NewText, int NewSelectionStart, int NewSelectionLength);

public static class MarkdownFormattingService
{
    public static EditResult ToggleWrap(string fullText, int selStart, int selLength, string prefix, string suffix)
    {
        string selected = fullText.Substring(selStart, selLength);
        if (selected.StartsWith(prefix) && selected.EndsWith(suffix) && selected.Length >= prefix.Length + suffix.Length)
        {
            string unwrapped = selected.Substring(prefix.Length, selected.Length - prefix.Length - suffix.Length);
            string newText = fullText.Remove(selStart, selLength).Insert(selStart, unwrapped);
            return new EditResult(newText, selStart, unwrapped.Length);
        }
        if (selLength == 0)
        {
            const string placeholder = "text";
            string newText = fullText.Insert(selStart, prefix + placeholder + suffix);
            return new EditResult(newText, selStart + prefix.Length, placeholder.Length);
        }
        string wrapped = prefix + selected + suffix;
        string newTextWrapped = fullText.Remove(selStart, selLength).Insert(selStart, wrapped);
        return new EditResult(newTextWrapped, selStart + prefix.Length, selected.Length);
    }

    public static EditResult ToggleHeader(string fullText, int caretPos, int level)
    {
        int lineStart = fullText.LastIndexOf('\n', System.Math.Max(0, caretPos - 1)) + 1;
        int lineEnd = fullText.IndexOf('\n', caretPos);
        if (lineEnd < 0) lineEnd = fullText.Length;
        string line = fullText[lineStart..lineEnd];

        string stripped = System.Text.RegularExpressions.Regex.Replace(line, "^#{1,6}\\s*", "");
        string newHashes = new string('#', level);
        bool alreadyThisLevel = line.StartsWith(newHashes + " ") && !line.StartsWith(newHashes + "#");
        string newLine = alreadyThisLevel ? stripped : newHashes + " " + stripped;

        string newText = fullText.Remove(lineStart, lineEnd - lineStart).Insert(lineStart, newLine);
        return new EditResult(newText, lineStart + newLine.Length, 0);
    }

    public static EditResult InsertLink(string fullText, int selStart, int selLength)
    {
        string selected = selLength > 0 ? fullText.Substring(selStart, selLength) : "link text";
        string markdown = "[" + selected + "](url)";
        string newText = fullText.Remove(selStart, selLength).Insert(selStart, markdown);
        int urlStart = selStart + selected.Length + 3; // past "[text]("
        return new EditResult(newText, urlStart, 3);
    }

    // ToggleListPrefix(fullText, selStart, selLength, ordered): expand selection to full
    // line boundaries, then for each line toggle "- " (unordered) or renumbered "N. "
    // (ordered) prefixes, same line-rebuild pattern as ToggleHeader. Most fiddly case —
    // budget extra time/testing.
}
```

Wiring from `MainWindow.xaml.cs` — every button handler follows the same pattern: call the service, write `NewText` back to `RawEditor.Text`, **then** set `SelectionStart`/`SelectionLength`, restore focus, mark dirty. Order matters because setting `Text` fires `TextChanged`.

```csharp
private void BoldButton_Click(object sender, RoutedEventArgs e) => ApplyWrap("**", "**");
private void ApplyWrap(string prefix, string suffix)
{
    var result = MarkdownFormattingService.ToggleWrap(RawEditor.Text, RawEditor.SelectionStart, RawEditor.SelectionLength, prefix, suffix);
    RawEditor.Text = result.NewText;
    RawEditor.SelectionStart = result.NewSelectionStart;
    RawEditor.SelectionLength = result.NewSelectionLength;
    RawEditor.Focus(FocusState.Programmatic);
    _document.MarkDirty();
}
```

Same pattern for `H1Button_Click`/`H2`/`H3` → `ApplyHeader(level)`, and `LinkButton_Click` → `InsertLink`.

**Known v1 limitation to document, not silently ship as a surprise:** `ToggleWrap`'s toggle-off detection only checks whether the *exact selection* is wrapped — placing the cursor inside already-bold text without selecting the full `**...**` span won't be detected as "already bold."

## File I/O

`Models/DocumentState.cs`: holds `CurrentFilePath` (`string?`, not `StorageFile`), `IsDirty`, and a `DirtyChanged` event via `MarkDirty()`/`MarkClean()`. Window title reflects `{filename}{*if dirty} - MDreader` as a visible end-to-end check.

**Picker API: use `Microsoft.Windows.Storage.Pickers`, not the legacy `Windows.Storage.Pickers`.** The classic UWP-era `Windows.Storage.Pickers.FileOpenPicker`/`FileSavePicker` (even with the documented `WinRT.Interop.InitializeWithWindow` HWND association) showed no observable dialog in this unpackaged/self-contained app during testing — the legacy pickers are fundamentally designed around package identity and are unreliable in true unpackaged scenarios, which is exactly why WindowsAppSDK 1.7+ ships a dedicated replacement. The new API takes a `Microsoft.UI.WindowId` directly in the constructor (no `InitializeWithWindow` call needed) and reliably shows the real dialog:

```csharp
using Microsoft.Windows.Storage.Pickers;

var picker = new FileOpenPicker(owner.AppWindow.Id);
picker.FileTypeFilter.Add(".md");
var result = await ToTask(picker.PickSingleFileAsync()); // returns PickFileResult? with a .Path string
```

**Also: `IAsyncOperation<T>.GetAwaiter()`/`.AsTask()` do not resolve in this project's WinRT projection** (CS1061/CS4036, cause not fully root-caused — tried `System.Runtime.InteropServices.WindowsRuntime`, ruled out `WindowsAppSDKSelfContained` as the cause). Worked around with a manual bridge using `IAsyncInfo`'s core (non-extension) `Completed`/`GetResults` members, which are unaffected:

```csharp
private static Task<T> ToTask<T>(IAsyncOperation<T> operation)
{
    var tcs = new TaskCompletionSource<T>();
    operation.Completed = (op, status) => { /* AsyncStatus.Completed -> tcs.SetResult(op.GetResults()), etc. */ };
    return tcs.Task;
}
```

Since the new picker returns a path string (not a `StorageFile`), `ReadTextAsync`/`WriteTextAsync` in `FileService.cs` are now plain `System.IO.File.ReadAllTextAsync`/`WriteAllTextAsync` — no WinRT involved at all for actual file content I/O, only for the picker dialogs themselves.

UI: `CommandBar` (not a classic `MenuBar`) for New/Open/Save/Save As — idiomatic Fluent top-level command surface. A `MenuBar` with File/Edit/View can be added later without disruption.
- **New**: if dirty, `ContentDialog` (Yes/No/Cancel) prompt; then clear `RawEditor.Text`, reset `DocumentState`.
- **Open**: pick → read → set `RawEditor.Text` → set `CurrentFilePath` → `MarkClean()`. Guard with a `_suppressDirty` flag around this programmatic `Text` assignment so loading isn't treated as a user edit.
- **Save**: if no `CurrentFilePath`, delegate to Save As; else write and `MarkClean()`.
- **Save As**: pick → write → set `CurrentFilePath` → `MarkClean()`.
- `RawEditor.TextChanged` → `_document.MarkDirty()` (suppressed during the Open/New programmatic assignment).

**Verification note:** the Save/Open dialogs were confirmed to construct and appear correctly (correct `.md` file filter, correct suggested filename pre-filled). Full automated click-through of the native Windows 11 dialog wasn't achievable — its filename field and Save/Cancel controls expose no invokable UI Automation patterns and resist focus-stealing from a background process. This is a known limitation of the modern Explorer-style common dialogs, not an app bug. If touching this code again, do a quick manual Save/Open round-trip check rather than assuming automation will work.

## Preview mode

`Services/MarkdownRenderService.cs` builds one `MarkdownPipeline` (`new MarkdownPipelineBuilder().UseAdvancedExtensions().Build()`) and exposes `RenderToHtml(markdown)` via `Markdown.ToHtml`. The HTML is inlined into a minimal shell with embedded `<style>` (not a separate CSS asset file — simpler for v1, revisit if theming needs grow). On entering Preview mode, call `PreviewView.NavigateToString(html)` (no temp file needed). Re-render only when entering Preview, not on every keystroke. Theme-aware CSS swap (light/dark) is a nice-to-have, not a v1 blocker.

**Gotcha confirmed by testing:** `PreviewView.NavigateToString(html)` throws if called before `CoreWebView2` has finished its async first-time initialization — calling it synchronously inside the toggle's `Click` handler silently swallowed the exception (no crash, but the visibility swap that came *after* it in code never ran, so toggling "on" visually highlighted the button but kept showing the raw editor). Fix: `await PreviewView.EnsureCoreWebView2Async();` before `NavigateToString`, and do the `RawEditor`/`PreviewView`/`FormattingPanel` visibility swap *before* that await, not after — so the UI swaps immediately even while WebView2 is still spinning up. Verified via screenshot: headings, **bold**, _italic_, `inline code`, bulleted lists, and blockquotes all render correctly through this pipeline.

## Build order (each step independently runnable/demoable)

1. **Scaffold**: WinUI 3 "Blank App, Packaged" project; confirm it builds and runs empty.
2. **Raw editor shell**: two-column Grid, `RawEditor` with the alignment-fix properties above. **Manually verify the `# Heading` / `## Heading` alignment check before proceeding** — this is the explicit regression target.
3. **Formatting toolbar logic**: implement `MarkdownFormattingService` (no UI yet).
4. **Wire toolbar buttons**: add the right-panel buttons, wire to the service. Verify: select→Bold wraps with `**`, click again unwraps, no-selection Bold inserts `**text**` with `text` selected.
5. **File I/O**: `CommandBar`, `FileService`, `DocumentState`, dirty-flag title. Verify round-trip: type, Save As, close, reopen, Open same file, content matches.
6. **Preview mode**: `MarkdownRenderService`, `WebView2` in the shared cell, Preview toggle. Verify a file with headers/bold/list/code block renders correctly.
7. **Polish**: dark/light preview CSS parity, unsaved-changes prompt on close/New, keyboard accelerators (Ctrl+B/I/S/O/N) reusing the same handlers.

## Verification

- Step 2's alignment check (above) is the direct test for the bug that motivated this project — must pass before step 4.
- Step 4: manual toggle tests for Bold/Header/Link as described.
- Step 5: full file round-trip (save → close → reopen → contents match).
- Step 6: render a test document with all major markdown constructs and visually confirm Fluent-consistent styling.
- Run `dotnet build` after each step; run the app (F5) and exercise the feature just added before moving on.

## Environment setup status (completed)

Dev machine initially had no .NET SDK (only runtimes) and no usable Windows 10/11 SDK (only a leftover VS2019 x86 install). Setup performed:
- Visual Studio 2022 Community installed via winget with `ManagedDesktop` + `Universal` workloads and the `WindowsAppSDK.Cs` component group — provides the WinUI 3 project templates (used to seed this project's scaffold) and Windows 11 SDK.
- .NET 8 SDK installed via winget (`Microsoft.DotNet.SDK.8`) alongside the .NET 9 SDK VS brought in, since the project targets `net8.0-windows10.0.19041.0`.
- Windows Developer Mode enabled (see deployment-model section above).
- Project scaffold copied from the VS-installed template at `...\Extensions\<id>\ProjectTemplates\CSharp\1033\WinUI.Desktop.Cs.SingleProjectPackagedApp\` (raw template files, tokens hand-replaced since there's no GUI wizard in this environment) — useful if any future file needs re-deriving from the template.
- See "Deployment model" section above for why the csproj deviates from the template defaults.
