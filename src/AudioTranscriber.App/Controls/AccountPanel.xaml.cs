using System.Windows;
using System.Windows.Controls;

namespace AudioTranscriber.App.Controls;

/// <summary>
/// Code-behind del bloque de "Cuenta" (ver AccountPanel.xaml para el contrato completo:
/// scope, DataContext heredado y la razón de <see cref="Compact"/>).
/// </summary>
public partial class AccountPanel : UserControl
{
    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(AccountPanel), new PropertyMetadata(false));

    /// <summary>False (default) = tamaño de SettingsWindow (avatar 44px, iniciales 15px, nombre
    /// 14px, gap 12px). True = tamaño de SyncWindow (avatar 40px, iniciales 14px, nombre 13px,
    /// gap 10px). Son los 5 valores que DESIGN-REVIEW-2026-07-16.md midió como "deriva" entre
    /// las dos ventanas: se preservan tal cual estaban en cada una, no se unifican — eso es una
    /// decisión de diseño visual fuera de este alcance.</summary>
    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public AccountPanel()
    {
        InitializeComponent();
    }
}
