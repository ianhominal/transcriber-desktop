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

    /// <summary>
    /// Id remoto (Supabase) de este proyecto, CONFIRMADO por el último sync exitoso (ver
    /// <see cref="ViewModels.MainViewModel"/>, método privado <c>ResolveRemoteIds</c> que resuelve
    /// esto tras cada <c>RefreshAudios</c> usando la baseline de <c>SyncIndex</c> -- mismo criterio
    /// que <see cref="AudioItemVm.RemoteId"/>, pero contra <c>LocalSnapshot.Projects</c> en vez de
    /// <c>Transcriptions</c>). Null cuando el proyecto todavía no se sincronizó nunca: la UI usa
    /// esto para deshabilitar "Asistente del proyecto" (no existe id remoto confiable todavía).
    /// Siempre null para el proyecto "General" (<see cref="IsGeneral"/>): no tiene contraparte en
    /// <c>projects</c> del backend.
    /// </summary>
    [ObservableProperty]
    private string? _remoteId;

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
