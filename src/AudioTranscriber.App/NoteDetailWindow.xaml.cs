using System.Windows;
using AudioTranscriber.App.ViewModels;

namespace AudioTranscriber.App;

/// <summary>
/// Detalle NATIVO de una nota sincronizada (resumen + formatos con IA, ver
/// <see cref="NoteDetailViewModel"/>). Reemplaza al viejo <c>AiDetailWindow</c> (WebView2 embebido,
/// eliminado en el mismo cambio -- ver changelog 2026-07-13 "Híbrido nativo"): esta ventana no
/// navega ninguna página web, solo renderiza controles WPF que consumen el backend directo.
/// </summary>
public partial class NoteDetailWindow : Window
{
    private readonly NoteDetailViewModel _viewModel;

    /// <param name="remoteId">Id remoto (Supabase) de la transcripción, ver <see cref="AudioTranscriber.App.ViewModels.AudioItemVm.RemoteId"/>.</param>
    /// <param name="title">Nombre del audio (sin extensión), para el encabezado y el <see cref="Controls.TitleBar"/>.</param>
    /// <param name="transcriptText">Texto transcripto ya cargado en el editor principal, mostrado acá en solo lectura.</param>
    public NoteDetailWindow(string remoteId, string title, string transcriptText)
    {
        InitializeComponent();

        _viewModel = new NoteDetailViewModel(remoteId, title, transcriptText);
        DataContext = _viewModel;

        WindowTitleBar.TitleText = title;
        Title = $"{title} — Audio Transcriber";

        // Carga los formatos del usuario recién cuando la ventana ya está armada (mismo criterio
        // que MainWindow.EnsureOnboarded: I/O de red desde el constructor es frágil).
        Loaded += async (_, _) => await _viewModel.InitializeAsync();

        // Auto-scroll al fondo del chat cada vez que se agrega un mensaje (burbuja de la usuaria o
        // placeholder del asistente) -- MVVM puro no tiene forma de "seguir" el contenido de un
        // ScrollViewer, así que este único enganche de código-behind hace de puente. Se dispara solo
        // en Add (no en cada actualización de texto del streaming): al agregarse ya queda scrolleado
        // al fondo, y el mensaje en streaming es justamente el último visible.
        _viewModel.ChatMessages.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(() => ChatMessagesScroll.ScrollToEnd());
    }
}
