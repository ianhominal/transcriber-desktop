using System.Windows;

namespace AudioTranscriber.App;

/// <summary>Diálogo de dos campos (nombre + instrucción) para crear/editar un Formato.</summary>
public partial class RecipeEditDialog : Window
{
    public string ResponseName => NameBox.Text.Trim();
    public string ResponseInstruction => InstructionBox.Text.Trim();

    public RecipeEditDialog(string title, string header, string defaultName, string defaultInstruction)
    {
        InitializeComponent();
        Title = title;
        HeaderText.Text = header;
        NameBox.Text = defaultName;
        InstructionBox.Text = defaultInstruction;
        Loaded += (_, _) => { NameBox.SelectAll(); NameBox.Focus(); };
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ErrorText.Text = "El nombre no puede estar vacío.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        if (string.IsNullOrWhiteSpace(InstructionBox.Text))
        {
            ErrorText.Text = "Contá qué querés que la IA haga con la nota.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }

    /// <summary>Muestra el diálogo. Devuelve (nombre, instrucción) o null si se canceló.</summary>
    public static (string Name, string Instruction)? Show(
        string title, string header, string defaultName = "", string defaultInstruction = "")
    {
        var dialog = new RecipeEditDialog(title, header, defaultName, defaultInstruction)
        {
            Owner = Application.Current.MainWindow,
        };
        return dialog.ShowDialog() == true ? (dialog.ResponseName, dialog.ResponseInstruction) : null;
    }
}
