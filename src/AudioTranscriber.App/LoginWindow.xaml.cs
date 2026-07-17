using System.Net.Http;
using System.Windows;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.App;

/// <summary>
/// Diálogo de login contra Supabase Auth. Al iniciar sesión con éxito, persiste el access
/// token/refresh token con <see cref="SecureStore"/> (nunca la contraseña) y cierra el diálogo.
/// </summary>
public partial class LoginWindow : Window
{
    private readonly HttpClient _http;
    private CancellationTokenSource? _googleSignInCts;

    public LoginWindow(HttpClient http)
    {
        InitializeComponent();
        _http = http;
        Loaded += (_, _) => EmailBox.Focus();
    }

    private async void OnSignIn(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        var password = PasswordInput.Password;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            ErrorText.Text = "Ingresá tu email y tu contraseña.";
            return;
        }

        ErrorText.Text = string.Empty;
        SignInButton.IsEnabled = false;
        SignInButton.Content = "Ingresando…";
        try
        {
            var auth = new SupabaseAuthClient(_http, SyncConfig.SupabaseUrl, SyncConfig.SupabaseAnonKey);
            var session = await auth.SignInAsync(email, password);

            // Solo se persisten los tokens de sesión; la contraseña nunca se guarda.
            SecureStore.SaveSecret(SecureStore.SyncAccessTokenKey, session.AccessToken);
            SecureStore.SaveSecret(SecureStore.SyncRefreshTokenKey, session.RefreshToken);
            SecureStore.SaveSecret(SecureStore.SyncExpiresAtKey, session.ExpiresAt.ToString());
            PersistUserProfile(session);

            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"No se pudo iniciar sesión: {ex.Message}";
        }
        finally
        {
            SignInButton.IsEnabled = true;
            SignInButton.Content = "Iniciar sesión";
        }
    }

    private async void OnGoogleSignIn(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        SignInButton.IsEnabled = false;
        GoogleSignInButton.IsEnabled = false;
        var originalContent = GoogleSignInButton.Content;
        GoogleSignInButton.Content = "Conectando con Google…";

        using var cts = new CancellationTokenSource();
        _googleSignInCts = cts;
        try
        {
            var google = new GoogleOAuthClient(_http, SyncConfig.SupabaseUrl, SyncConfig.SupabaseAnonKey);
            var session = await google.SignInAsync(cts.Token);

            // Solo se persisten los tokens de sesión; nunca credenciales de Google.
            SecureStore.SaveSecret(SecureStore.SyncAccessTokenKey, session.AccessToken);
            SecureStore.SaveSecret(SecureStore.SyncRefreshTokenKey, session.RefreshToken);
            SecureStore.SaveSecret(SecureStore.SyncExpiresAtKey, session.ExpiresAt.ToString());
            PersistUserProfile(session);

            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            ErrorText.Text = "Se canceló el inicio de sesión con Google.";
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"No se pudo iniciar sesión con Google: {ex.Message}";
        }
        finally
        {
            _googleSignInCts = null;
            SignInButton.IsEnabled = true;
            GoogleSignInButton.IsEnabled = true;
            GoogleSignInButton.Content = originalContent;
        }
    }

    /// <summary>
    /// Persiste nombre/email/avatar del usuario (ver <see cref="AuthUser.DisplayName"/> y
    /// <see cref="AuthUser.AvatarUrl"/>) en <see cref="AppSettings"/> para que SyncWindow los
    /// pueda mostrar sin volver a consultar Supabase. Best-effort: si la sesión no trae "user"
    /// (no debería pasar, pero la respuesta es JSON externo) simplemente no persiste nada.
    /// </summary>
    private static void PersistUserProfile(AuthSession session)
    {
        if (session.User is null)
            return;

        var settings = AppSettings.Instance;
        settings.UserName = session.User.DisplayName;
        settings.UserEmail = session.User.Email;
        settings.UserAvatarUrl = session.User.AvatarUrl;
        settings.Save();
    }

    /// <summary>Muestra el diálogo de login. Devuelve true si la sesión quedó iniciada (y guardada).</summary>
    public static bool Show(HttpClient http)
    {
        var dialog = new LoginWindow(http) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _googleSignInCts?.Cancel();
        base.OnClosed(e);
    }
}
