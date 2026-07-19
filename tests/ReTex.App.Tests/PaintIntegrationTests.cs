using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using ReTex.App;
using ReTex.App.ViewModels;
using ReTex.Core.P3d;
using ReTex.Core.Paa;
using ReTex.Core.Paint;

namespace ReTex.App.Tests;

public sealed class PaintIntegrationTests
{
    [Fact] public void SidebarPanelsFollowTheActiveToolAndLayout() => RunSta(() =>
    {
        using var owner = new MainViewModel(new AppSettings { WorkshopPath = "" },
            persistSettings: false, scanOnStart: false);
        PaintSessionViewModel paint = owner.Paint;

        paint.Tool = PaintTool.Brush;
        Assert.True(paint.UsesBrushControls);
        Assert.True(paint.UsesColorControls);
        Assert.False(paint.UsesColorMatchControls);
        Assert.False(paint.UsesTintControls);

        paint.Tool = PaintTool.MagicWand;
        Assert.False(paint.UsesBrushControls);
        Assert.False(paint.UsesColorControls);
        Assert.True(paint.UsesColorMatchControls);
        Assert.True(paint.ShowsMagicWandHelp);

        paint.Tool = PaintTool.TextureTint;
        Assert.True(paint.UsesColorControls);
        Assert.True(paint.UsesColorMatchControls);
        Assert.True(paint.UsesTintControls);
        Assert.False(paint.ShowsMagicWandHelp);

        paint.Layout = "2D";
        Assert.False(paint.Shows3DNavigation);
        paint.Layout = "Split";
        Assert.True(paint.Shows3DNavigation);
    });

    [Fact] public void BrushPresetsSaveOverwriteApplyDeleteAndReload() => RunSta(() =>
    {
        var settings = new AppSettings { WorkshopPath = "" };
        using var owner = new MainViewModel(settings, persistSettings: false, scanOnStart: false);
        PaintSessionViewModel paint = owner.Paint;

        Assert.False(paint.SaveBrushPresetCommand.CanExecute(null));
        Assert.False(paint.ApplySelectedBrushPresetCommand.CanExecute(null));
        Assert.False(paint.DeleteBrushPresetCommand.CanExecute(null));

        paint.BrushPresetName = "  Detail   Brush  ";
        paint.BrushSize = 19;
        paint.Hardness = 0.82;
        paint.Opacity = 0.71;
        paint.Flow = 0.63;
        paint.Spacing = 0.14;
        paint.SaveBrushPresetCommand.Execute(null);

        PaintBrushPresetViewModel saved = Assert.Single(paint.BrushPresets);
        Assert.Equal("Detail Brush", saved.Name);
        Assert.True(paint.ApplySelectedBrushPresetCommand.CanExecute(null));
        Assert.True(paint.DeleteBrushPresetCommand.CanExecute(null));

        paint.BrushSize = 41;
        paint.SaveBrushPresetCommand.Execute(null);
        Assert.Single(paint.BrushPresets);
        Assert.Equal(41, paint.BrushPresets[0].Settings.Size);

        paint.BrushSize = 5;
        paint.ApplySelectedBrushPresetCommand.Execute(null);
        Assert.Equal(41, paint.BrushSize);

        paint.Dispose();
        using var reloadedOwner = new MainViewModel(settings, persistSettings: false, scanOnStart: false);
        PaintSessionViewModel reloaded = reloadedOwner.Paint;
        Assert.Equal("Detail Brush", Assert.Single(reloaded.BrushPresets).Name);
        Assert.Equal(41, reloaded.BrushPresets[0].Settings.Size);

        reloaded.SelectedBrushPreset = reloaded.BrushPresets[0];
        reloaded.DeleteBrushPresetCommand.Execute(null);
        Assert.Empty(reloaded.BrushPresets);
        Assert.False(reloaded.DeleteBrushPresetCommand.CanExecute(null));
    });

    [Fact] public void DirtyRegionUpdatesExistingBitmapAndLiveMaterialWithoutRebuildingGeometry() => RunSta(() =>
    {
        string path = Path.GetFullPath("paint-integration.paa");
        var pixels = new byte[16 * 16 * 4];
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var texture = new PaintTextureViewModel(new PaintDocument(path, 16, 16, pixels), PaaFormat.Dxt1);
        WriteableBitmap originalBitmap = texture.Bitmap;

        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0) },
            TriangleIndices = new Int32Collection { 0, 1, 2 },
        };
        var initialImage = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 0, 255 }, 4);
        var sourceModel = new GeometryModel3D(mesh, new DiffuseMaterial(new ImageBrush(initialImage)));
        var frozen = new Model3DGroup(); frozen.Children.Add(sourceModel); frozen.Freeze();
        var group = new OdolPreviewGroup
        {
            Texture = new PreviewTexture { SourceVirtualPath = "source.paa", ProjectFilePath = path },
            FaceIndices = new List<int> { 0 },
        };
        var live = ModelViewHelper.ActivateLiveTextures(frozen, new[] { group });
        GeometryModel3D liveModel = Assert.Single(live.Parts.Keys);
        ImageBrush liveBrush = Assert.Single(live.ProjectTextureBrushes[path]);
        ImageBrush selectionBrush = Assert.Single(live.SelectionOverlayBrushes[path]);
        liveBrush.ImageSource = originalBitmap;

        var history = new PaintHistory();
        using (var transaction = history.Begin("integration stroke"))
        {
            texture.Document.Stamp(8, 8, new PaintColor(0, 0, 255),
                new PaintBrushSettings(4, 1, 1, 1), false, transaction);
            transaction.Commit();
        }
        texture.Refresh();

        Assert.Same(originalBitmap, texture.Bitmap);
        Assert.Same(originalBitmap, liveBrush.ImageSource);
        Assert.NotSame(originalBitmap, selectionBrush.ImageSource);
        Assert.Same(mesh, liveModel.Geometry);
        Assert.Same(liveModel, Assert.Single(live.Parts.Keys));
        var painted = new byte[4];
        originalBitmap.CopyPixels(new Int32Rect(8, 8, 1, 1), painted, 4, 0);
        Assert.Equal((byte)255, painted[2]);
    });

    [Fact] public void SelectionOverlayUpdatesLiveMaterialWithoutChangingTextureOrGeometry() => RunSta(() =>
    {
        string path = Path.GetFullPath("paint-3d-selection.paa");
        var pixels = new byte[8 * 8 * 4];
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var texture = new PaintTextureViewModel(new PaintDocument(path, 8, 8, pixels), PaaFormat.Dxt1);
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0) },
            TriangleIndices = new Int32Collection { 0, 1, 2 },
        };
        var initialImage = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 0, 255 }, 4);
        var materials = new MaterialGroup();
        materials.Children.Add(new DiffuseMaterial(new ImageBrush(initialImage)));
        var sourceModel = new GeometryModel3D(mesh, materials);
        var frozen = new Model3DGroup(); frozen.Children.Add(sourceModel); frozen.Freeze();
        var group = new OdolPreviewGroup
        {
            Texture = new PreviewTexture { SourceVirtualPath = "source.paa", ProjectFilePath = path },
            FaceIndices = new List<int> { 0 },
        };
        var live = ModelViewHelper.ActivateLiveTextures(frozen, new[] { group });
        GeometryModel3D liveModel = Assert.Single(live.Parts.Keys);
        ImageBrush baseBrush = Assert.Single(live.ProjectTextureBrushes[path]);
        ImageBrush overlayBrush = Assert.Single(live.SelectionOverlayBrushes[path]);
        baseBrush.ImageSource = texture.Bitmap;
        overlayBrush.ImageSource = texture.SelectionBitmap;

        texture.Document.SelectByColor(0, 0, 0, PaintMatchMode.Global);
        texture.RefreshSelection();

        var selected = new byte[4];
        texture.SelectionBitmap.CopyPixels(new Int32Rect(4, 4, 1, 1), selected, 4, 0);
        Assert.Equal((byte)54, selected[3]);
        Assert.Same(texture.SelectionBitmap, overlayBrush.ImageSource);
        Assert.Same(texture.Bitmap, baseBrush.ImageSource);
        Assert.Same(mesh, liveModel.Geometry);
        Assert.False(texture.Document.IsDirty);
    });

    [Fact] public void ProjectedStrokeAcrossTwoMaterialsUpdatesAndUndoesAtomically() => RunSta(() =>
    {
        string firstPath = Path.GetFullPath("paint-projection-first.paa");
        string secondPath = Path.GetFullPath("paint-projection-second.paa");
        var firstPixels = OpaquePixels(16, 16);
        var secondPixels = OpaquePixels(16, 16);
        var first = new PaintTextureViewModel(new PaintDocument(firstPath, 16, 16, firstPixels), PaaFormat.Dxt1);
        var second = new PaintTextureViewModel(new PaintDocument(secondPath, 16, 16, secondPixels), PaaFormat.Dxt1);

        var firstMesh = TriangleMesh(0);
        var secondMesh = TriangleMesh(2);
        var frozen = new Model3DGroup();
        frozen.Children.Add(TexturedModel(firstMesh));
        frozen.Children.Add(TexturedModel(secondMesh));
        frozen.Freeze();
        var groups = new[]
        {
            PreviewGroup(firstPath),
            PreviewGroup(secondPath),
        };
        BuiltPreview live = ModelViewHelper.ActivateLiveTextures(frozen, groups);
        GeometryModel3D[] models = live.Parts.Keys.ToArray();
        Assert.Equal(2, models.Length);
        Assert.Same(firstMesh, models[0].Geometry);
        Assert.Same(secondMesh, models[1].Geometry);
        Assert.Single(live.ProjectTextureBrushes[firstPath]).ImageSource = first.Bitmap;
        Assert.Single(live.ProjectTextureBrushes[secondPath]).ImageSource = second.Bitmap;

        var settings = new AppSettings { WorkshopPath = "" };
        using var owner = new MainViewModel(settings, persistSettings: false, scanOnStart: false);
        owner.Paint.Textures.Add(first);
        owner.Paint.Textures.Add(second);
        owner.Paint.Red = 240;
        owner.Paint.Green = 30;
        owner.Paint.Blue = 20;
        owner.Paint.BeginStroke("two-material projection");
        var projected = new Dictionary<string, IReadOnlyCollection<PaintTexel>>(StringComparer.OrdinalIgnoreCase)
        {
            [firstPath] = new[] { new PaintTexel(4, 5), new PaintTexel(5, 5) },
            [secondPath] = new[] { new PaintTexel(10, 11), new PaintTexel(11, 11) },
        };
        IReadOnlyCollection<PaintTextureViewModel> touched = owner.Paint.StampProjected(projected, 4);
        owner.Paint.EndStroke();

        Assert.Equal(2, touched.Count);
        Assert.NotEqual((byte)0, first.Document.Pixels[(5 * 16 + 4) * 4 + 2]);
        Assert.NotEqual((byte)0, second.Document.Pixels[(11 * 16 + 10) * 4 + 2]);
        Assert.True(owner.Paint.CanUndo);

        owner.Paint.UndoCommand.Execute(null);
        Assert.Equal((byte)0, first.Document.Pixels[(5 * 16 + 4) * 4 + 2]);
        Assert.Equal((byte)0, second.Document.Pixels[(11 * 16 + 10) * 4 + 2]);
        Assert.False(owner.Paint.CanUndo);
        Assert.True(owner.Paint.CanRedo);
        Assert.Same(first.Bitmap, Assert.Single(live.ProjectTextureBrushes[firstPath]).ImageSource);
        Assert.Same(second.Bitmap, Assert.Single(live.ProjectTextureBrushes[secondPath]).ImageSource);
        Assert.Same(firstMesh, models[0].Geometry);
        Assert.Same(secondMesh, models[1].Geometry);

        static byte[] OpaquePixels(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return pixels;
        }

        static MeshGeometry3D TriangleMesh(double x) => new()
        {
            Positions = new Point3DCollection { new(x, 0, 0), new(x + 1, 0, 0), new(x, 1, 0) },
            TriangleIndices = new Int32Collection { 0, 1, 2 },
        };

        static GeometryModel3D TexturedModel(MeshGeometry3D mesh)
        {
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 0, 0, 0, 255 }, 4);
            var material = new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(new ImageBrush(image)));
            return new GeometryModel3D(mesh, material);
        }

        static OdolPreviewGroup PreviewGroup(string path) => new()
        {
            Texture = new PreviewTexture { SourceVirtualPath = Path.GetFileName(path), ProjectFilePath = path },
            FaceIndices = new List<int> { 0 },
        };
    });

    [Fact] public void SelectionRefreshBuildsFillAndCrispBoundaryOverlay() => RunSta(() =>
    {
        string path = Path.GetFullPath("paint-selection-overlay.paa");
        var pixels = new byte[4 * 4 * 4];
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var texture = new PaintTextureViewModel(new PaintDocument(path, 4, 4, pixels), PaaFormat.Dxt1);

        texture.Document.SelectByColor(0, 0, 0, PaintMatchMode.Global);
        texture.RefreshSelection();

        var fill = new byte[4];
        var edge = new byte[4];
        var center = new byte[4];
        texture.SelectionBitmap.CopyPixels(new Int32Rect(1, 1, 1, 1), fill, 4, 0);
        texture.SelectionOutlineBitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), edge, 4, 0);
        texture.SelectionOutlineBitmap.CopyPixels(new Int32Rect(1, 1, 1, 1), center, 4, 0);

        Assert.Equal((byte)54, fill[3]);
        Assert.Equal((byte)235, edge[3]);
        Assert.Equal((byte)0, center[3]);

        byte firstEdgeValue = edge[0];
        texture.RefreshSelectionOutline(4);
        texture.SelectionOutlineBitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), edge, 4, 0);
        Assert.NotEqual(firstEdgeValue, edge[0]);
        Assert.Equal((byte)235, edge[3]);
    });

    [Fact] public void LargeSelectionKeepsStaticOverlayWithoutAnimationChurn() => RunSta(() =>
    {
        const int width = 1025, height = 1024;
        var pixels = new byte[width * height * 4];
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var texture = new PaintTextureViewModel(
            new PaintDocument(Path.GetFullPath("paint-large-selection.paa"), width, height, pixels),
            PaaFormat.Dxt1);

        texture.Document.SelectByColor(0, 0, 0, PaintMatchMode.Contiguous);
        texture.RefreshSelection();

        Assert.False(texture.CanAnimateSelectionOutline);
        var fill = new byte[4];
        var edge = new byte[4];
        texture.SelectionBitmap.CopyPixels(new Int32Rect(width / 2, height / 2, 1, 1), fill, 4, 0);
        texture.SelectionOutlineBitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), edge, 4, 0);
        Assert.Equal((byte)54, fill[3]);
        Assert.Equal((byte)235, edge[3]);
    });

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
