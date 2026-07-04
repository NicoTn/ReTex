using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReTex.Core.Projects;

namespace ReTex.App.ViewModels;

/// <summary>
/// One row in the form editor's Selections list: wraps a <see cref="RetexSelection"/> and exposes
/// its name + source/project texture paths for display, plus a Browse command. The row is a thin
/// observable wrapper so the assigned project-texture path updates in place after Browse (the
/// underlying model type isn't observable). The actual file-pick/import work lives in the main VM,
/// invoked via the <c>onBrowse</c> callback.
/// </summary>
public partial class SelectionRowViewModel : ObservableObject
{
    private readonly Action<SelectionRowViewModel> _onBrowse;

    public RetexSelection Selection { get; }

    public SelectionRowViewModel(RetexSelection selection, Action<SelectionRowViewModel> onBrowse)
    {
        Selection = selection;
        _onBrowse = onBrowse;
    }

    public string Name => Selection.Name.Length > 0 ? Selection.Name : $"selection {Selection.Index}";
    public string SourceTexture => Selection.SourceTexture.Length > 0 ? Selection.SourceTexture : "(none)";
    public string ProjectTexture => Selection.ProjectTexture.Length > 0 ? Selection.ProjectTexture : "(not retextured)";

    /// <summary>True when this selection also swaps an .rvmat material (so the form shows it).</summary>
    public bool HasMaterial => Selection.ProjectMaterial.Length > 0;
    public string ProjectMaterial => $"material: {Selection.ProjectMaterial}";

    [RelayCommand]
    private void Browse() => _onBrowse(this);

    /// <summary>Raises change notification after the underlying model's ProjectTexture is reassigned.</summary>
    public void RefreshProjectTexture() => OnPropertyChanged(nameof(ProjectTexture));
}
