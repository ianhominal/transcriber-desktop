using System.Windows;
using System.Windows.Input;

namespace AudioTranscriber.App;

/// <summary>
/// Diálogo que se muestra al terminar de grabar desde el micrófono: deja elegir el proyecto
/// destino (o "General") y el título de la grabación (prellenado con el nombre automático, ver
/// <see cref="Core.Workspaces.RecordingSaveDefaults.DefaultTitle"/>). El caller (<c>MainViewModel</c>)
/// decide si hace falta mover/renombrar el audio ya grabado según lo que el usuario elija acá.
/// </summary>
public partial class RecordingSaveDialog : Window
{
    // Índice 0 siempre es General (null). El resto son nombres de proyecto reales, en el mismo
    // orden que se muestran en el ComboBox.
    private readonly List<string?> _projectValues = new();

    /// <summary>Proyecto elegido: <c>null</c> significa General.</summary>
    public string? SelectedProjectName { get; private set; }

    public string RecordingTitle => TitleBox.Text.Trim();

    public RecordingSaveDialog(IReadOnlyList<string> projectNames, string? preselectedProject, string defaultTitle)
    {
        InitializeComponent();

        _projectValues.Add(null);
        ProjectCombo.Items.Add("General");
        foreach (var name in projectNames)
        {
            _projectValues.Add(name);
            ProjectCombo.Items.Add(name);
        }

        var preselectIndex = preselectedProject is null
            ? 0
            : _projectValues.FindIndex(v => string.Equals(v, preselectedProject, StringComparison.OrdinalIgnoreCase));
        ProjectCombo.SelectedIndex = preselectIndex >= 0 ? preselectIndex : 0;

        TitleBox.Text = defaultTitle;
        Loaded += (_, _) => { TitleBox.SelectAll(); TitleBox.Focus(); };
    }

    private void OnOk(object sender, RoutedEventArgs e) => Confirm();

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Confirm();
    }

    private void Confirm()
    {
        SelectedProjectName = _projectValues[ProjectCombo.SelectedIndex];
        DialogResult = true;
    }

    /// <summary>
    /// Muestra el diálogo. Devuelve (proyecto elegido, título) o null si se canceló.
    /// <paramref name="projectNames"/> son solo los proyectos reales (sin "General": el diálogo
    /// ya lo agrega como primera opción).
    /// </summary>
    public static (string? ProjectName, string Title)? Show(
        IReadOnlyList<string> projectNames, string? preselectedProject, string defaultTitle)
    {
        var dialog = new RecordingSaveDialog(projectNames, preselectedProject, defaultTitle)
        {
            Owner = Application.Current.MainWindow,
        };
        return dialog.ShowDialog() == true ? (dialog.SelectedProjectName, dialog.RecordingTitle) : null;
    }
}
