namespace ReTex.Core.Paint;

public sealed class PaintHistory
{
    private readonly LinkedList<PaintHistoryEntry> _undo = new();
    private readonly Stack<PaintHistoryEntry> _redo = new();
    private long _bytes;
    public int MaxActions { get; set; } = 100;
    public long MaxBytes { get; set; } = 512L * 1024 * 1024;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;
    public long EstimatedBytes => _bytes;

    public PaintTransaction Begin(string name) => new(this, name);

    internal void Commit(PaintHistoryEntry entry)
    {
        if (entry.Tiles.Count == 0) return;
        _undo.AddLast(entry); _redo.Clear(); _bytes += entry.Bytes;
        Trim();
    }

    private void Trim()
    {
        while (_undo.Count > MaxActions || _bytes > MaxBytes)
        { _bytes -= _undo.First!.Value.Bytes; _undo.RemoveFirst(); }
    }

    public IReadOnlyCollection<PaintDocument> Undo()
    {
        if (_undo.Last is null) return Array.Empty<PaintDocument>();
        var entry = _undo.Last.Value; _undo.RemoveLast(); _bytes -= entry.Bytes;
        entry.RestoreBefore(); _redo.Push(entry);
        return entry.Documents;
    }

    public IReadOnlyCollection<PaintDocument> Redo()
    {
        if (_redo.Count == 0) return Array.Empty<PaintDocument>();
        var entry = _redo.Pop(); entry.RestoreAfter(); _undo.AddLast(entry); _bytes += entry.Bytes; Trim();
        return entry.Documents;
    }
}

public sealed class PaintTransaction : IDisposable
{
    private readonly PaintHistory _history;
    private readonly string _name;
    private readonly Dictionary<TileKey, byte[]> _before = new();
    private readonly Dictionary<PaintDocument, bool> _dirtyBefore = new();
    private bool _completed;
    internal PaintTransaction(PaintHistory history, string name) { _history = history; _name = name; }

    public void Capture(PaintDocument document, int x, int y)
    {
        _dirtyBefore.TryAdd(document, document.IsDirty);
        var key = new TileKey(document, x / PaintDocument.TileSize, y / PaintDocument.TileSize);
        if (!_before.ContainsKey(key)) _before[key] = document.CopyTile(key.X, key.Y);
    }

    public void CaptureRegion(PaintDocument document, int x0, int y0, int x1, int y1)
    {
        _dirtyBefore.TryAdd(document, document.IsDirty);
        int tx0 = Math.Max(0, x0) / PaintDocument.TileSize;
        int ty0 = Math.Max(0, y0) / PaintDocument.TileSize;
        int tx1 = Math.Min(document.Width - 1, x1) / PaintDocument.TileSize;
        int ty1 = Math.Min(document.Height - 1, y1) / PaintDocument.TileSize;
        for (int ty = ty0; ty <= ty1; ty++)
        for (int tx = tx0; tx <= tx1; tx++)
        {
            var key = new TileKey(document, tx, ty);
            if (!_before.ContainsKey(key)) _before[key] = document.CopyTile(tx, ty);
        }
    }

    public void Commit()
    {
        if (_completed) return;
        _completed = true;
        var tiles = _before.Select(pair => new TileChange(pair.Key, pair.Value,
            pair.Key.Document.CopyTile(pair.Key.X, pair.Key.Y))).Where(x => !x.Before.SequenceEqual(x.After)).ToList();
        foreach (var doc in tiles.Select(t => t.Key.Document).Distinct()) doc.IsDirty = true;
        _history.Commit(new PaintHistoryEntry(_name, tiles));
    }

    public IReadOnlyCollection<PaintDocument> Rollback()
    {
        if (_completed) return Array.Empty<PaintDocument>();
        _completed = true;
        foreach (var pair in _before)
            pair.Key.Document.RestoreTile(pair.Key.X, pair.Key.Y, pair.Value);
        foreach (var pair in _dirtyBefore)
            pair.Key.IsDirty = pair.Value;
        return _before.Keys.Select(key => key.Document).Distinct().ToArray();
    }

    public void Dispose() { if (!_completed) Commit(); }
}

internal readonly record struct TileKey(PaintDocument Document, int X, int Y);
internal sealed record TileChange(TileKey Key, byte[] Before, byte[] After);

internal sealed class PaintHistoryEntry
{
    public string Name { get; }
    public List<TileChange> Tiles { get; }
    public long Bytes => Tiles.Sum(t => (long)t.Before.Length + t.After.Length);
    public IReadOnlyCollection<PaintDocument> Documents => Tiles.Select(t => t.Key.Document).Distinct().ToArray();
    public PaintHistoryEntry(string name, List<TileChange> tiles) { Name = name; Tiles = tiles; }
    public void RestoreBefore() { foreach (var t in Tiles) t.Key.Document.RestoreTile(t.Key.X, t.Key.Y, t.Before); }
    public void RestoreAfter() { foreach (var t in Tiles) t.Key.Document.RestoreTile(t.Key.X, t.Key.Y, t.After); }
}
