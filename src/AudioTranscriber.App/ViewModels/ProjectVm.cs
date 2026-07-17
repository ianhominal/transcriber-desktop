using System.Collections.ObjectModel;
using AudioTranscriber.Core.Workspaces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioTranscriber.App.ViewModels;

/// <summary>Envoltorio observable de <see cref="AudioProject"/> para el árbol de la UI.</summary>
public partial class ProjectVm : ObservableObject
{
    public AudioProject Model { get; }

    public string Name => Model.Name;
    public bool IsGeneral => Model.IsGeneral;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    private string _title;

    [ObservableProperty]
    private string _description;

    /// <summary>
    /// Id de <see cref="AudioTranscriber.Core.Workspaces.ProjectColorPalette"/>, o <c>null</c> =
    /// "sin color". Bindeado en MainWindow.xaml (punto + borde de acento) vía
    /// ProjectColorToBrushConverter; el proyecto "General" nunca lo setea (queda null).
    /// </summary>
    [ObservableProperty]
    private string? _color;

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>Selección en el árbol (bindeado a TreeViewItem.IsSelected).</summary>
    [ObservableProperty]
    private bool _isSelected;

    public ObservableCollection<AudioItemVm> Audios { get; } = new();

    public ProjectVm(AudioProject model)
    {
        Model = model;
        _title = model.IsGeneral ? "General" : (string.IsNullOrWhiteSpace(model.Title) ? model.Name : model.Title);
        _description = model.Description;
        _color = model.Color;
        foreach (var a in model.Audios)
            Audios.Add(new AudioItemVm(a));
    }

    /// <summary>Texto del nodo: título + cantidad de audios.</summary>
    public string Header => $"{Title}  ({Audios.Count})";
}
