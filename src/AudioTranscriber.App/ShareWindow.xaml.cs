using System.Windows;
using AudioTranscriber.App.ViewModels;

namespace AudioTranscriber.App;

/// <summary>
/// "Compartir proyecto" (Team Sharing slice 1b, Phase 17): invitar por email, ver/cambiar el rol
/// de los miembros actuales, sacarlos, y ver/cancelar las invitaciones pendientes que ya mandaste
/// para este proyecto. Se abre desde <c>MainViewModel.OpenShareWindowCommand</c> con el proyecto
/// seleccionado. Mismo criterio de ventana independiente (no modal) que <see cref="InvitesWindow"/>.
/// </summary>
public partial class ShareWindow : Window
{
    private readonly ShareViewModel _viewModel;

    public ShareWindow(string projectId, string projectTitle)
    {
        InitializeComponent();

        _viewModel = new ShareViewModel(projectId, projectTitle);
        DataContext = _viewModel;

        Title = $"Compartir {projectTitle} — Audio Transcriber";
        WindowTitleBar.TitleText = $"Compartir {projectTitle}";

        Loaded += async (_, _) => await _viewModel.LoadCommand.ExecuteAsync(null);
    }
}
