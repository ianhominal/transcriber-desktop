using System.Windows;
using System.Windows.Input;

namespace AudioTranscriber.App;

/// <summary>Diálogo simple para pedir un texto (nombre de proyecto, renombrar, etc.).</summary>
public partial class InputDialog : Window
{
    public string ResponseText => Input.Text.Trim();

    public InputDialog(string title, string message, string defaultValue)
    {
        InitializeComponent();
        Title = title;
        Msg.Text = message;
        Input.Text = defaultValue;
        Loaded += (_, _) => { Input.SelectAll(); Input.Focus(); };
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            DialogResult = true;
    }

    /// <summary>Muestra el diálogo. Devuelve el texto ingresado o null si se canceló.</summary>
    public static string? Show(string title, string message, string defaultValue = "")
    {
        var dialog = new InputDialog(title, message, defaultValue)
        {
            Owner = Application.Current.MainWindow,
        };
        return dialog.ShowDialog() == true ? dialog.ResponseText : null;
    }
}
