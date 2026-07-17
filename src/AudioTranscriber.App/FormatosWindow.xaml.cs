using System.Windows;
using AudioTranscriber.App.ViewModels;

namespace AudioTranscriber.App;

/// <summary>
/// Gestión NATIVA del catálogo de Formatos (<see cref="FormatosViewModel"/>): crear, editar,
/// borrar y marcar default. Abierta desde <see cref="SettingsWindow"/> ("Configuración →
/// Formatos"), mismo criterio "Híbrido nativo" que <see cref="NoteDetailWindow"/>.
/// </summary>
public partial class FormatosWindow : Window
{
    private readonly FormatosViewModel _viewModel;

    public FormatosWindow()
    {
        InitializeComponent();

        _viewModel = new FormatosViewModel();
        DataContext = _viewModel;

        Loaded += async (_, _) => await _viewModel.InitializeAsync();
    }
}
