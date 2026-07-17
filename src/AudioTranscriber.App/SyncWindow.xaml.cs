using System.Windows;

namespace AudioTranscriber.App;

/// <summary>Ventana de sincronización con la nube (login, carpeta y sync manual).</summary>
public partial class SyncWindow : Window
{
    public SyncWindow()
    {
        InitializeComponent();

        // Misma instancia compartida que usan MainWindow y el menú de la bandeja: así el estado
        // (carpeta, IsLoggedIn, IsBusy, StatusMessage) siempre está consistente en toda la app.
        DataContext = SyncCoordinator.Instance;
    }
}
