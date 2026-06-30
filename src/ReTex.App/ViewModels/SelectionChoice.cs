using CommunityToolkit.Mvvm.ComponentModel;

namespace ReTex.App.ViewModels;

/// <summary>A hidden selection the user can choose to retexture or leave alone.</summary>
public partial class SelectionChoice : ObservableObject
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    [ObservableProperty] private bool _selected = true;

    public string Label => $"[{Index}] {Name}";
}
