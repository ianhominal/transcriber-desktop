using System.Windows;
using AudioTranscriber.App.ViewModels;

namespace AudioTranscriber.App;

/// <summary>
/// "Transcribir desde una URL": ventana propia para pegar una URL, analizarla con yt-dlp, elegir
/// uno o más elementos y descargarlos. Se abre desde <c>MainViewModel.OpenWebImportCommand</c>.
/// Mismo criterio de ventana independiente (no <c>ShowDialog</c>) que <see cref="ShareWindow"/>/
/// <see cref="BrainWindow"/> -- la usuaria puede seguir usando la ventana principal mientras analiza
/// o descarga.
/// </summary>
public partial class WebImportWindow : Window
{
    public WebImportWindow(WebImportViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
