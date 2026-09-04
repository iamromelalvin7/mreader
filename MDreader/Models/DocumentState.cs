using System;
using System.IO;

namespace MDreader.Models;

public class DocumentState
{
    public string? CurrentFilePath { get; set; }
    public string CurrentFileName => CurrentFilePath is null ? "Untitled" : Path.GetFileName(CurrentFilePath);
    public bool IsDirty { get; private set; }
    public DateTime? LastKnownWriteTimeUtc { get; set; }

    public event EventHandler? DirtyChanged;

    public void MarkDirty()
    {
        if (!IsDirty)
        {
            IsDirty = true;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void MarkClean()
    {
        if (IsDirty)
        {
            IsDirty = false;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Reset()
    {
        CurrentFilePath = null;
        LastKnownWriteTimeUtc = null;
        IsDirty = false;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }
}
