using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace MDreader.Services;

public static class FileService
{
    public static async Task<string?> PickOpenFilePathAsync(Window owner)
    {
        var picker = new FileOpenPicker(owner.AppWindow.Id);
        picker.FileTypeFilter.Add(".md");
        picker.FileTypeFilter.Add(".markdown");
        picker.FileTypeFilter.Add(".txt");

        var result = await ToTask(picker.PickSingleFileAsync());
        return result?.Path;
    }

    public static async Task<string?> PickSaveFilePathAsync(Window owner, string suggestedName)
    {
        var picker = new FileSavePicker(owner.AppWindow.Id);
        picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
        picker.SuggestedFileName = suggestedName;

        var result = await ToTask(picker.PickSaveFileAsync());
        return result?.Path;
    }

    public static async Task<string?> PickExportFilePathAsync(
        Window owner,
        string suggestedName,
        string description,
        params string[] extensions)
    {
        var picker = new FileSavePicker(owner.AppWindow.Id);
        picker.FileTypeChoices.Add(description, new List<string>(extensions));
        picker.SuggestedFileName = suggestedName;

        var result = await ToTask(picker.PickSaveFileAsync());
        return result?.Path;
    }

    public static async Task<string?> PickImageFilePathAsync(Window owner)
    {
        var picker = new FileOpenPicker(owner.AppWindow.Id);
        foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg" })
            picker.FileTypeFilter.Add(extension);

        var result = await ToTask(picker.PickSingleFileAsync());
        return result?.Path;
    }

    public static Task<string> ReadTextAsync(string path) => File.ReadAllTextAsync(path);

    public static async Task WriteTextAsync(string path, string text)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new IOException("The selected file has no parent folder.");
        Directory.CreateDirectory(directory);

        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{System.Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static async Task<string> CopyImageToDocumentAssetsAsync(string imagePath, string documentPath)
    {
        string documentDirectory = Path.GetDirectoryName(Path.GetFullPath(documentPath))!;
        string assetsDirectory = Path.Combine(documentDirectory, "assets");
        Directory.CreateDirectory(assetsDirectory);

        string baseName = Path.GetFileNameWithoutExtension(imagePath);
        string extension = Path.GetExtension(imagePath).ToLowerInvariant();
        string destination = Path.Combine(assetsDirectory, baseName + extension);
        int suffix = 2;
        while (File.Exists(destination) && !FilesAreSame(imagePath, destination))
            destination = Path.Combine(assetsDirectory, $"{baseName}-{suffix++}{extension}");

        if (!FilesAreSame(imagePath, destination))
            await Task.Run(() => File.Copy(imagePath, destination, overwrite: false));

        return "assets/" + Uri.EscapeDataString(Path.GetFileName(destination));
    }

    public static async Task<string?> SaveClipboardImageToDocumentAssetsAsync(string documentPath)
    {
        DataPackageView clipboard = Clipboard.GetContent();
        if (!clipboard.Contains(StandardDataFormats.Bitmap)) return null;

        RandomAccessStreamReference reference = await ToTask(clipboard.GetBitmapAsync());
        using IRandomAccessStreamWithContentType stream = await ToTask(reference.OpenReadAsync());
        if (stream.Size > uint.MaxValue) throw new IOException("The clipboard image is too large.");

        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await ToTask(reader.LoadAsync((uint)stream.Size));
        byte[] bytes = new byte[(int)stream.Size];
        reader.ReadBytes(bytes);

        string extension = stream.ContentType.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            _ => ".png"
        };
        string documentDirectory = Path.GetDirectoryName(Path.GetFullPath(documentPath))!;
        string assetsDirectory = Path.Combine(documentDirectory, "assets");
        Directory.CreateDirectory(assetsDirectory);
        string fileName = $"pasted-image-{DateTime.Now:yyyyMMdd-HHmmssfff}{extension}";
        await File.WriteAllBytesAsync(Path.Combine(assetsDirectory, fileName), bytes);
        return "assets/" + Uri.EscapeDataString(fileName);
    }

    public static DateTime? GetLastWriteTimeUtc(string? path) =>
        path is not null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;

    private static bool FilesAreSame(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), System.StringComparison.OrdinalIgnoreCase);

    // Bridges Windows.Foundation's IAsyncOperation to Task manually: the GetAwaiter/AsTask
    // extension methods aren't resolving in this project's WinRT projection, but
    // Completed/GetResults are core IAsyncInfo interface members so this works regardless.
    internal static Task<T> ToTask<T>(IAsyncOperation<T> operation)
    {
        var tcs = new TaskCompletionSource<T>();
        operation.Completed = (op, status) =>
        {
            switch (status)
            {
                case AsyncStatus.Completed:
                    tcs.SetResult(op.GetResults());
                    break;
                case AsyncStatus.Canceled:
                    tcs.SetCanceled();
                    break;
                default:
                    tcs.SetException(op.ErrorCode);
                    break;
            }
        };
        return tcs.Task;
    }
}
