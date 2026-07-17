using System.Windows;
using System.Windows.Threading;
using AudioTranscriber.Core.Updates;
using Velopack;
using Velopack.Sources;

namespace AudioTranscriber.App;

/// <summary>
/// Auto-update vía Velopack contra los releases públicos de GitHub
/// (https://github.com/ianhominal/audio-transcriber-web, tag <c>desktop-v1.0</c> y sucesivos, donde
/// se publica el instalador generado por <c>vpk pack</c> + <c>vpk upload github</c>).
/// <para/>
/// Tres entradas al mismo chequeo (ver <see cref="CheckAndDownloadCoreAsync"/>, que las tres reusan):
/// <list type="bullet">
/// <item><see cref="CheckAndDownloadAsync"/>: automático al abrir la app (<see cref="App.OnStartup"/>),
/// fire-and-forget, silencioso ante error — nadie espera su resultado.</item>
/// <item>El mismo <see cref="CheckAndDownloadAsync"/>, disparado periódicamente cada
/// <see cref="PeriodicCheckInterval"/> mientras la app sigue corriendo (ver
/// <see cref="StartPeriodicChecks"/>) — la app vive en la bandeja sin reiniciarse, así que sin esto
/// el chequeo de arranque nunca se repetía.</item>
/// <item><see cref="CheckForUpdateManualAsync"/>: manual desde el botón "Buscar actualizaciones" de
/// SettingsWindow, awaited, devuelve un <see cref="UpdateCheckResult"/> claro para mostrar en la UI.</item>
/// </list>
/// En ambos casos, si hay una versión nueva se descarga antes de devolver el control y recién ahí
/// se dispara <see cref="UpdateReady"/> para que la UI (tray/banner) ofrezca "Reiniciar y actualizar" —
/// <see cref="ApplyAndRestart"/> nunca se llama solo, siempre a pedido explícito del usuario.
/// Instancia única (<see cref="Instance"/>), mismo patrón que <see cref="SyncCoordinator"/>.
/// <para/>
/// Cada paso relevante (chequeo, descarga, aplicar/reiniciar, y cualquier excepción) queda logueado
/// best-effort vía <see cref="UpdateLogger"/> en %LOCALAPPDATA%\AudioTranscriber\logs\update-yyyyMMdd.log,
/// incluso en el camino automático silencioso — así el próximo reporte de "no busca actualizaciones
/// o falla" se puede diagnosticar sin tener que reproducirlo en la PC del usuario.
/// </summary>
public sealed class UpdateService
{
    /// <summary>Repo GitHub donde se publican los releases del instalador (fuente de <see cref="GithubSource"/>).</summary>
    private const string RepoUrl = "https://github.com/ianhominal/audio-transcriber-web";

    public static UpdateService Instance { get; } = new();

    /// <summary>
    /// Cada cuánto se re-chequea mientras la app sigue corriendo (ver <see cref="StartPeriodicChecks"/>).
    /// La app vive en la bandeja sin reiniciarse nunca, así que sin esto el único chequeo (al abrir,
    /// ver <see cref="App.OnStartup"/>) no se repetía jamás -- un usuario que deja la app abierta
    /// semanas enteras no se enteraba de versiones nuevas hasta el próximo relanzamiento manual.
    /// </summary>
    /// <remarks>
    /// `const`, NO `static readonly` (en horas, y se convierte a TimeSpan al usarlo). Bugfix
    /// 2026-07-15, reportado en producción como "la notificación de actualización sale cada 2
    /// segundos":
    ///
    /// `Instance` (línea de arriba) es un inicializador estático declarado ANTES que este campo, y
    /// los inicializadores estáticos de C# corren EN ORDEN DE DECLARACIÓN. O sea que el
    /// constructor corría cuando este campo todavía valía `default(TimeSpan)` = `TimeSpan.Zero`, y
    /// un DispatcherTimer con intervalo cero dispara en CADA pasada del dispatcher: miles de ticks
    /// por segundo, notificación repetida, CPU quemada y un log de 5,3 millones de líneas.
    ///
    /// Una `const` se resuelve en compilación y se incrusta en el call site, así que es inmune al
    /// orden de inicialización. La guarda del constructor lo verifica igual.
    /// </remarks>
    private const double PeriodicCheckHours = 4;

    private readonly DispatcherTimer _periodicCheckTimer;

    /// <summary>
    /// Versión que YA se notificó (ver el aviso en <see cref="CheckAndDownloadCoreAsync"/>). Evita
    /// repetir el mismo aviso en cada chequeo mientras la actualización siga sin aplicarse.
    /// </summary>
    private string? _notifiedVersion;

    private UpdateService()
    {
        // DispatcherTimer (no System.Threading.Timer): captura el Dispatcher del hilo de UI en el
        // que se construye esta instancia (siempre el de UI -- ver Instance, primer acceso desde
        // App.OnStartup), así el Tick ya cae en ese mismo hilo sin marshaling manual, mismo
        // criterio que ya usa SyncCoordinator._periodicTimer para su propio pull cada 60s.
        var interval = TimeSpan.FromHours(PeriodicCheckHours);

        // Red de seguridad ante el bug de arriba: un intervalo cero/negativo hace que el timer
        // dispare sin parar. Si algún día alguien vuelve a romper esto, que reviente acá y no en la
        // máquina del usuario a razón de miles de ticks por segundo.
        if (interval <= TimeSpan.Zero)
            throw new InvalidOperationException($"El intervalo del chequeo periódico debe ser mayor a cero (era {interval}).");

        _periodicCheckTimer = new DispatcherTimer { Interval = interval };
        _periodicCheckTimer.Tick += (_, _) =>
        {
            UpdateLogger.Log("Periodic check timer tick: triggering CheckAndDownloadAsync.");
            _ = CheckAndDownloadAsync();
        };
    }

    /// <summary>
    /// Arranca el re-chequeo periódico (cada <see cref="PeriodicCheckInterval"/>). Llamado una sola
    /// vez desde <see cref="App.OnStartup"/>, después del chequeo inicial -- reusa
    /// <see cref="CheckAndDownloadAsync"/> tal cual (mismo fire-and-forget silencioso, y el
    /// <see cref="_checkGate"/> ya descarta cualquier solape con un chequeo manual o con el de
    /// arranque que sigan corriendo). Idempotente (reiniciar un timer ya arrancado no hace nada raro).
    /// </summary>
    public void StartPeriodicChecks() => _periodicCheckTimer.Start();

    /// <summary>Detiene el timer periódico (ver <see cref="App.OnExit"/>). Idempotente.</summary>
    public void StopPeriodicChecks() => _periodicCheckTimer.Stop();

    /// <summary>
    /// Se dispara en el hilo que llamó al chequeo (el caller es responsable de marshalear a UI si
    /// hace falta — ver los comentarios de <see cref="TrayIconService.OnUpdateReady"/> y
    /// <see cref="AudioTranscriber.App.ViewModels.MainViewModel"/>) cuando ya hay una actualización
    /// DESCARGADA y lista para instalar. El string es la versión nueva (para mostrar en el mensaje).
    /// </summary>
    public event Action<string>? UpdateReady;

    /// <summary>
    /// Último resultado conocido de un chequeo (manual, automático al abrir, o periódico) — null
    /// hasta que el primero termine. Pensado para que la UI muestre un estado PASIVO sin tener que
    /// disparar su propio chequeo (ver <see cref="AudioTranscriber.Core.Updates.UpdateUiTextFormatter.FormatPassiveStatus"/>
    /// y su uso en SettingsWindow). No se actualiza con el resultado transitorio de "ya hay una
    /// verificación en curso" (ver <see cref="CheckAndDownloadCoreAsync"/>) -- ese no es un
    /// resultado real, solo indica que este disparo se descartó.
    /// </summary>
    public UpdateCheckResult? LastResult { get; private set; }

    /// <summary>
    /// Se dispara cada vez que un chequeo (cualquiera de los tres orígenes) termina con un
    /// resultado real (ver <see cref="LastResult"/>). Igual que <see cref="UpdateReady"/>, el
    /// caller es responsable de marshalear a UI si hace falta.
    /// </summary>
    public event Action<UpdateCheckResult>? CheckCompleted;

    /// <summary>Guarda <see cref="LastResult"/>, avisa por <see cref="CheckCompleted"/> y devuelve el mismo resultado (para poder envolver cada <c>return</c> sin duplicar las dos líneas).</summary>
    private UpdateCheckResult RecordResult(UpdateCheckResult result)
    {
        LastResult = result;
        CheckCompleted?.Invoke(result);
        return result;
    }

    /// <summary>
    /// Serializa <see cref="CheckAndDownloadCoreAsync"/>: el chequeo automático (al abrir la app) y
    /// el manual (botón "Buscar actualizaciones") pueden solaparse. Sin este freno, dos llamadas
    /// concurrentes podían escribir <see cref="_manager"/>/<see cref="_pendingUpdate"/> de forma
    /// entrelazada, y <see cref="ApplyAndRestart"/> terminar aplicando un asset que no corresponde
    /// al manager guardado. Mismo patrón que <see cref="SyncCoordinator"/>._syncGate: un disparo que
    /// llega mientras otro chequeo ya está corriendo se descarta (no se encola ni se espera).
    /// </summary>
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    private UpdateManager? _manager;
    private VelopackAsset? _pendingUpdate;

    /// <summary>
    /// Chequeo automático al abrir la app: fire-and-forget, best-effort a propósito (ver
    /// <see cref="CheckAndDownloadCoreAsync"/> para el detalle de qué casos silencia). El resultado
    /// no le importa a nadie acá — solo dispara <see cref="UpdateReady"/> si encontró y descargó algo.
    /// </summary>
    public async Task CheckAndDownloadAsync() => await CheckAndDownloadCoreAsync();

    /// <summary>
    /// Chequeo MANUAL desde el botón "Buscar actualizaciones" de SettingsWindow: mismo chequeo que
    /// el automático (mismo <see cref="UpdateManager"/> + <see cref="GithubSource"/>, mismo
    /// <see cref="RepoUrl"/>), pero acá el caller SÍ espera un resultado claro para mostrar en la UI
    /// (a diferencia de <see cref="CheckAndDownloadAsync"/>, que es silencioso).
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateManualAsync() => await CheckAndDownloadCoreAsync();

    /// <summary>
    /// Chequea si hay una versión nueva y, si la hay, la descarga antes de devolver el control.
    /// Casos esperados que se resuelven como <see cref="UpdateCheckResult.Error"/> con un mensaje
    /// claro (nunca tiran excepción hacia el caller): sin conexión, GitHub caído/rate-limited, o
    /// corriendo sin instalar vía Velopack (<see cref="UpdateManager.IsInstalled"/> false, p.ej. F5
    /// desde el IDE o el .exe portátil viejo). El caller de <see cref="CheckAndDownloadAsync"/>
    /// ignora el resultado (silencioso a propósito); el de <see cref="CheckForUpdateManualAsync"/>
    /// lo usa para actualizar el texto del botón.
    /// </summary>
    private async Task<UpdateCheckResult> CheckAndDownloadCoreAsync()
    {
        if (!await _checkGate.WaitAsync(0))
        {
            UpdateLogger.Log("CheckAndDownloadCoreAsync: ya hay un chequeo en curso -- se descarta este disparo.");
            return UpdateCheckResult.Error("Ya hay una verificación de actualizaciones en curso.");
        }

        try
        {
            UpdateLogger.Log("CheckAndDownloadCoreAsync: begin.");
            try
            {
                var manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
                if (!manager.IsInstalled)
                {
                    // Caso esperado y correcto (no un error real): corre suelto/portátil o F5 desde el
                    // IDE, Velopack no tiene desde dónde aplicar un update. El mensaje anterior ("No se
                    // puede buscar actualizaciones en esta instalación...") era técnicamente correcto
                    // pero confundía al usuario común, que terminaba sin entender qué hacer al respecto.
                    UpdateLogger.Log("CheckAndDownloadCoreAsync: not installed (portable/dev run) — skipping check.");
                    return RecordResult(UpdateCheckResult.Error(
                        "Estás usando la versión portátil. Instalá la app para recibir actualizaciones automáticas."));
                }

                var updateInfo = await manager.CheckForUpdatesAsync();
                if (updateInfo is null)
                {
                    var currentVersion = manager.CurrentVersion?.ToString() ?? "?";
                    UpdateLogger.Log($"CheckAndDownloadCoreAsync: up to date (current={currentVersion}).");
                    return RecordResult(UpdateCheckResult.UpToDate(currentVersion));
                }

                var newVersion = updateInfo.TargetFullRelease.Version.ToString();
                UpdateLogger.Log($"CheckAndDownloadCoreAsync: update found (version={newVersion}). Downloading...");
                await manager.DownloadUpdatesAsync(updateInfo);
                UpdateLogger.Log($"CheckAndDownloadCoreAsync: download complete (version={newVersion}).");

                _manager = manager;
                _pendingUpdate = updateInfo.TargetFullRelease;

                // Avisar UNA sola vez por versión. Mientras la actualización siga sin aplicarse,
                // cada chequeo la vuelve a encontrar; sin esta guarda, cada uno disparaba otra
                // notificación para la MISMA versión (reportado en producción). El banner de la
                // ventana y el ítem del menú de la bandeja siguen visibles igual — eso es estado,
                // no un aviso, y no molesta.
                if (_notifiedVersion != newVersion)
                {
                    _notifiedVersion = newVersion;
                    UpdateReady?.Invoke(newVersion);
                }
                else
                {
                    UpdateLogger.Log($"CheckAndDownloadCoreAsync: {newVersion} ya fue notificada — no se vuelve a avisar.");
                }

                return RecordResult(UpdateCheckResult.Available(newVersion));
            }
            catch (Exception ex)
            {
                // El resultado hacia el caller sigue siendo el mismo mensaje genérico a propósito (ver
                // comentario del método): sin conexión, GitHub caído, etc. no deben tirar abajo la app
                // ni el chequeo automático, y el detalle de la excepción no le sirve al usuario (no hay
                // acción que pueda tomar con él). Pero SÍ queda logueado acá con el detalle completo —
                // antes esta excepción se perdía del todo, sin dejar ningún rastro para diagnosticar el
                // reporte "no busca actualizaciones o falla".
                UpdateLogger.Log($"CheckAndDownloadCoreAsync: FAILED — {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
                // El mensaje se deriva de la excepción REAL (ver UpdateErrorFormatter): el texto fijo
                // "revisá tu conexión" mandó a un usuario a revisar su router durante horas cuando lo
                // que pasaba era un 429 de GitHub. Sigue sin filtrarse nada crudo.
                return RecordResult(UpdateCheckResult.Error(UpdateErrorFormatter.Describe(ex)));
            }
        }
        finally
        {
            _checkGate.Release();
        }
    }

    /// <summary>
    /// Aplica la actualización ya descargada y reinicia la app. No hace nada si no hay ninguna
    /// pendiente (p.ej. si se llama sin haber pasado antes por un chequeo que haya encontrado algo).
    /// <para/>
    /// Patrón recomendado por Velopack (<c>UpdateManager.ApplyUpdatesAndRestart</c>, ver
    /// docs.velopack.io): en el camino feliz este método NUNCA retorna -- sale del proceso actual de
    /// inmediato, aplica la actualización y relanza la app nueva. Solo puede volver acá si algo tira
    /// ANTES de ese exit (Update.exe faltante/bloqueado por un antivirus, sin permisos, disco lleno,
    /// etc.). Antes, esa excepción se perdía silenciosa hacia arriba y terminaba en el handler
    /// genérico de <see cref="App.OnUnhandledException"/> ("Ocurrió un error inesperado", sin
    /// contexto de que el problema era la actualización) -- de ahí que el usuario, sin entender qué
    /// pasó, terminara resignándose al .exe portátil. El try/catch acá deja un mensaje claro y
    /// específico, y un rastro en <see cref="UpdateLogger"/> con el detalle real para diagnosticarlo.
    /// </summary>
    public void ApplyAndRestart()
    {
        if (_manager is null || _pendingUpdate is null)
        {
            UpdateLogger.Log("ApplyAndRestart: called with no pending update — no-op.");
            return;
        }

        var version = _pendingUpdate.Version.ToString();
        UpdateLogger.Log($"ApplyAndRestart: applying update (version={version})...");
        try
        {
            _manager.ApplyUpdatesAndRestart(_pendingUpdate);
        }
        catch (Exception ex)
        {
            UpdateLogger.Log($"ApplyAndRestart: FAILED (version={version}) — {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
            CrashLogger.Log(ex);
            MessageBox.Show(
                "No se pudo aplicar la actualización. Cerrá la app y volvé a abrirla para reintentar, " +
                "o descargá el instalador manualmente si el problema sigue.",
                "No se pudo actualizar",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
