using System.Windows;
using System.Windows.Input;
using AudioTranscriber.App.ViewModels;

namespace AudioTranscriber.App;

/// <summary>
/// Gestión NATIVA del vocabulario custom (<see cref="VocabularyViewModel"/>): listar, agregar,
/// editar y borrar términos. Abierta desde <see cref="SettingsWindow"/> ("Configuración →
/// Vocabulario"), mismo criterio "Híbrido nativo" que <see cref="NoteDetailWindow"/>.
/// </summary>
public partial class VocabularyWindow : Window
{
    private readonly VocabularyViewModel _viewModel;

    public VocabularyWindow()
    {
        InitializeComponent();

        _viewModel = new VocabularyViewModel();
        DataContext = _viewModel;

        Loaded += async (_, _) => await _viewModel.InitializeAsync();
    }

    /// <summary>Enter en el campo "nuevo término" agrega, igual que apretar el botón "Agregar".</summary>
    private void OnNewTermKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.AddTermCommand.CanExecute(null))
            _viewModel.AddTermCommand.Execute(null);
    }
}
