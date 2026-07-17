using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReTex.Core.Paa;
using ReTex.Core.Paint;
using ReTex.Core.Projects;

namespace ReTex.App.ViewModels;

public sealed record PaintToolChoice(PaintTool Tool, string Name, string Shortcut, string Glyph);
public sealed record PaintBrushPresetViewModel(string Name, PaintBrushSettings Settings);

public partial class PaintTextureViewModel : ObservableObject
{
    private const int MaxAnimatedSelectionPixels = 1024 * 1024;
    private readonly byte[] _selectionPixels;
    private readonly byte[] _selectionOutlinePixels;
    private int[] _selectionBoundaryPixels = Array.Empty<int>();

    public event EventHandler? SelectionChanged;
    public PaintDocument Document { get; }
    public PaaFormat SourceFormat { get; }
    public WriteableBitmap Bitmap { get; }
    public WriteableBitmap SelectionBitmap { get; }
    public WriteableBitmap SelectionOutlineBitmap { get; }
    public string Name => System.IO.Path.GetFileName(Document.Path);
    public string Path => Document.Path;
    public string DisplayName => Document.IsDirty ? $"{Name} *" : Name;
    public bool IsDirty => Document.IsDirty;
    public bool CanAnimateSelectionOutline => Document.Width * (long)Document.Height <= MaxAnimatedSelectionPixels;

    public PaintTextureViewModel(PaintDocument document, PaaFormat sourceFormat)
    {
        Document = document; SourceFormat = sourceFormat;
        Bitmap = new WriteableBitmap(document.Width, document.Height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null);
        SelectionBitmap = new WriteableBitmap(document.Width, document.Height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null);
        SelectionOutlineBitmap = new WriteableBitmap(document.Width, document.Height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null);
        _selectionPixels = new byte[document.Width * document.Height * 4];
        _selectionOutlinePixels = new byte[_selectionPixels.Length];
        Refresh(forceFull: true);
    }

    public void Refresh(bool forceFull = false)
    {
        var dirty = Document.ConsumeDirtyRect();
        if (forceFull)
            Bitmap.WritePixels(new Int32Rect(0, 0, Document.Width, Document.Height),
                Document.Pixels, Document.Width * 4, 0);
        else if (dirty is { } d)
            Bitmap.WritePixels(new Int32Rect(d.X, d.Y, d.Width, d.Height),
                Document.Pixels, Document.Width * 4, (d.Y * Document.Width + d.X) * 4);
        OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(DisplayName));
    }

    public void RefreshSelection(int outlinePhase = 0)
    {
        Array.Clear(_selectionPixels);
        var boundaries = new List<int>();
        if (Document.HasSelection)
        {
            for (int p = 0; p < Document.Selection.Length; p++)
            {
                if (!Document.Selection[p]) continue;
                int i = p * 4;
                _selectionPixels[i] = 255; _selectionPixels[i + 1] = 210;
                _selectionPixels[i + 2] = 30; _selectionPixels[i + 3] = 54;
                int x = p % Document.Width, y = p / Document.Width;
                if (IsSelectionBoundary(x, y)) boundaries.Add(p);
            }
        }
        _selectionBoundaryPixels = boundaries.ToArray();
        SelectionBitmap.WritePixels(new Int32Rect(0, 0, Document.Width, Document.Height),
            _selectionPixels, Document.Width * 4, 0);
        RefreshSelectionOutline(outlinePhase);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshSelectionOutline(int phase = 0)
    {
        Array.Clear(_selectionOutlinePixels);
        foreach (int p in _selectionBoundaryPixels)
        {
            int x = p % Document.Width, y = p / Document.Width;
            int i = p * 4;
            bool light = (((x + y + phase) / 4) & 1) == 0;
            byte value = light ? (byte)255 : (byte)0;
            _selectionOutlinePixels[i] = value; _selectionOutlinePixels[i + 1] = value;
            _selectionOutlinePixels[i + 2] = value; _selectionOutlinePixels[i + 3] = 235;
        }
        SelectionOutlineBitmap.WritePixels(new Int32Rect(0, 0, Document.Width, Document.Height),
            _selectionOutlinePixels, Document.Width * 4, 0);
    }

    private bool IsSelectionBoundary(int x, int y)
    {
        return IsOutsideOrClear(x - 1, y) || IsOutsideOrClear(x + 1, y)
            || IsOutsideOrClear(x, y - 1) || IsOutsideOrClear(x, y + 1);
    }

    private bool IsOutsideOrClear(int x, int y)
    {
        if ((uint)x >= (uint)Document.Width || (uint)y >= (uint)Document.Height) return true;
        return !Document.Selection[y * Document.Width + x];
    }
}

public partial class PaintSessionViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _owner;
    private readonly Action? _saveSettings;
    private readonly PaintHistory _history = new();
    private PaintTransaction? _stroke;
    private readonly HashSet<PaintTextureViewModel> _strokeDocuments = new();
    private string? _projectDir;
    private readonly System.Windows.Threading.DispatcherTimer _recoveryTimer;
    private readonly System.Windows.Threading.DispatcherTimer _settingsTimer;
    private readonly System.Windows.Threading.DispatcherTimer _selectionTimer;
    private CancellationTokenSource? _operationCts;
    private bool _syncingColor;
    private bool _loadingSettings;
    private int _selectionPhase;

    public ObservableCollection<PaintTextureViewModel> Textures { get; } = new();
    public ObservableCollection<string> RecentColors { get; } = new();
    public ObservableCollection<PaintBrushPresetViewModel> BrushPresets { get; } = new();
    public IReadOnlyList<PaintToolChoice> ToolChoices { get; } =
    [
        new(PaintTool.Brush, "Brush", "B", "BR"), new(PaintTool.Eraser, "Eraser", "E", "ER"),
        new(PaintTool.Eyedropper, "Eyedropper", "I", "EY"), new(PaintTool.Fill, "Fill", "G", "FL"),
        new(PaintTool.MagicWand, "Magic Wand", "W", "MW"), new(PaintTool.Replace, "Replace", "R", "RP"),
        new(PaintTool.Colorize, "Colorize", "C", "CZ"), new(PaintTool.TextureTint, "Texture Tint", "T", "TT"),
    ];
    public string[] Layouts { get; } = { "Split", "2D", "3D" };

    [ObservableProperty] private PaintTextureViewModel? _activeTexture;
    [ObservableProperty] private PaintTool _tool = PaintTool.Brush;
    [ObservableProperty] private string _layout = "Split";
    [ObservableProperty] private double _brushSize = 32;
    [ObservableProperty] private double _hardness = 0.75;
    [ObservableProperty] private double _opacity = 1;
    [ObservableProperty] private double _flow = 1;
    [ObservableProperty] private double _spacing = 0.2;
    [ObservableProperty] private double _tolerance = 0.12;
    [ObservableProperty] private double _tintStrength = 0.8;
    [ObservableProperty] private double _cameraMoveSpeed = 1;
    [ObservableProperty] private bool _globalMatch;
    [ObservableProperty] private string _colorHex = "#FFE53935";
    [ObservableProperty] private byte _red = 229;
    [ObservableProperty] private byte _green = 57;
    [ObservableProperty] private byte _blue = 53;
    [ObservableProperty] private double _hue = 1.25;
    [ObservableProperty] private double _saturation = 0.77;
    [ObservableProperty] private double _value = 0.90;
    [ObservableProperty] private string _backgroundColorHex = "#FFFFFF";
    [ObservableProperty] private string _brushPresetName = "";
    [ObservableProperty] private PaintBrushPresetViewModel? _selectedBrushPreset;
    [ObservableProperty] private string _status = "Select a project retexture to paint.";
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _operationProgress;
    [ObservableProperty] private double _lastPaintLatencyMs;

    public BitmapSource? ActiveBitmap => ActiveTexture?.Bitmap;
    public BitmapSource? ActiveSelectionBitmap => ActiveTexture?.SelectionBitmap;
    public BitmapSource? ActiveSelectionOutlineBitmap => ActiveTexture?.SelectionOutlineBitmap;
    public bool HasDirty => Textures.Any(t => t.Document.IsDirty);
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public PaintBrushSettings Brush => new(BrushSize, Hardness, Opacity, Flow, Spacing);
    public PaintColor Color => new(Blue, Green, Red, 255);
    public string ActiveToolName => ToolChoices.First(choice => choice.Tool == Tool).Name;
    public bool UsesBrushControls => Tool is PaintTool.Brush or PaintTool.Eraser;
    public bool UsesColorControls => Tool is not (PaintTool.Eraser or PaintTool.MagicWand);
    public bool UsesColorMatchControls => Tool is PaintTool.Fill or PaintTool.MagicWand
        or PaintTool.Replace or PaintTool.Colorize or PaintTool.TextureTint;
    public bool UsesTintControls => Tool == PaintTool.TextureTint;
    public bool ShowsMagicWandHelp => Tool == PaintTool.MagicWand;
    public bool Shows3DNavigation => Layout != "2D";
    public string HueColorHex
    {
        get { HsvToRgb(Hue, 1, 1, out byte r, out byte g, out byte b); return $"#{r:X2}{g:X2}{b:X2}"; }
    }
    public double ColorPickerX => Saturation * 180;
    public double ColorPickerY => (1 - Value) * 140;
    public double HuePickerY => Hue / 360 * 140;

    public void ReportLatency(TimeSpan elapsed)
    {
        double ms = elapsed.TotalMilliseconds;
        LastPaintLatencyMs = LastPaintLatencyMs <= 0 ? ms : LastPaintLatencyMs * 0.8 + ms * 0.2;
    }

    public PaintSessionViewModel(MainViewModel owner, Action? saveSettings = null)
    {
        _owner = owner;
        _saveSettings = saveSettings;
        _recoveryTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _recoveryTimer.Tick += (_, _) => WriteRecovery();
        _recoveryTimer.Start();
        _settingsTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _settingsTimer.Tick += (_, _) => { _settingsTimer.Stop(); SavePaintSettings(); };
        _selectionTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _selectionTimer.Tick += (_, _) => AdvanceSelectionOutline();
        _selectionTimer.Start();
        LoadPaintSettings();
    }

    partial void OnActiveTextureChanged(PaintTextureViewModel? value)
    {
        OnPropertyChanged(nameof(ActiveBitmap));
        OnPropertyChanged(nameof(ActiveSelectionBitmap));
        OnPropertyChanged(nameof(ActiveSelectionOutlineBitmap));
    }
    partial void OnToolChanged(PaintTool value)
    {
        OnPropertyChanged(nameof(ActiveToolName));
        OnPropertyChanged(nameof(UsesBrushControls));
        OnPropertyChanged(nameof(UsesColorControls));
        OnPropertyChanged(nameof(UsesColorMatchControls));
        OnPropertyChanged(nameof(UsesTintControls));
        OnPropertyChanged(nameof(ShowsMagicWandHelp));
        SchedulePaintSettingsSave();
    }
    partial void OnLayoutChanged(string value)
    {
        OnPropertyChanged(nameof(Shows3DNavigation));
        SchedulePaintSettingsSave();
    }
    partial void OnGlobalMatchChanged(bool value) => SchedulePaintSettingsSave();
    partial void OnRedChanged(byte value) { bool syncing = _syncingColor; SyncFromRgb(); if (!syncing) SchedulePaintSettingsSave(); }
    partial void OnGreenChanged(byte value) { bool syncing = _syncingColor; SyncFromRgb(); if (!syncing) SchedulePaintSettingsSave(); }
    partial void OnBlueChanged(byte value) { bool syncing = _syncingColor; SyncFromRgb(); if (!syncing) SchedulePaintSettingsSave(); }
    partial void OnHueChanged(double value)
    {
        if (ClampProperty(value, 0, 360, 0, v => Hue = v)) return;
        bool syncing = _syncingColor;
        OnPropertyChanged(nameof(HueColorHex)); OnPropertyChanged(nameof(HuePickerY)); SyncFromHsv();
        if (!syncing) SchedulePaintSettingsSave();
    }
    partial void OnSaturationChanged(double value)
    {
        if (ClampProperty(value, 0, 1, 0, v => Saturation = v)) return;
        bool syncing = _syncingColor;
        OnPropertyChanged(nameof(ColorPickerX)); SyncFromHsv();
        if (!syncing) SchedulePaintSettingsSave();
    }
    partial void OnValueChanged(double value)
    {
        if (ClampProperty(value, 0, 1, 0, v => Value = v)) return;
        bool syncing = _syncingColor;
        OnPropertyChanged(nameof(ColorPickerY)); SyncFromHsv();
        if (!syncing) SchedulePaintSettingsSave();
    }
    partial void OnBrushSizeChanged(double value) { if (!ClampProperty(value, 1, 256, 32, v => BrushSize = v)) SchedulePaintSettingsSave(); }
    partial void OnHardnessChanged(double value) { if (!ClampProperty(value, 0, 1, 0.75, v => Hardness = v)) SchedulePaintSettingsSave(); }
    partial void OnOpacityChanged(double value) { if (!ClampProperty(value, 0.01, 1, 1, v => Opacity = v)) SchedulePaintSettingsSave(); }
    partial void OnFlowChanged(double value) { if (!ClampProperty(value, 0.01, 1, 1, v => Flow = v)) SchedulePaintSettingsSave(); }
    partial void OnSpacingChanged(double value) { if (!ClampProperty(value, 0.02, 1, 0.2, v => Spacing = v)) SchedulePaintSettingsSave(); }
    partial void OnToleranceChanged(double value) { if (!ClampProperty(value, 0, 1, 0.12, v => Tolerance = v)) SchedulePaintSettingsSave(); }
    partial void OnTintStrengthChanged(double value) { if (!ClampProperty(value, 0, 1, 0.8, v => TintStrength = v)) SchedulePaintSettingsSave(); }
    partial void OnCameraMoveSpeedChanged(double value) { if (!ClampProperty(value, 0.1, 5, 1, v => CameraMoveSpeed = v)) SchedulePaintSettingsSave(); }
    partial void OnColorHexChanged(string value)
    {
        if (_syncingColor) return;
        try
        {
            var c = PaintColor.FromHex(value);
            SetForeground(c);
            SchedulePaintSettingsSave();
        }
        catch { }
    }
    partial void OnBackgroundColorHexChanged(string value) => SchedulePaintSettingsSave();
    partial void OnSelectedBrushPresetChanged(PaintBrushPresetViewModel? value)
    {
        if (value is not null) BrushPresetName = value.Name;
        ApplySelectedBrushPresetCommand.NotifyCanExecuteChanged();
        DeleteBrushPresetCommand.NotifyCanExecuteChanged();
    }
    partial void OnBrushPresetNameChanged(string value) => SaveBrushPresetCommand.NotifyCanExecuteChanged();

    private void SyncFromRgb()
    {
        if (_syncingColor) return;
        _syncingColor = true;
        ColorHex = $"#{Red:X2}{Green:X2}{Blue:X2}";
        RgbToHsv(Red, Green, Blue, out double hue, out double saturation, out double value);
        Hue = hue; Saturation = saturation; Value = value;
        _syncingColor = false;
    }

    private void SyncFromHsv()
    {
        if (_syncingColor) return;
        _syncingColor = true;
        HsvToRgb(Hue, Saturation, Value, out byte red, out byte green, out byte blue);
        Red = red; Green = green; Blue = blue;
        ColorHex = $"#{red:X2}{green:X2}{blue:X2}";
        _syncingColor = false;
    }

    private void SetForeground(PaintColor color)
    {
        _syncingColor = true;
        Red = color.R; Green = color.G; Blue = color.B;
        ColorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        RgbToHsv(color.R, color.G, color.B, out double hue, out double saturation, out double value);
        Hue = hue; Saturation = saturation; Value = value;
        _syncingColor = false;
    }

    private static bool ClampProperty(double value, double minimum, double maximum, double fallback,
        Action<double> assign)
    {
        double clamped = double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
        if (value.Equals(clamped)) return false;
        assign(clamped); return true;
    }

    public void Load(RetexProject? project, RetexEntry? entry)
    {
        _operationCts?.Cancel();
        EndStroke(); WriteRecovery(); Textures.Clear(); ActiveTexture = null;
        _projectDir = project?.ProjectDir;
        if (project is null || entry is null) { IsAvailable = false; Status = "Select a project retexture to paint."; return; }
        foreach (string path in entry.Selections.Where(s => s.ProjectTexture.Length > 0)
            .Select(s => Path.GetFullPath(Path.Combine(project.AddonDir, s.ProjectTexture)))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var image = PaaImage.LoadFile(path);
                if (!image.Metadata.IsPaintable) continue;
                var texture = new PaintTextureViewModel(
                    new PaintDocument(path, image.Width, image.Height, (byte[])image.Bgra.Clone()), image.Format);
                texture.SelectionChanged += OnTextureSelectionChanged;
                Textures.Add(texture);
            }
            catch { }
        }
        ActiveTexture = Textures.FirstOrDefault();
        IsAvailable = Textures.Count > 0;
        Status = IsAvailable ? $"{Textures.Count} editable texture(s). Paint in 2D or directly on the model."
            : "No editable BC1/BC3 project texture is available for this entry.";
        TryRecover();
        BindToPreview();
    }

    public void BindToPreview()
    {
        foreach (var texture in Textures)
        {
            _owner.BindPaintBitmap(texture.Path, texture.Bitmap);
            _owner.BindPaintSelectionBitmap(texture.Path, texture.SelectionBitmap);
        }
    }

    private void OnTextureSelectionChanged(object? sender, EventArgs e)
    {
        if (sender is PaintTextureViewModel texture)
            _owner.BindPaintSelectionBitmap(texture.Path, texture.SelectionBitmap);
    }

    public PaintTextureViewModel? Find(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full;
        try { full = Path.GetFullPath(path); } catch { full = path; }
        return Textures.FirstOrDefault(t => t.Path.Equals(full, StringComparison.OrdinalIgnoreCase));
    }

    public void BeginStroke(string name = "Paint stroke")
    {
        if (IsBusy) return;
        RememberColor();
        EndStroke(); _stroke = _history.Begin(name); _strokeDocuments.Clear();
    }

    public void Stamp(PaintTextureViewModel texture, double x, double y, double? size = null)
    {
        _stroke ??= _history.Begin("Paint stroke");
        var brush = size.HasValue ? Brush with { Size = size.Value } : Brush;
        texture.Document.Stamp(x, y, Color, brush, Tool == PaintTool.Eraser, _stroke);
        _strokeDocuments.Add(texture);
    }

    /// <summary>Applies one sampled screen-space projection to every project texture it crossed.
    /// The caller brackets consecutive samples with BeginStroke/EndStroke, keeping all materials in
    /// one history transaction even when a brush crosses UV seams or separate PAAs.</summary>
    public IReadOnlyCollection<PaintTextureViewModel> StampProjected(
        IReadOnlyDictionary<string, IReadOnlyCollection<PaintTexel>> projected, double sampleSpacing)
    {
        var touched = new List<PaintTextureViewModel>();
        foreach (var pair in projected)
        {
            var texture = Find(pair.Key);
            if (texture is null) continue;
            double stampSize = Math.Max(1.5,
                Math.Min(texture.Document.Width, texture.Document.Height) / 1024d * sampleSpacing);
            foreach (var texel in pair.Value) Stamp(texture, texel.X, texel.Y, stampSize);
            touched.Add(texture);
        }
        return touched;
    }

    public void EndStroke()
    {
        if (_stroke is null) return;
        _stroke.Commit(); _stroke = null;
        foreach (var doc in _strokeDocuments) doc.Refresh();
        _strokeDocuments.Clear(); RaiseHistory();
    }

    public void Sample(PaintTextureViewModel texture, int x, int y)
    {
        var c = texture.Document.Sample(x, y); SetForeground(c); RememberColor();
        Status = $"Sampled {ColorHex}.";
    }

    public async Task ApplyPointToolAsync(PaintTextureViewModel texture, int x, int y,
        PaintSelectionCombine selectionCombine = PaintSelectionCombine.Replace)
    {
        if (IsBusy) return;
        var mode = GlobalMatch ? PaintMatchMode.Global : PaintMatchMode.Contiguous;
        if (Tool == PaintTool.Eyedropper) { Sample(texture, x, y); return; }
        if (Tool == PaintTool.MagicWand)
        {
            _operationCts = new CancellationTokenSource();
            var selectionToken = _operationCts.Token;
            IProgress<double> selectionProgress = new Progress<double>(value => OperationProgress = value);
            IsBusy = true; OperationProgress = 0; Status = "Building color selection...";
            try
            {
                double tolerance = Tolerance;
                await Task.Run(() => texture.Document.SelectByColor(x, y, tolerance, mode,
                    selectionCombine, selectionToken, value => selectionProgress.Report(value)), selectionToken);
                texture.RefreshSelection(_selectionPhase);
                Status = selectionCombine switch
                {
                    PaintSelectionCombine.Add => "Added matching color to the selection.",
                    PaintSelectionCombine.Subtract => "Removed matching color from the selection.",
                    _ => "Color selection replaced.",
                };
            }
            catch (OperationCanceledException) { Status = "Color selection cancelled."; }
            catch (Exception ex) { Status = $"Color selection failed: {ex.Message}"; }
            finally
            {
                IsBusy = false; OperationProgress = 0;
                _operationCts.Dispose(); _operationCts = null;
            }
            return;
        }
        if (Tool is not (PaintTool.Fill or PaintTool.Replace or PaintTool.Colorize or PaintTool.TextureTint)) return;

        RememberColor();
        PaintTool requestedTool = Tool;
        _operationCts = new CancellationTokenSource();
        var token = _operationCts.Token;
        IProgress<double> progress = new Progress<double>(value => OperationProgress = value);
        IsBusy = true; OperationProgress = 0; Status = $"Applying {requestedTool.ToString().ToLowerInvariant()}...";
        using var tx = _history.Begin(requestedTool.ToString());
        bool committed = false;
        try
        {
            PaintTool operation = requestedTool == PaintTool.Fill ? PaintTool.Replace : requestedTool;
            PaintColor color = Color;
            double tolerance = Tolerance;
            double tintStrength = TintStrength;
            await Task.Run(() => texture.Document.ApplyColorOperation(x, y, color, tolerance,
                mode, operation, tx, token, value => progress.Report(value), tintStrength), token);
            tx.Commit(); committed = true;
            Status = $"{requestedTool} complete.";
        }
        catch (OperationCanceledException)
        {
            tx.Rollback();
            Status = $"{requestedTool} cancelled; no pixels were changed.";
        }
        catch (Exception ex)
        {
            tx.Rollback();
            Status = $"{requestedTool} failed: {ex.Message}";
        }
        finally
        {
            texture.Refresh();
            IsBusy = false; OperationProgress = 0;
            _operationCts.Dispose(); _operationCts = null;
            if (committed) RaiseHistory();
            else OnPropertyChanged(nameof(HasDirty));
        }
    }

    [RelayCommand] private void Undo()
    { if (IsBusy) return; foreach (var d in _history.Undo()) Find(d.Path)?.Refresh(); RaiseHistory(); }
    [RelayCommand] private void Redo()
    { if (IsBusy) return; foreach (var d in _history.Redo()) Find(d.Path)?.Refresh(); RaiseHistory(); }
    [RelayCommand] private void ClearSelection() { if (IsBusy) return; foreach (var t in Textures) { t.Document.ClearSelection(); t.RefreshSelection(_selectionPhase); } Status = "Selection cleared."; }
    [RelayCommand] private void InvertSelection() { if (IsBusy) return; if (ActiveTexture is { } t) { t.Document.InvertSelection(); t.RefreshSelection(_selectionPhase); } Status = "Selection inverted."; }
    [RelayCommand] private void CancelOperation() => _operationCts?.Cancel();
    [RelayCommand] private void SwapColors()
    {
        string foreground = ColorHex;
        try { SetForeground(PaintColor.FromHex(BackgroundColorHex)); BackgroundColorHex = foreground; }
        catch { }
    }
    [RelayCommand] private void ResetColors() { SetForeground(new PaintColor(0, 0, 0)); BackgroundColorHex = "#FFFFFF"; }
    [RelayCommand] private void SelectRecentColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try { SetForeground(PaintColor.FromHex(value)); }
        catch { }
    }
    [RelayCommand] private void ApplyBrushPreset(string? preset)
    {
        if (string.IsNullOrWhiteSpace(preset)) return;
        PaintBrushSettings? saved = BrushPresets.FirstOrDefault(p =>
            string.Equals(p.Name, preset, StringComparison.OrdinalIgnoreCase))?.Settings;
        switch (preset)
        {
            case "Hard": Hardness = 1; Opacity = 1; Flow = 1; Spacing = 0.2; break;
            case "Soft": Hardness = 0; Opacity = 0.65; Flow = 0.5; Spacing = 0.12; break;
            case "Airbrush": Hardness = 0; Opacity = 0.3; Flow = 0.12; Spacing = 0.05; break;
            default:
                if (saved is not { } settings) return;
                BrushSize = settings.Size; Hardness = settings.Hardness; Opacity = settings.Opacity;
                Flow = settings.Flow; Spacing = settings.Spacing;
                break;
        }
        Status = $"Applied {preset.ToLowerInvariant()} brush preset.";
    }

    private bool CanApplySelectedBrushPreset() => SelectedBrushPreset is not null;

    [RelayCommand(CanExecute = nameof(CanApplySelectedBrushPreset))]
    private void ApplySelectedBrushPreset()
    {
        if (SelectedBrushPreset is { } preset) ApplyBrushPreset(preset.Name);
    }

    private bool CanSaveBrushPreset() => NormalizePresetName(BrushPresetName).Length > 0;

    [RelayCommand(CanExecute = nameof(CanSaveBrushPreset))]
    private void SaveBrushPreset()
    {
        string name = NormalizePresetName(BrushPresetName);
        int existing = FindBrushPresetIndex(name);
        var preset = new PaintBrushPresetViewModel(name, Brush);
        if (existing >= 0) BrushPresets[existing] = preset;
        else BrushPresets.Add(preset);
        SelectedBrushPreset = preset;
        SavePaintSettings();
        Status = existing >= 0 ? $"Updated brush preset {name}." : $"Saved brush preset {name}.";
    }

    private bool CanDeleteBrushPreset() => SelectedBrushPreset is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteBrushPreset))]
    private void DeleteBrushPreset()
    {
        var preset = SelectedBrushPreset;
        if (preset is null) return;
        int index = BrushPresets.IndexOf(preset);
        if (index < 0) return;
        BrushPresets.RemoveAt(index);
        SelectedBrushPreset = BrushPresets.Count == 0 ? null : BrushPresets[Math.Min(index, BrushPresets.Count - 1)];
        SavePaintSettings();
        Status = $"Deleted brush preset {preset.Name}.";
    }
    [RelayCommand] private async Task SaveAll()
    {
        try { await SaveAsync(); }
        catch (Exception ex) { Status = $"Save failed: {ex.Message}"; }
    }

    public async Task SaveAsync()
    {
        if (IsBusy) { Status = "Cancel or finish the current paint operation before saving."; return; }
        EndStroke();
        var dirty = Textures.Where(t => t.Document.IsDirty).ToArray();
        if (dirty.Length == 0) { Status = "There are no painted changes to save."; return; }

        _operationCts = new CancellationTokenSource();
        var token = _operationCts.Token;
        var saved = new List<string>();
        string? projectDir = _projectDir;
        IProgress<double> saveProgress = new Progress<double>(value => OperationProgress = value);
        IsBusy = true; OperationProgress = 0; Status = "Encoding PAA textures...";
        try
        {
            for (int i = 0; i < dirty.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var texture = dirty[i];
                Status = $"Encoding {texture.Name} ({i + 1}/{dirty.Length})...";
                int textureIndex = i;
                await Task.Run(() => WriteTextureFile(texture, projectDir,
                    path => _owner.PreparePaintSave(new[] { path }), token,
                    progress => saveProgress.Report((textureIndex + progress) / dirty.Length)), token);
                texture.Document.MarkSaved(); texture.Refresh(); saved.Add(texture.Path);
                OperationProgress = (i + 1d) / dirty.Length;
            }
            Status = "All painted textures saved as PAA.";
        }
        catch (OperationCanceledException)
        {
            Status = saved.Count == 0 ? "PAA save cancelled." : $"Save cancelled after {saved.Count} texture(s).";
        }
        finally
        {
            IsBusy = false; OperationProgress = 0;
            _operationCts.Dispose(); _operationCts = null;
            OnPropertyChanged(nameof(HasDirty));
            if (saved.Count > 0) { DeleteRecovery(saved); _owner.NotifyPaintSaved(saved); }
        }
    }

    public void Save()
    {
        if (IsBusy) { Status = "Cancel or finish the current paint operation before saving."; return; }
        EndStroke();
        var dirty = Textures.Where(t => t.Document.IsDirty).ToArray();
        foreach (var texture in dirty)
        {
            WriteTextureFile(texture, _projectDir, path => _owner.PreparePaintSave(new[] { path }),
                CancellationToken.None, null);
            texture.Document.MarkSaved(); texture.Refresh();
        }
        DeleteRecovery(dirty.Select(t => t.Path)); Status = "All painted textures saved as PAA.";
        OnPropertyChanged(nameof(HasDirty));
        if (dirty.Length > 0) _owner.NotifyPaintSaved(dirty.Select(t => t.Path));
    }

    private static void WriteTextureFile(PaintTextureViewModel texture, string? projectDir,
        Action<string> beforeReplace, CancellationToken cancellationToken, Action<double>? reportProgress)
    {
        string temp = texture.Path + ".retex.tmp";
        try
        {
            string backupRoot = Path.Combine(projectDir ?? Path.GetDirectoryName(texture.Path)!, "paint-backups");
            Directory.CreateDirectory(backupRoot);
            string backup = Path.Combine(backupRoot, Path.GetFileName(texture.Path) + ".original.paa");
            if (!File.Exists(backup)) File.Copy(texture.Path, backup);
            PaaWriter.Write(temp, texture.Document.Width, texture.Document.Height, texture.Document.Pixels,
                new PaaWriteOptions(texture.SourceFormat), cancellationToken, reportProgress);
            cancellationToken.ThrowIfCancellationRequested();
            beforeReplace(texture.Path);
            File.Move(temp, texture.Path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private void RaiseHistory()
    {
        OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(CanRedo)); OnPropertyChanged(nameof(HasDirty));
    }

    private void RememberColor()
    {
        string value = $"#{Red:X2}{Green:X2}{Blue:X2}";
        int existing = RecentColors.IndexOf(value);
        if (existing >= 0) RecentColors.RemoveAt(existing);
        RecentColors.Insert(0, value);
        while (RecentColors.Count > 12) RecentColors.RemoveAt(RecentColors.Count - 1);
        SchedulePaintSettingsSave();
    }

    private void LoadPaintSettings()
    {
        _loadingSettings = true;
        try
        {
            var s = _owner.Settings;
            if (Enum.TryParse(s.PaintTool, out PaintTool savedTool)) Tool = savedTool;
            if (Layouts.Contains(s.PaintLayout)) Layout = s.PaintLayout;
            BrushSize = s.PaintBrushSize; Hardness = s.PaintHardness; Opacity = s.PaintOpacity;
            Flow = s.PaintFlow; Spacing = s.PaintSpacing; Tolerance = s.PaintTolerance;
            TintStrength = s.PaintTintStrength; CameraMoveSpeed = s.PaintCameraMoveSpeed;
            GlobalMatch = s.PaintGlobalMatch;
            BackgroundColorHex = string.IsNullOrWhiteSpace(s.PaintBackgroundColorHex) ? "#FFFFFF" : s.PaintBackgroundColorHex;
            if (!string.IsNullOrWhiteSpace(s.PaintColorHex)) ColorHex = s.PaintColorHex;
            RecentColors.Clear();
            foreach (var color in s.PaintRecentColors.Where(c => !string.IsNullOrWhiteSpace(c)).Take(12))
                RecentColors.Add(color);
            BrushPresets.Clear();
            foreach (var preset in s.PaintBrushPresets
                         .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                         .Select(p => new PaintBrushPresetViewModel(NormalizePresetName(p.Name),
                             new PaintBrushSettings(p.Size, p.Hardness, p.Opacity, p.Flow, p.Spacing)))
                         .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                         .Select(g => g.First())
                         .Take(24))
                BrushPresets.Add(preset);
        }
        finally { _loadingSettings = false; }
    }

    private void SchedulePaintSettingsSave()
    {
        if (_loadingSettings) return;
        _settingsTimer.Stop();
        _settingsTimer.Start();
    }

    private void SavePaintSettings()
    {
        if (_loadingSettings) return;
        var s = _owner.Settings;
        s.PaintTool = Tool.ToString();
        s.PaintLayout = Layout;
        s.PaintBrushSize = BrushSize;
        s.PaintHardness = Hardness;
        s.PaintOpacity = Opacity;
        s.PaintFlow = Flow;
        s.PaintSpacing = Spacing;
        s.PaintTolerance = Tolerance;
        s.PaintTintStrength = TintStrength;
        s.PaintCameraMoveSpeed = CameraMoveSpeed;
        s.PaintGlobalMatch = GlobalMatch;
        s.PaintColorHex = $"#{Red:X2}{Green:X2}{Blue:X2}";
        s.PaintBackgroundColorHex = BackgroundColorHex;
        s.PaintRecentColors = RecentColors.Take(12).ToList();
        s.PaintBrushPresets = BrushPresets.Select(p => new PaintBrushPresetSettings
        {
            Name = p.Name,
            Size = p.Settings.Size,
            Hardness = p.Settings.Hardness,
            Opacity = p.Settings.Opacity,
            Flow = p.Settings.Flow,
            Spacing = p.Settings.Spacing,
        }).ToList();
        _saveSettings?.Invoke();
    }

    private int FindBrushPresetIndex(string name)
    {
        for (int i = 0; i < BrushPresets.Count; i++)
            if (string.Equals(BrushPresets[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static string NormalizePresetName(string? value) =>
        string.Join(' ', (value ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private void AdvanceSelectionOutline()
    {
        if (IsBusy || ActiveTexture is not { CanAnimateSelectionOutline: true } texture
            || !texture.Document.HasSelection) return;
        _selectionPhase = (_selectionPhase + 1) & 31;
        texture.RefreshSelectionOutline(_selectionPhase);
    }

    private static void RgbToHsv(byte red, byte green, byte blue,
        out double hue, out double saturation, out double value)
    {
        double r = red / 255d, g = green / 255d, b = blue / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        hue = delta == 0 ? 0 : max == r ? 60 * (((g - b) / delta) % 6)
            : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        if (hue < 0) hue += 360;
        saturation = max == 0 ? 0 : delta / max;
        value = max;
    }

    private static void HsvToRgb(double hue, double saturation, double value,
        out byte red, out byte green, out byte blue)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1); value = Math.Clamp(value, 0, 1);
        double chroma = value * saturation, x = chroma * (1 - Math.Abs((hue / 60) % 2 - 1));
        (double r, double g, double b) = hue switch
        {
            < 60 => (chroma, x, 0d), < 120 => (x, chroma, 0d), < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma), < 300 => (x, 0d, chroma), _ => (chroma, 0d, x),
        };
        double match = value - chroma;
        red = (byte)Math.Round((r + match) * 255);
        green = (byte)Math.Round((g + match) * 255);
        blue = (byte)Math.Round((b + match) * 255);
    }

    private string? RecoveryDir => _projectDir is null ? null : Path.Combine(_projectDir, ".retex", "paint-recovery");
    private void WriteRecovery()
    {
        if (!HasDirty || RecoveryDir is null) return;
        foreach (var t in Textures.Where(t => t.Document.IsDirty))
        {
            try { PaintRecoveryStore.Write(RecoveryDir, t.Document); }
            catch (Exception ex) { Status = $"Paint recovery could not be written: {ex.Message}"; }
        }
    }
    private void DeleteRecovery(IEnumerable<string> paths)
    {
        if (RecoveryDir is { } dir) PaintRecoveryStore.Delete(dir, paths);
    }
    private void TryRecover()
    {
        if (RecoveryDir is not { } dir || !Directory.Exists(dir)) return;
        var texturePaths = Textures.Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recoveries = Directory.GetFiles(dir, "*.bgra")
            .Select(file => PaintRecoveryStore.TryRead(file, out var snapshot) ? snapshot : null)
            .Where(snapshot => snapshot is not null && texturePaths.Contains(snapshot.Path)).Cast<PaintRecoverySnapshot>().ToArray();
        if (recoveries.Length == 0) return;
        var answer = MessageBox.Show("ReTex found unsaved Paint recovery data for this project. Restore it?",
            "Recover Paint session", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) { DeleteRecovery(recoveries.Select(r => r.Path)); return; }
        int restored = 0;
        foreach (var recovery in recoveries)
        {
            var texture = Find(recovery.Path);
            if (texture is null || texture.Document.Width != recovery.Width || texture.Document.Height != recovery.Height) continue;
            Buffer.BlockCopy(recovery.Pixels, 0, texture.Document.Pixels, 0, recovery.Pixels.Length);
            texture.Document.MarkDirty(); texture.Refresh(); restored++;
        }
        if (restored > 0) Status = $"Recovered unsaved Paint changes for {restored} texture(s).";
        OnPropertyChanged(nameof(HasDirty));
    }
    public void Dispose()
    {
        _operationCts?.Cancel(); _operationCts?.Dispose();
        _recoveryTimer.Stop();
        _selectionTimer.Stop();
        if (_settingsTimer.IsEnabled) { _settingsTimer.Stop(); SavePaintSettings(); }
    }
}
