using ReTex.Core.Paint;

namespace ReTex.Core.Tests;

public sealed class PaintEngineTests
{
    [Fact] public void BrushAndUndoRestoreChangedTiles()
    {
        var document = new PaintDocument("test.paa", 64, 64, Enumerable.Repeat((byte)255, 64 * 64 * 4).ToArray());
        var history = new PaintHistory();
        using (var tx = history.Begin("stroke"))
        {
            document.Stamp(32, 32, new PaintColor(0, 0, 255), new PaintBrushSettings(16, 1, 1, 1), false, tx);
            tx.Commit();
        }
        Assert.Equal((byte)255, document.Sample(32, 32).R);
        Assert.Equal((byte)0, document.Sample(32, 32).G);
        history.Undo();
        Assert.Equal(new PaintColor(255, 255, 255, 255), document.Sample(32, 32));
        history.Redo();
        Assert.Equal((byte)255, document.Sample(32, 32).R);
    }

    [Fact] public void ContiguousSelectionDoesNotCrossDifferentColor()
    {
        var pixels = new byte[8 * 4 * 4];
        for (int p = 0; p < 32; p++) { pixels[p * 4] = (byte)(p % 8 < 4 ? 20 : 220); pixels[p * 4 + 3] = 255; }
        var document = new PaintDocument("test.paa", 8, 4, pixels);
        document.SelectByColor(1, 1, .01, PaintMatchMode.Contiguous);
        Assert.True(document.Selection[9]); Assert.False(document.Selection[14]);
    }

    [Fact] public void ColorizeRetainsShadingAndAlpha()
    {
        var pixels = new byte[4 * 4 * 4]; pixels[0] = 50; pixels[1] = 100; pixels[2] = 150; pixels[3] = 255;
        var document = new PaintDocument("test.paa", 4, 4, pixels); var history = new PaintHistory();
        using var tx = history.Begin("colorize");
        document.ApplyColorOperation(0, 0, new PaintColor(255, 0, 0), 0, PaintMatchMode.Global, PaintTool.Colorize, tx); tx.Commit();
        var result = document.Sample(0, 0); Assert.True(result.B > result.R); Assert.Equal((byte)255, result.A);
    }

    [Fact] public void TextureTintPreservesWearContrastAndAlpha()
    {
        byte[] pixels =
        {
            30, 30, 30, 100,
            190, 190, 190, 220,
        };
        var document = new PaintDocument("tint.paa", 2, 1, pixels);
        var history = new PaintHistory(); using var transaction = history.Begin("texture tint");
        document.ApplyColorOperation(0, 0, new PaintColor(20, 40, 220), 1,
            PaintMatchMode.Global, PaintTool.TextureTint, transaction,
            strength: 0.8);

        var darkWear = document.Sample(0, 0);
        var lightPaint = document.Sample(1, 0);
        Assert.Equal((byte)100, darkWear.A);
        Assert.Equal((byte)220, lightPaint.A);
        Assert.True(lightPaint.R - darkWear.R > 100);
        Assert.True(lightPaint.G - darkWear.G > 30);
    }

    [Fact] public void ZeroStrengthTextureTintLeavesPixelsUnchanged()
    {
        var original = new PaintColor(40, 80, 120, 170);
        var document = SolidDocument(4, 4, original);
        var history = new PaintHistory(); using var transaction = history.Begin("texture tint");
        document.ApplyColorOperation(0, 0, new PaintColor(220, 10, 30), 0,
            PaintMatchMode.Global, PaintTool.TextureTint, transaction,
            strength: 0);
        transaction.Commit();

        Assert.Equal(original, document.Sample(0, 0));
        Assert.False(history.CanUndo);
    }

    [Fact] public void PaintingIdenticalColorDoesNotDirtyOrCreateHistory()
    {
        var color = new PaintColor(40, 80, 120, 255);
        var document = SolidDocument(128, 128, color);
        var history = new PaintHistory();
        using (var transaction = history.Begin("no-op stroke"))
        {
            document.Stamp(64, 64, color, new PaintBrushSettings(64, 0.5, 1, 1), false, transaction);
            transaction.Commit();
        }

        Assert.Null(document.ConsumeDirtyRect());
        Assert.False(document.IsDirty);
        Assert.False(history.CanUndo);
    }

    [Fact] public void OneTransactionUndoesMultipleTextures()
    {
        var a = new PaintDocument("a.paa", 4, 4, Enumerable.Repeat((byte)255, 64).ToArray());
        var b = new PaintDocument("b.paa", 4, 4, Enumerable.Repeat((byte)255, 64).ToArray());
        var history = new PaintHistory();
        using (var tx = history.Begin("projected stroke"))
        {
            var brush = new PaintBrushSettings(3, 1, 1, 1);
            a.Stamp(2, 2, new PaintColor(0, 0, 255), brush, false, tx);
            b.Stamp(2, 2, new PaintColor(0, 0, 255), brush, false, tx);
            tx.Commit();
        }
        Assert.Equal(2, history.Undo().Count);
        Assert.Equal(new PaintColor(255, 255, 255, 255), a.Sample(2, 2));
        Assert.Equal(new PaintColor(255, 255, 255, 255), b.Sample(2, 2));
    }

    [Fact] public void SmallBrushReportsOnlyATightDirtyRectangle()
    {
        var document = new PaintDocument("large.paa", 1024, 1024, new byte[1024 * 1024 * 4]);
        var history = new PaintHistory(); using var tx = history.Begin("stroke");
        document.Stamp(512, 512, new PaintColor(0, 0, 255), new PaintBrushSettings(16, 1, 1, 1), false, tx);
        var dirty = Assert.IsType<PaintDirtyRect>(document.ConsumeDirtyRect());
        Assert.InRange(dirty.Width, 16, 20); Assert.InRange(dirty.Height, 16, 20);
    }

    [Fact] public void ColorSelectionCanAddAndSubtractMatches()
    {
        var pixels = new byte[4 * 4 * 4];
        for (int p = 0; p < 16; p++)
        {
            pixels[p * 4 + 2] = (byte)(p % 4 == 0 ? 255 : 0);
            pixels[p * 4 + 1] = (byte)(p % 4 == 1 ? 255 : 0);
            pixels[p * 4 + 3] = 255;
        }
        var document = new PaintDocument("selection.paa", 4, 4, pixels);
        document.SelectByColor(0, 0, 0, PaintMatchMode.Global);
        document.SelectByColor(1, 0, 0, PaintMatchMode.Global, PaintSelectionCombine.Add);
        Assert.True(document.Selection[0]); Assert.True(document.Selection[1]);
        document.SelectByColor(0, 0, 0, PaintMatchMode.Global, PaintSelectionCombine.Subtract);
        Assert.False(document.Selection[0]); Assert.True(document.Selection[1]);
    }

    [Fact] public void CancelledColorOperationRollsBackChangedTilesAndDirtyState()
    {
        const int size = 256;
        var pixels = new byte[size * size * 4];
        for (int p = 0; p < size * size; p++) pixels[p * 4 + 3] = 255;
        var document = new PaintDocument("cancel.paa", size, size, pixels);
        var history = new PaintHistory();
        using var cancellation = new CancellationTokenSource();
        using var transaction = history.Begin("replace");

        Assert.Throws<OperationCanceledException>(() => document.ApplyColorOperation(0, 0,
            new PaintColor(20, 40, 60), 0, PaintMatchMode.Global, PaintTool.Replace, transaction,
            cancellation.Token, progress => { if (progress > 0.5) cancellation.Cancel(); }));
        transaction.Rollback();

        Assert.Equal(new PaintColor(0, 0, 0, 255), document.Sample(0, 0));
        Assert.Equal(new PaintColor(0, 0, 0, 255), document.Sample(size - 1, size - 1));
        Assert.False(document.IsDirty);
        Assert.False(history.CanUndo);
    }

    [Fact] public void BrushOpacityAndHardnessProduceSoftAntialiasedFalloff()
    {
        var document = SolidDocument(32, 32, new PaintColor(0, 0, 0));
        var history = new PaintHistory(); using var transaction = history.Begin("soft brush");
        document.Stamp(16, 16, new PaintColor(255, 255, 255),
            new PaintBrushSettings(20, 0, 0.5, 1), false, transaction);

        byte center = document.Sample(16, 16).R;
        byte edge = document.Sample(24, 16).R;
        Assert.InRange(center, (byte)115, (byte)130);
        Assert.InRange(edge, (byte)10, (byte)40);
        Assert.True(center > edge);
    }

    [Fact] public void EraserRespectsOpacity()
    {
        var document = SolidDocument(16, 16, new PaintColor(20, 30, 40, 255));
        var history = new PaintHistory(); using var transaction = history.Begin("erase");
        document.Stamp(8, 8, default, new PaintBrushSettings(8, 1, 0.5, 1), true, transaction);
        Assert.InRange(document.Sample(8, 8).A, (byte)125, (byte)130);
    }

    [Fact] public void ToleranceAndSelectionLimitGlobalReplacement()
    {
        var pixels = new byte[4 * 4 * 4];
        for (int p = 0; p < 16; p++)
        {
            pixels[p * 4 + 2] = (byte)(p < 8 ? 100 : 220);
            pixels[p * 4 + 3] = 255;
        }
        var document = new PaintDocument("replace.paa", 4, 4, pixels);
        document.SelectByColor(0, 0, 0, PaintMatchMode.Global);
        var history = new PaintHistory(); using var transaction = history.Begin("replace");
        document.ApplyColorOperation(0, 0, new PaintColor(10, 20, 30), 0.3,
            PaintMatchMode.Global, PaintTool.Replace, transaction);

        Assert.Equal((byte)30, document.Sample(0, 0).R);
        Assert.Equal((byte)220, document.Sample(0, 3).R);
    }

    [Fact] public void CancelledSelectionLeavesPreviousMaskIntact()
    {
        const int size = 256;
        var pixels = new byte[size * size * 4];
        for (int p = 0; p < size * size; p++)
        {
            pixels[p * 4 + 2] = (byte)(p < pixels.Length / 8 ? 255 : 0);
            pixels[p * 4 + 1] = (byte)(p < pixels.Length / 8 ? 0 : 255);
            pixels[p * 4 + 3] = 255;
        }
        var document = new PaintDocument("selection-cancel.paa", size, size, pixels);
        document.SelectByColor(0, 0, 0, PaintMatchMode.Global);
        using var cancellation = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() => document.SelectByColor(0, size - 1, 0,
            PaintMatchMode.Global, PaintSelectionCombine.Replace, cancellation.Token,
            progress => { if (progress > 0.84) cancellation.Cancel(); }));

        Assert.True(document.Selection[0]);
        Assert.False(document.Selection[^1]);
    }

    [Fact] public void HistoryCapsActionsAndTracksBytesAcrossUndoRedo()
    {
        var document = SolidDocument(64, 64, new PaintColor(0, 0, 0));
        var history = new PaintHistory { MaxActions = 2 };
        for (byte value = 1; value <= 3; value++)
        {
            using var transaction = history.Begin($"stroke {value}");
            document.Stamp(32, 32, new PaintColor(value, value, value),
                new PaintBrushSettings(4, 1, 1, 1), false, transaction);
            transaction.Commit();
        }

        Assert.Equal(2, history.UndoCount);
        Assert.True(history.EstimatedBytes > 0);
        history.Undo(); history.Undo();
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(2, history.RedoCount);
        Assert.Equal(0, history.EstimatedBytes);
        history.Redo();
        Assert.Equal(1, history.UndoCount);
        Assert.True(history.EstimatedBytes > 0);
    }

    [Fact] public void StrokeSamplerIncludesEndpointWithoutExceedingSpacing()
    {
        PaintPoint[] points = PaintStrokeSampler.Between(0, 0, 10, 0, 3).ToArray();
        Assert.Equal(new PaintPoint(10, 0), points[^1]);
        double previous = 0;
        foreach (var point in points)
        {
            Assert.InRange(point.X - previous, 0.01, 3);
            previous = point.X;
        }
    }

    [Fact] public void FourKLongStrokeUsesTileHistoryInsteadOfFullFrameSnapshots()
    {
        const int size = 4096;
        var document = new PaintDocument("four-k-stroke.paa", size, size, new byte[size * size * 4]);
        var history = new PaintHistory();
        using (var transaction = history.Begin("long diagonal stroke"))
        {
            var brush = new PaintBrushSettings(12, 0.8, 1, 1, 0.2);
            foreach (PaintPoint point in PaintStrokeSampler.Between(20, 20, size - 20, size - 20, 6))
                document.Stamp(point.X, point.Y, new PaintColor(10, 80, 220), brush, false, transaction);
            transaction.Commit();
        }

        Assert.True(history.CanUndo);
        Assert.InRange(history.EstimatedBytes, 1, 16L * 1024 * 1024);
        PaintDirtyRect dirty = Assert.IsType<PaintDirtyRect>(document.ConsumeDirtyRect());
        Assert.True(dirty.Width > 4000);
        Assert.True(dirty.Height > 4000);
        history.Undo();
        Assert.Equal(new PaintColor(0, 0, 0, 0), document.Sample(size / 2, size / 2));
    }

    [Fact] public void LargeGlobalReplacementCapturesEachTileOnceAndUndoRestoresPixels()
    {
        const int size = 2048;
        var document = SolidDocument(size, size, new PaintColor(25, 50, 75, 255));
        var history = new PaintHistory();
        var progress = new List<double>();
        using (var transaction = history.Begin("large replace"))
        {
            document.ApplyColorOperation(0, 0, new PaintColor(180, 120, 60, 255), 0,
                PaintMatchMode.Global, PaintTool.Replace, transaction,
                reportProgress: value => progress.Add(value));
            transaction.Commit();
        }

        Assert.Equal(new PaintColor(180, 120, 60, 255), document.Sample(size - 1, size - 1));
        Assert.Equal(32L * 1024 * 1024, history.EstimatedBytes);
        Assert.Equal(1, progress[^1]);
        Assert.True(progress.Zip(progress.Skip(1), (a, b) => b >= a).All(value => value));
        history.Undo();
        Assert.Equal(new PaintColor(25, 50, 75, 255), document.Sample(0, 0));
        Assert.Equal(new PaintColor(25, 50, 75, 255), document.Sample(size - 1, size - 1));
    }

    [Fact] public void HistoryByteLimitDropsOversizedAction()
    {
        var document = SolidDocument(128, 128, new PaintColor(0, 0, 0, 255));
        var history = new PaintHistory { MaxBytes = 16 * 1024 };
        using (var transaction = history.Begin("oversized stroke"))
        {
            document.Stamp(64, 64, new PaintColor(255, 255, 255),
                new PaintBrushSettings(100, 1, 1, 1), false, transaction);
            transaction.Commit();
        }

        Assert.False(history.CanUndo);
        Assert.Equal(0, history.EstimatedBytes);
    }

    private static PaintDocument SolidDocument(int width, int height, PaintColor color)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = color.A; }
        return new PaintDocument("solid.paa", width, height, pixels);
    }
}
