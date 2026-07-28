using System.Windows;
using AudioTranscriber.App.ViewModels;

namespace AudioTranscriber.App;

/// <summary>
/// "Invitaciones pendientes" (Team Sharing slice 1b, Phase 16, design ADR-13): ver y resolver
/// (aceptar/rechazar) las invitaciones que el usuario recibió. Mismo criterio de ventana
/// independiente (no modal) que <see cref="BrainWindow"/>/<see cref="FormatosWindow"/>.
/// </summary>
public partial class InvitesWindow : Window
{
    private readonly InvitesViewModel _viewModel;

    public InvitesWindow()
    {
        InitializeComponent();

        _viewModel = new InvitesViewModel();
        DataContext = _viewModel;

        // Carga automática al abrir -- el usuario no debería tener que apretar "Actualizar" la
        // primera vez para ver si tiene invitaciones (mismo criterio de "no hacerla buscar" que
        // el resto de las ventanas con datos remotos de esta app).
        Loaded += async (_, _) => await _viewModel.LoadCommand.ExecuteAsync(null);
    }
}
