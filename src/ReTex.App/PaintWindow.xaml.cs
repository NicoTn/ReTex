using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using ReTex.App.ViewModels;
using ReTex.Core.Paint;

namespace ReTex.App;

public partial class PaintWindow : Window
{
    private bool _stroke2D, _stroke3D, _panning, _rotating3D, _pickingColor, _pickingHue;
    private Point _last2D, _last3D, _panStart;
    private Point _cursor2D, _cursor3D;
    private double _panX, _panY;
    private Point? _pending2D, _pending3D;
    private PaintTextureViewModel? _stroke2DTexture;
    private readonly System.Windows.Threading.DispatcherTimer _paintFrameTimer;
    private readonly HashSet<Key> _navigationKeys = new();

    public PaintWindow(MainViewModel viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
        RestoreWindowState();
        viewModel.Paint.BindToPreview();
        _paintFrameTimer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(16), System.Windows.Threading.DispatcherPriority.Render,
            (_, _) => { FlushPaintFrame(); MoveCameraFrame(); }, Dispatcher);
        _paintFrameTimer.Stop();
        Closed += (_, _) => { SaveWindowState(); _paintFrameTimer.Stop(); };
        Deactivated += (_, _) => { _navigationKeys.Clear(); StopPaintFramesIfIdle(); };
    }

    private MainViewModel Vm => (MainViewModel)DataContext;
    private static bool IsStrokeTool(PaintTool tool) => tool is PaintTool.Brush or PaintTool.Eraser;
    private static PaintSelectionCombine SelectionCombine =>
        (Keyboard.Modifiers & ModifierKeys.Alt) != 0 ? PaintSelectionCombine.Subtract :
        (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? PaintSelectionCombine.Add : PaintSelectionCombine.Replace;
    private static bool Inside(PaintTextureViewModel texture, Point p) => p.X >= 0 && p.Y >= 0 && p.X < texture.Document.Width && p.Y < texture.Document.Height;

    private void ColorField_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    { _pickingColor = true; ColorField.CaptureMouse(); UpdateColorField(e.GetPosition(ColorField)); e.Handled = true; }

    private void ColorField_MouseMove(object sender, MouseEventArgs e)
    { if (_pickingColor && e.LeftButton == MouseButtonState.Pressed) UpdateColorField(e.GetPosition(ColorField)); }

    private void ColorField_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    { if (!_pickingColor) return; UpdateColorField(e.GetPosition(ColorField)); _pickingColor = false; ColorField.ReleaseMouseCapture(); e.Handled = true; }

    private void UpdateColorField(Point point)
    {
        Vm.Paint.Saturation = Math.Clamp(point.X / Math.Max(1, ColorField.ActualWidth), 0, 1);
        Vm.Paint.Value = 1 - Math.Clamp(point.Y / Math.Max(1, ColorField.ActualHeight), 0, 1);
    }

    private void HueField_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    { _pickingHue = true; HueField.CaptureMouse(); UpdateHueField(e.GetPosition(HueField)); e.Handled = true; }

    private void HueField_MouseMove(object sender, MouseEventArgs e)
    { if (_pickingHue && e.LeftButton == MouseButtonState.Pressed) UpdateHueField(e.GetPosition(HueField)); }

    private void HueField_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    { if (!_pickingHue) return; UpdateHueField(e.GetPosition(HueField)); _pickingHue = false; HueField.ReleaseMouseCapture(); e.Handled = true; }

    private void UpdateHueField(Point point) =>
        Vm.Paint.Hue = Math.Clamp(point.Y / Math.Max(1, HueField.ActualHeight), 0, 1) * 360;

    private async void Paint2D_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var paint = Vm.Paint; var texture = paint.ActiveTexture; if (texture is null || paint.IsBusy) return;
        if (Keyboard.IsKeyDown(Key.Space))
        {
            _panning = true; _panStart = e.GetPosition(Paint2DPanel); _panX = Paint2DTranslate.X; _panY = Paint2DTranslate.Y;
            Paint2DPanel.CaptureMouse(); e.Handled = true; return;
        }
        Point p = e.GetPosition(PaintTextureImage); if (!Inside(texture, p)) return;
        if (!IsStrokeTool(paint.Tool)) { e.Handled = true; await paint.ApplyPointToolAsync(texture, (int)p.X, (int)p.Y, SelectionCombine); return; }
        paint.BeginStroke(); _stroke2D = true; _stroke2DTexture = texture; _last2D = p; _pending2D = null;
        paint.Stamp(texture, p.X, p.Y); texture.Refresh(); StartPaintFrames();
        Paint2DPanel.CaptureMouse(); e.Handled = true;
    }

    private void Paint2D_MouseMove(object sender, MouseEventArgs e)
    {
        _cursor2D = e.GetPosition(PaintTextureImage);
        Update2DBrushCursor(_cursor2D);
        if (_panning)
        {
            Point p = e.GetPosition(Paint2DPanel); Paint2DTranslate.X = _panX + p.X - _panStart.X; Paint2DTranslate.Y = _panY + p.Y - _panStart.Y; return;
        }
        if (!_stroke2D || e.LeftButton != MouseButtonState.Pressed) return;
        _pending2D = e.GetPosition(PaintTextureImage); e.Handled = true;
    }

    private void Paint2D_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_stroke2D) { _pending2D = e.GetPosition(PaintTextureImage); Flush2DFrame(); Vm.Paint.EndStroke(); }
        _stroke2D = _panning = false; _stroke2DTexture = null; StopPaintFramesIfIdle(); Paint2DPanel.ReleaseMouseCapture();
    }

    private void Paint2D_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var paint = Vm.Paint; var texture = paint.ActiveTexture; if (texture is null || paint.IsBusy) return;
        Point p = e.GetPosition(PaintTextureImage); if (Inside(texture, p)) paint.Sample(texture, (int)p.X, (int)p.Y); e.Handled = true;
    }

    private void Paint2D_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double old = Paint2DScale.ScaleX <= 0 ? 1 : Paint2DScale.ScaleX;
        double next = Math.Clamp(old * (e.Delta > 0 ? 1.15 : 1 / 1.15), 1, 16); Point c = e.GetPosition(Paint2DPanel);
        Paint2DTranslate.X = c.X - next / old * (c.X - Paint2DTranslate.X); Paint2DTranslate.Y = c.Y - next / old * (c.Y - Paint2DTranslate.Y);
        Paint2DScale.ScaleX = Paint2DScale.ScaleY = next; e.Handled = true;
    }

    private void Update2DBrushCursor(Point point)
    {
        var texture = Vm.Paint.ActiveTexture;
        bool show = texture is not null && IsStrokeTool(Vm.Paint.Tool) && !Keyboard.IsKeyDown(Key.Space) && Inside(texture, point);
        Paint2DBrushCursorOuter.Visibility = Paint2DBrushCursorInner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Paint2DPanel.Cursor = show ? Cursors.None : null;
        if (!show) return;
        double radius = Vm.Paint.BrushSize / 2;
        Canvas.SetLeft(Paint2DBrushCursorOuter, point.X - radius); Canvas.SetTop(Paint2DBrushCursorOuter, point.Y - radius);
        Canvas.SetLeft(Paint2DBrushCursorInner, point.X - radius); Canvas.SetTop(Paint2DBrushCursorInner, point.Y - radius);
    }

    private void Paint2D_MouseLeave(object sender, MouseEventArgs e)
    {
        Paint2DBrushCursorOuter.Visibility = Paint2DBrushCursorInner.Visibility = Visibility.Collapsed;
        Paint2DPanel.Cursor = null;
    }

    private async void Paint3D_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        PaintViewport.Focus();
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 && Vm.Paint.Tool != PaintTool.MagicWand) return;
        var paint = Vm.Paint; if (paint.IsBusy) return; Point p = e.GetPosition(PaintViewport);
        if (!IsStrokeTool(paint.Tool)) { e.Handled = true; await Apply3DPointAsync(p, paint.Tool); return; }
        paint.BeginStroke(); _stroke3D = true; _last3D = p; _pending3D = null;
        var touched = new HashSet<PaintTextureViewModel>(); ProjectDisk(p, touched); Refresh(touched);
        StartPaintFrames(); PaintViewport.CaptureMouse(); e.Handled = true;
    }

    private void Paint3D_MouseMove(object sender, MouseEventArgs e)
    {
        _cursor3D = e.GetPosition(PaintViewport);
        Update3DBrushCursor(_cursor3D);
        if (!_stroke3D || e.LeftButton != MouseButtonState.Pressed) return;
        _pending3D = e.GetPosition(PaintViewport); e.Handled = true;
    }

    private void Paint3D_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_stroke3D) { _pending3D = e.GetPosition(PaintViewport); Flush3DFrame(); Vm.Paint.EndStroke(); }
        _stroke3D = false; StopPaintFramesIfIdle(); PaintViewport.ReleaseMouseCapture();
    }
    private async void Paint3D_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        PaintViewport.Focus();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (Vm.Paint.IsBusy) return;
            e.Handled = true; await Apply3DPointAsync(e.GetPosition(PaintViewport), PaintTool.Eyedropper); return;
        }
        _rotating3D = true;
        Paint3DBrushCursorOuter.Visibility = Paint3DBrushCursorInner.Visibility = Visibility.Collapsed;
        PaintViewport.Cursor = null;
        // Leave the event unhandled so Helix receives its normal viewer rotation gesture.
    }

    private void Paint3D_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    { _rotating3D = false; Update3DBrushCursor(e.GetPosition(PaintViewport)); }

    private void Update3DBrushCursor(Point point)
    {
        bool show = IsStrokeTool(Vm.Paint.Tool) && (Keyboard.Modifiers & ModifierKeys.Alt) == 0 && !_rotating3D;
        Paint3DBrushCursorOuter.Visibility = Paint3DBrushCursorInner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PaintViewport.Cursor = show ? Cursors.None : null;
        if (!show) return;
        double radius = Vm.Paint.BrushSize / 2;
        Canvas.SetLeft(Paint3DBrushCursorOuter, point.X - radius); Canvas.SetTop(Paint3DBrushCursorOuter, point.Y - radius);
        Canvas.SetLeft(Paint3DBrushCursorInner, point.X - radius); Canvas.SetTop(Paint3DBrushCursorInner, point.Y - radius);
    }

    private void Paint3D_MouseLeave(object sender, MouseEventArgs e)
    {
        if (e.RightButton != MouseButtonState.Pressed) _rotating3D = false;
        Paint3DBrushCursorOuter.Visibility = Paint3DBrushCursorInner.Visibility = Visibility.Collapsed;
        PaintViewport.Cursor = null;
    }

    private async Task Apply3DPointAsync(Point point, PaintTool requested)
    {
        if (!TryHit(point, out var texture, out double x, out double y) || texture is null) return;
        if (requested == PaintTool.Eyedropper) Vm.Paint.Sample(texture, (int)x, (int)y);
        else await Vm.Paint.ApplyPointToolAsync(texture, (int)x, (int)y, SelectionCombine);
    }

    private void FlushPaintFrame()
    {
        Flush2DFrame(); Flush3DFrame();
    }

    private void Flush2DFrame()
    {
        if (!_stroke2D || _pending2D is not { } target || _stroke2DTexture is not { } texture) return;
        _pending2D = null;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var paint = Vm.Paint;
        double step = Math.Max(1, paint.BrushSize * Math.Max(.02, paint.Spacing));
        foreach (var point in PaintStrokeSampler.Between(_last2D.X, _last2D.Y, target.X, target.Y, step))
            paint.Stamp(texture, point.X, point.Y);
        _last2D = target; texture.Refresh(); timer.Stop(); paint.ReportLatency(timer.Elapsed);
    }

    private void Flush3DFrame()
    {
        if (!_stroke3D || _pending3D is not { } target) return;
        _pending3D = null;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var paint = Vm.Paint;
        // Consecutive projected disks only need to overlap; denser centers repeat expensive raycasts.
        double step = Math.Max(2, Math.Max(paint.BrushSize * Math.Max(.05, paint.Spacing), paint.BrushSize * 0.5));
        var touched = new HashSet<PaintTextureViewModel>();
        foreach (var point in PaintStrokeSampler.Between(_last3D.X, _last3D.Y, target.X, target.Y, step))
            ProjectDisk(new Point(point.X, point.Y), touched);
        _last3D = target; Refresh(touched); timer.Stop(); paint.ReportLatency(timer.Elapsed);
    }

    private void StartPaintFrames()
    { if (!_paintFrameTimer.IsEnabled) _paintFrameTimer.Start(); }

    private void StopPaintFramesIfIdle()
    { if (!_stroke2D && !_stroke3D && _navigationKeys.Count == 0) _paintFrameTimer.Stop(); }

    private void MoveCameraFrame()
    {
        if (_navigationKeys.Count == 0 || PaintViewport.Camera is not ProjectionCamera camera) return;
        Vector3D forward = camera.LookDirection, up = camera.UpDirection;
        double distance = forward.Length;
        if (distance < 1e-9 || up.Length < 1e-9) return;
        forward.Normalize(); up.Normalize();
        Vector3D right = Vector3D.CrossProduct(forward, up);
        if (right.Length < 1e-9) return;
        right.Normalize();

        Vector3D movement = new();
        if (_navigationKeys.Contains(Key.W)) movement += forward;
        if (_navigationKeys.Contains(Key.S)) movement -= forward;
        if (_navigationKeys.Contains(Key.D)) movement += right;
        if (_navigationKeys.Contains(Key.A)) movement -= right;
        if (_navigationKeys.Contains(Key.E)) movement += up;
        if (_navigationKeys.Contains(Key.Q)) movement -= up;
        if (movement.Length < 1e-9) return;
        movement.Normalize();

        double speed = Math.Max(0.002, distance * 0.01) * Vm.Paint.CameraMoveSpeed;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) speed *= 4;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) speed *= 0.25;
        camera.Position += movement * speed;
    }

    private static void Refresh(IEnumerable<PaintTextureViewModel> textures)
    { foreach (var texture in textures) texture.Refresh(); }

    private void ProjectDisk(Point center, ISet<PaintTextureViewModel> touched)
    {
        var paint = Vm.Paint; double radius = Math.Max(1, paint.BrushSize / 2);
        // Sparse screen samples are expanded in texture space below, keeping projection near one frame.
        double step = Math.Max(4, radius / 3);
        var hits = PaintProjection.ProjectDiskToTexels(center.X, center.Y, radius, step, sample =>
        {
            return TryHit(new Point(sample.X, sample.Y), out var texture, out double x, out double y) && texture is not null
                ? new PaintProjectedTexel(texture.Path, (int)x, (int)y)
                : null;
        });
        foreach (var texture in paint.StampProjected(hits, step)) touched.Add(texture);
    }

    private bool TryHit(Point point, out PaintTextureViewModel? texture, out double x, out double y)
    {
        texture = null; x = y = 0; var mesh = Vm.CachedMesh; var parts = Vm.PreviewPaintParts; if (mesh is null || parts is null) return false;
        try
        {
            var hit = Viewport3DHelper.FindHits(PaintViewport.Viewport, point).FirstOrDefault(h => h.Model is GeometryModel3D gm && parts.ContainsKey(gm));
            if (hit?.RayHit is not RayMeshGeometry3DHitTestResult rh || hit.Model is not GeometryModel3D model || !parts.TryGetValue(model, out var group)) return false;
            texture = Vm.Paint.Find(group.Texture.ProjectFilePath); if (texture is null) return false;
            int i1 = rh.VertexIndex1, i2 = rh.VertexIndex2, i3 = rh.VertexIndex3;
            if (!PaintProjection.TryInterpolateUv(mesh.Uv, i1, i2, i3, rh.VertexWeight1,
                rh.VertexWeight2, rh.VertexWeight3, out var uv)) return false;
            var texel = uv.ToTexel(texture.Document.Width, texture.Document.Height);
            x = texel.X; y = texel.Y; return true;
        }
        catch { return false; }
    }

    private async void Paint_KeyDown(object sender, KeyEventArgs e)
    {
        var paint = Vm.Paint;
        if (paint.IsBusy) { if (e.Key == Key.Escape) paint.CancelOperationCommand.Execute(null); return; }
        if (Keyboard.FocusedElement is TextBox or ComboBox) return;
        if (PaintViewport.IsKeyboardFocusWithin && paint.Layout != "2D" && IsNavigationKey(e.Key)
            && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            _navigationKeys.Add(e.Key); StartPaintFrames(); e.Handled = true; return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key == Key.S) { e.Handled = true; await paint.SaveAsync(); }
            else if (e.Key == Key.Z && paint.UndoCommand.CanExecute(null)) { paint.UndoCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Y && paint.RedoCommand.CanExecute(null)) { paint.RedoCommand.Execute(null); e.Handled = true; }
        }
        else if (e.Key == Key.OemOpenBrackets) { paint.BrushSize = Math.Max(1, paint.BrushSize - 2); Update2DBrushCursor(_cursor2D); Update3DBrushCursor(_cursor3D); e.Handled = true; }
        else if (e.Key == Key.OemCloseBrackets) { paint.BrushSize = Math.Min(256, paint.BrushSize + 2); Update2DBrushCursor(_cursor2D); Update3DBrushCursor(_cursor3D); e.Handled = true; }
        else if (e.Key == Key.B) { paint.Tool = PaintTool.Brush; e.Handled = true; }
        else if (e.Key == Key.E) { paint.Tool = PaintTool.Eraser; e.Handled = true; }
        else if (e.Key == Key.I) { paint.Tool = PaintTool.Eyedropper; e.Handled = true; }
        else if (e.Key == Key.G) { paint.Tool = PaintTool.Fill; e.Handled = true; }
        else if (e.Key == Key.W) { paint.Tool = PaintTool.MagicWand; e.Handled = true; }
        else if (e.Key == Key.R) { paint.Tool = PaintTool.Replace; e.Handled = true; }
        else if (e.Key == Key.C) { paint.Tool = PaintTool.Colorize; e.Handled = true; }
        else if (e.Key == Key.T) { paint.Tool = PaintTool.TextureTint; e.Handled = true; }
        else if (e.Key == Key.X) { paint.SwapColorsCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.D) { paint.ResetColorsCommand.Execute(null); e.Handled = true; }
    }

    private void Paint_KeyUp(object sender, KeyEventArgs e)
    {
        if (!IsNavigationKey(e.Key)) return;
        _navigationKeys.Remove(e.Key); StopPaintFramesIfIdle(); e.Handled = true;
    }

    private static bool IsNavigationKey(Key key) => key is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E;

    private void RestoreWindowState()
    {
        var s = Vm.Settings;
        if (s.PaintWindowWidth >= MinWidth && s.PaintWindowHeight >= MinHeight)
        {
            Width = s.PaintWindowWidth;
            Height = s.PaintWindowHeight;
        }
        if (!double.IsNaN(s.PaintWindowLeft) && !double.IsNaN(s.PaintWindowTop)
            && IsOnScreen(s.PaintWindowLeft, s.PaintWindowTop, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = s.PaintWindowLeft;
            Top = s.PaintWindowTop;
        }
        if (s.PaintWindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowState()
    {
        var s = Vm.Settings;
        s.PaintWindowMaximized = WindowState == WindowState.Maximized;
        var b = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        s.PaintWindowWidth = b.Width;
        s.PaintWindowHeight = b.Height;
        s.PaintWindowLeft = b.Left;
        s.PaintWindowTop = b.Top;
        s.Save();
    }

    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        var virtualDesktop = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        return virtualDesktop.IntersectsWith(new Rect(left, top, width, height));
    }
}
