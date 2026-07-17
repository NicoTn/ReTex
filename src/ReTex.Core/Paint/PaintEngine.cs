namespace ReTex.Core.Paint;

public enum PaintTool { Brush, Eraser, Eyedropper, Fill, MagicWand, Replace, Colorize, TextureTint }
public enum PaintMatchMode { Contiguous, Global }
public enum PaintSelectionCombine { Replace, Add, Subtract }

public readonly record struct PaintColor(byte B, byte G, byte R, byte A = 255)
{
    public string Hex => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
    public static PaintColor FromHex(string value)
    {
        string s = value.Trim().TrimStart('#');
        if (s.Length == 6) s = "FF" + s;
        if (s.Length != 8 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint n))
            throw new FormatException("Use #RRGGBB or #AARRGGBB.");
        return new PaintColor((byte)n, (byte)(n >> 8), (byte)(n >> 16), (byte)(n >> 24));
    }
}

public sealed record PaintBrushSettings(double Size = 32, double Hardness = 0.75,
    double Opacity = 1, double Flow = 1, double Spacing = 0.2);

public readonly record struct PaintPoint(double X, double Y);

public static class PaintStrokeSampler
{
    public static IEnumerable<PaintPoint> Between(double fromX, double fromY, double toX, double toY,
        double maximumSpacing)
    {
        double dx = toX - fromX, dy = toY - fromY;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        int count = Math.Max(1, (int)Math.Ceiling(distance / Math.Max(0.01, maximumSpacing)));
        for (int i = 1; i <= count; i++)
        {
            double amount = i / (double)count;
            yield return new PaintPoint(fromX + dx * amount, fromY + dy * amount);
        }
    }
}

public sealed class PaintDocument
{
    public const int TileSize = 64;
    public string Path { get; }
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public bool[] Selection { get; }
    public bool HasSelection { get; private set; }
    public bool IsDirty { get; internal set; }
    private int _dirtyMinX = int.MaxValue, _dirtyMinY = int.MaxValue, _dirtyMaxX = -1, _dirtyMaxY = -1;
    public void MarkDirty() { IsDirty = true; MarkPixelsChanged(0, 0, Width - 1, Height - 1); }
    public void MarkSaved() => IsDirty = false;

    public PaintDocument(string path, int width, int height, byte[] bgra)
    {
        if (bgra.Length != width * height * 4) throw new ArgumentException("Pixel buffer size mismatch.");
        Path = path; Width = width; Height = height; Pixels = bgra;
        Selection = new bool[width * height];
    }

    public PaintColor Sample(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1); y = Math.Clamp(y, 0, Height - 1);
        int i = (y * Width + x) * 4;
        return new PaintColor(Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }

    public void Stamp(double cx, double cy, PaintColor color, PaintBrushSettings brush,
        bool erase, PaintTransaction transaction)
    {
        double radius = Math.Max(0.5, brush.Size / 2);
        int x0 = Math.Max(0, (int)Math.Floor(cx - radius - 1));
        int y0 = Math.Max(0, (int)Math.Floor(cy - radius - 1));
        int x1 = Math.Min(Width - 1, (int)Math.Ceiling(cx + radius + 1));
        int y1 = Math.Min(Height - 1, (int)Math.Ceiling(cy + radius + 1));
        double hard = Math.Clamp(brush.Hardness, 0, 1);
        double inner = radius * hard;
        double radiusSquared = radius * radius, innerSquared = inner * inner;
        bool changed = false;
        transaction.CaptureRegion(this, x0, y0, x1, y1);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            if (HasSelection && !Selection[y * Width + x]) continue;
            double dx = x + .5 - cx, dy = y + .5 - cy, distanceSquared = dx * dx + dy * dy;
            if (distanceSquared >= radiusSquared) continue;
            double edge = distanceSquared <= innerSquared || radius <= inner + 1e-6
                ? 1 : 1 - (Math.Sqrt(distanceSquared) - inner) / (radius - inner);
            double amount = Math.Clamp(edge * brush.Opacity * brush.Flow, 0, 1);
            if (amount <= 0) continue;
            int i = (y * Width + x) * 4;
            if (erase)
            {
                byte alpha = Mix(Pixels[i + 3], 0, amount);
                if (alpha != Pixels[i + 3]) { Pixels[i + 3] = alpha; changed = true; }
            }
            else
            {
                byte b = Mix(Pixels[i], color.B, amount), g = Mix(Pixels[i + 1], color.G, amount);
                byte r = Mix(Pixels[i + 2], color.R, amount), a = Mix(Pixels[i + 3], color.A, amount);
                if (b == Pixels[i] && g == Pixels[i + 1] && r == Pixels[i + 2] && a == Pixels[i + 3]) continue;
                Pixels[i] = b; Pixels[i + 1] = g; Pixels[i + 2] = r; Pixels[i + 3] = a; changed = true;
            }
        }
        if (changed) MarkPixelsChanged(x0, y0, x1, y1);
    }

    public void SelectByColor(int x, int y, double tolerance, PaintMatchMode mode,
        PaintSelectionCombine combine = PaintSelectionCombine.Replace,
        CancellationToken cancellationToken = default, Action<double>? reportProgress = null)
    {
        var source = Sample(x, y);
        var matches = BuildAffected(source, x, y, tolerance, mode, cancellationToken,
            progress => reportProgress?.Invoke(progress * 0.8));
        for (int i = 0; i < Selection.Length; i++)
        {
            if ((i & 0x3fff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reportProgress?.Invoke(0.8 + 0.2 * i / Selection.Length);
            }
            matches[i] = combine switch
            {
                PaintSelectionCombine.Add => Selection[i] || matches[i],
                PaintSelectionCombine.Subtract => Selection[i] && !matches[i],
                _ => matches[i],
            };
        }
        cancellationToken.ThrowIfCancellationRequested();
        Array.Copy(matches, Selection, matches.Length);
        HasSelection = Selection.Any(v => v);
        reportProgress?.Invoke(1);
    }

    public void ClearSelection() { Array.Clear(Selection); HasSelection = false; }
    public void InvertSelection()
    {
        for (int i = 0; i < Selection.Length; i++) Selection[i] = !Selection[i];
        HasSelection = true;
    }

    public void ApplyColorOperation(int x, int y, PaintColor target, double tolerance,
        PaintMatchMode mode, PaintTool tool, PaintTransaction transaction,
        CancellationToken cancellationToken = default, Action<double>? reportProgress = null,
        double strength = 1)
    {
        var source = Sample(x, y);
        bool[] affected = BuildAffected(source, x, y, tolerance, mode, cancellationToken,
            progress => reportProgress?.Invoke(progress * 0.35));
        int tileColumns = (Width + TileSize - 1) / TileSize;
        int tileRows = (Height + TileSize - 1) / TileSize;
        var capturedTiles = new bool[tileColumns * tileRows];
        for (int p = 0; p < affected.Length; p++)
        {
            if ((p & 0x3fff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reportProgress?.Invoke(0.35 + 0.2 * p / affected.Length);
            }
            if (!affected[p] || (HasSelection && !Selection[p])) continue;
            int px = p % Width, py = p / Width;
            int tile = py / TileSize * tileColumns + px / TileSize;
            if (capturedTiles[tile]) continue;
            capturedTiles[tile] = true;
            transaction.Capture(this, px, py);
        }
        cancellationToken.ThrowIfCancellationRequested();

        int minX = Width, minY = Height, maxX = -1, maxY = -1;
        for (int p = 0; p < affected.Length; p++)
        {
            if ((p & 0x3fff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reportProgress?.Invoke(0.55 + 0.45 * p / affected.Length);
            }
            if (!affected[p] || (HasSelection && !Selection[p])) continue;
            int px = p % Width, py = p / Width;
            minX = Math.Min(minX, px); minY = Math.Min(minY, py); maxX = Math.Max(maxX, px); maxY = Math.Max(maxY, py);
            int i = p * 4;
            if (tool is PaintTool.Colorize or PaintTool.TextureTint)
            {
                RgbToHsl(Pixels[i + 2], Pixels[i + 1], Pixels[i], out _, out _, out double light);
                RgbToHsl(target.R, target.G, target.B, out double hue, out double saturation, out _);
                HslToRgb(hue, saturation, light, out byte tintedR, out byte tintedG, out byte tintedB);
                double amount = tool == PaintTool.TextureTint ? Math.Clamp(strength, 0, 1) : 1;
                Pixels[i] = Mix(Pixels[i], tintedB, amount);
                Pixels[i + 1] = Mix(Pixels[i + 1], tintedG, amount);
                Pixels[i + 2] = Mix(Pixels[i + 2], tintedR, amount);
            }
            else
            {
                Pixels[i] = target.B; Pixels[i + 1] = target.G; Pixels[i + 2] = target.R;
                Pixels[i + 3] = target.A;
            }
        }
        if (maxX >= 0) MarkPixelsChanged(minX, minY, maxX, maxY);
        reportProgress?.Invoke(1);
    }

    private bool[] BuildAffected(PaintColor source, int x, int y, double tolerance, PaintMatchMode mode,
        CancellationToken cancellationToken, Action<double>? reportProgress)
    {
        var result = new bool[Selection.Length];
        if (mode == PaintMatchMode.Global)
        {
            for (int p = 0; p < result.Length; p++)
            {
                if ((p & 0x3fff) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    reportProgress?.Invoke((double)p / result.Length);
                }
                result[p] = Matches(p, source, tolerance);
            }
            reportProgress?.Invoke(1);
            return result;
        }

        var seen = new bool[Selection.Length];
        var queue = new Queue<int>();
        int start = Math.Clamp(y, 0, Height - 1) * Width + Math.Clamp(x, 0, Width - 1);
        queue.Enqueue(start); seen[start] = true;
        int visited = 0;
        while (queue.Count > 0)
        {
            if ((visited++ & 0x3fff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reportProgress?.Invoke(Math.Min(0.99, (double)visited / result.Length));
            }
            int p = queue.Dequeue();
            if (!Matches(p, source, tolerance)) continue;
            result[p] = true;
            int px = p % Width, py = p / Width;
            Enqueue(px - 1, py); Enqueue(px + 1, py); Enqueue(px, py - 1); Enqueue(px, py + 1);
        }
        reportProgress?.Invoke(1);
        return result;

        void Enqueue(int px, int py)
        {
            if ((uint)px >= (uint)Width || (uint)py >= (uint)Height) return;
            int p = py * Width + px;
            if (!seen[p]) { seen[p] = true; queue.Enqueue(p); }
        }
    }

    private bool Matches(int pixel, PaintColor source, double tolerance)
    {
        int i = pixel * 4;
        double db = Pixels[i] - source.B, dg = Pixels[i + 1] - source.G;
        double dr = Pixels[i + 2] - source.R, da = Pixels[i + 3] - source.A;
        double threshold = Math.Clamp(tolerance, 0, 1) * 510.0;
        return db * db + dg * dg + dr * dr + da * da <= threshold * threshold;
    }

    internal byte[] CopyTile(int tx, int ty)
    {
        int x0 = tx * TileSize, y0 = ty * TileSize;
        int w = Math.Min(TileSize, Width - x0), h = Math.Min(TileSize, Height - y0);
        var data = new byte[w * h * 4];
        for (int y = 0; y < h; y++) Buffer.BlockCopy(Pixels, ((y0 + y) * Width + x0) * 4, data, y * w * 4, w * 4);
        return data;
    }

    internal void RestoreTile(int tx, int ty, byte[] data)
    {
        int x0 = tx * TileSize, y0 = ty * TileSize;
        int w = Math.Min(TileSize, Width - x0), h = Math.Min(TileSize, Height - y0);
        for (int y = 0; y < h; y++) Buffer.BlockCopy(data, y * w * 4, Pixels, ((y0 + y) * Width + x0) * 4, w * 4);
        IsDirty = true;
        MarkPixelsChanged(x0, y0, x0 + w - 1, y0 + h - 1);
    }

    public PaintDirtyRect? ConsumeDirtyRect()
    {
        if (_dirtyMaxX < _dirtyMinX || _dirtyMaxY < _dirtyMinY) return null;
        var result = new PaintDirtyRect(_dirtyMinX, _dirtyMinY,
            _dirtyMaxX - _dirtyMinX + 1, _dirtyMaxY - _dirtyMinY + 1);
        _dirtyMinX = _dirtyMinY = int.MaxValue; _dirtyMaxX = _dirtyMaxY = -1;
        return result;
    }

    private void MarkPixelsChanged(int x0, int y0, int x1, int y1)
    {
        _dirtyMinX = Math.Min(_dirtyMinX, Math.Clamp(x0, 0, Width - 1));
        _dirtyMinY = Math.Min(_dirtyMinY, Math.Clamp(y0, 0, Height - 1));
        _dirtyMaxX = Math.Max(_dirtyMaxX, Math.Clamp(x1, 0, Width - 1));
        _dirtyMaxY = Math.Max(_dirtyMaxY, Math.Clamp(y1, 0, Height - 1));
    }

    private static byte Mix(byte from, byte to, double amount) =>
        (byte)Math.Clamp(Math.Round(from + (to - from) * amount), 0, 255);

    private static void RgbToHsl(byte r8, byte g8, byte b8, out double h, out double s, out double l)
    {
        double r = r8 / 255d, g = g8 / 255d, b = b8 / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2; double d = max - min;
        if (d == 0) { h = s = 0; return; }
        s = d / (1 - Math.Abs(2 * l - 1));
        h = max == r ? ((g - b) / d) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h = (h * 60 + 360) % 360;
    }

    private static void HslToRgb(double h, double s, double l, out byte r, out byte g, out byte b)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = l - c / 2;
        (double rr, double gg, double bb) = h switch
        { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        r = (byte)Math.Round((rr + m) * 255); g = (byte)Math.Round((gg + m) * 255); b = (byte)Math.Round((bb + m) * 255);
    }
}

public readonly record struct PaintDirtyRect(int X, int Y, int Width, int Height);
