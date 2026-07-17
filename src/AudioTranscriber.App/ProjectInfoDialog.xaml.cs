using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AudioTranscriber.Core.Workspaces;

namespace AudioTranscriber.App;

/// <summary>Diálogo para editar el título, la descripción y el color de acento de un proyecto.</summary>
public partial class ProjectInfoDialog : Window
{
    public string ProjectTitle => TitleBox.Text.Trim();
    public string ProjectDescription => DescBox.Text.Trim();

    /// <summary>
    /// Id de <see cref="ProjectColorPalette"/> elegido en el picker de swatches, o <c>null</c> si
    /// quedó seleccionado "Sin color". Se lee del RadioButton tildado en <c>ColorSwatches</c> (ver
    /// XAML) en vez de mantener un campo propio: una sola fuente de verdad, el estado visual
    /// mismo.
    /// </summary>
    public string? ProjectColor => ColorSwatches.Children.OfType<RadioButton>()
        .FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

    public ProjectInfoDialog(string title, string description, string? color)
    {
        InitializeComponent();
        TitleBox.Text = title;
        DescBox.Text = description;

        // Preselecciona el swatch que matchea el color actual; un id inválido/desconocido (no
        // debería pasar, ver ProjectColorPalette.Normalize en Workspace) cae en "Sin color" en vez
        // de dejar el picker sin ninguna opción tildada.
        var normalized = ProjectColorPalette.Normalize(color);
        var swatchToCheck = ColorSwatches.Children.OfType<RadioButton>()
            .FirstOrDefault(r => Equals(r.Tag as string, normalized)) ?? SwatchNone;
        swatchToCheck.IsChecked = true;

        Loaded += (_, _) => { TitleBox.SelectAll(); TitleBox.Focus(); };
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>Muestra el diálogo. Devuelve (título, descripción, color) o null si se canceló.</summary>
    public static (string Title, string Description, string? Color)? Show(string title, string description, string? color)
    {
        var dialog = new ProjectInfoDialog(title, description, color)
        {
            Owner = Application.Current.MainWindow,
        };
        return dialog.ShowDialog() == true
            ? (dialog.ProjectTitle, dialog.ProjectDescription, dialog.ProjectColor)
            : null;
    }
}
