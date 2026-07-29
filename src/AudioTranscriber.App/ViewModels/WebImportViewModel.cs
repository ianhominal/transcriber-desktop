using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using AudioTranscriber.Core.WebImport;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioTranscriber.App.ViewModels;

/// <summary>
/// "Transcribir desde una URL" (App/WebImportWindow): pegar una URL, analizarla con yt-dlp (sin
/// descargar nada todavía), elegir uno o más ítems de la lista, y descargarlos. Cada archivo
/// descargado se entrega vía <see cref="_onFileDownloaded"/> a <c>MainViewModel</c>, que lo mete al
/// proyecto elegido POR EL MISMO CAMINO que "Cargar archivo(s)"/arrastrar y soltar
/// (<c>MainViewModel.AddDroppedFiles</c>) -- este ViewModel no toca workspaces ni la cola de
/// transcripción, solo analiza/descarga y avisa.
/// <para/>
/// yt-dlp es una herramienta externa (~17 MB) que se baja de GitHub la primera vez
/// (<see cref="YtDlpProvider"/>): antes de bajarla se pide consentimiento explícito
/// (<see cref="AppSettings.YtDlpConsentGiven"/>) -- una sola vez, nunca en silencio.
/// </summary>
public sealed partial class WebImportViewModel : ObservableObject
{
    private readonly WebPageAnalyzer _analyzer;
    private readonly WebAudioDownloader _downloader;
    private readonly YtDlpProvider _provider;
    private readonly AppSettings _settings;
    private readonly string _downloadTempDir;
    private readonly Action<string> _onFileDownloaded;

    private CancellationTokenSource? _cts;

    public WebImportViewModel(
        WebPageAnalyzer analyzer,
        WebAudioDownloader downloader,
        YtDlpProvider provider,
        AppSettings settings,
        string downloadTempDir,
        Action<string> onFileDownloaded)
    {
        _analyzer = analyzer;
        _downloader = downloader;
        _provider = provider;
        _settings = settings;
        _downloadTempDir = downloadTempDir;
        _onFileDownloaded = onFileDownloaded;
    }

    public ObservableCollection<WebImportItemVm> Items { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    private string _url = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    /// <summary>Cuántos ítems tiene tildados la usuaria ahora mismo -- ver <see cref="OnItemSelectionChanged"/>.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private int _selectedCount;

    [ObservableProperty]
    private string? _playlistTitle;

    [ObservableProperty]
    private string _statusMessage = "Pegá la URL de un video, audio o playlist y tocá \"Analizar\".";

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _showProgress;

    private bool CanAnalyze() => !IsBusy && WebImportViewState.CanAnalyze(Url);

    /// <summary>
    /// Analiza la URL con yt-dlp (sin descargar nada) y llena <see cref="Items"/>. Cada estado de
    /// error de <see cref="WebImportResult"/> ya trae su mensaje en español rioplatense listo para
    /// mostrar tal cual -- ver el comentario de <see cref="WebImportStatus"/> (Core).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        IsBusy = true;
        Items.Clear();
        PlaylistTitle = null;
        StatusMessage = "Analizando…";
        _cts = new CancellationTokenSource();

        try
        {
            if (!await EnsureYtDlpReadyAsync(_cts.Token))
                return;

            var result = await _analyzer.AnalyzeAsync(Url, _cts.Token);
            if (result.Status != WebImportStatus.Success || result.Analysis is null)
            {
                StatusMessage = result.ErrorMessage ?? "No se pudo analizar la URL.";
                return;
            }

            PlaylistTitle = result.Analysis.IsPlaylist ? result.Analysis.PlaylistTitle : null;
            foreach (var item in result.Analysis.Items)
                Items.Add(new WebImportItemVm(item, OnItemSelectionChanged));

            StatusMessage = Items.Count == 1
                ? "Se encontró 1 elemento. Elegilo y tocá \"Descargar y transcribir\"."
                : $"Se encontraron {Items.Count} elementos. Elegí uno o más y tocá \"Descargar y transcribir\".";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Análisis cancelado.";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnItemSelectionChanged() => SelectedCount = Items.Count(i => i.IsSelected);

    private bool CanConfirm() => !IsBusy && SelectedCount > 0;

    /// <summary>
    /// Descarga cada ítem tildado (de a uno, para poder mostrar progreso individual) y lo entrega a
    /// <see cref="_onFileDownloaded"/> apenas termina -- no espera a que terminen TODOS para que el
    /// primero ya quede visible en el proyecto. Un ítem que falla no corta a los demás: sigue con el
    /// próximo y el mensaje final resume cuántos entraron.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
            return;

        IsBusy = true;
        ShowProgress = true;
        _cts = new CancellationTokenSource();

        int done = 0, failed = 0;
        try
        {
            if (!await EnsureYtDlpReadyAsync(_cts.Token))
                return;

            Directory.CreateDirectory(_downloadTempDir);

            for (var i = 0; i < selected.Count; i++)
            {
                var vm = selected[i];
                StatusMessage = $"Descargando \"{vm.Title}\" ({i + 1}/{selected.Count})…";
                ProgressPercent = 0;

                var progress = new Progress<WebDownloadProgress>(p => ProgressPercent = p.Percent);
                var dl = await _downloader.DownloadAsync(vm.Item, Url, _downloadTempDir, progress, _cts.Token);

                if (dl.Status == WebImportStatus.Success && dl.FilePath is not null)
                {
                    _onFileDownloaded(dl.FilePath);
                    done++;
                }
                else
                {
                    failed++;
                    StatusMessage = dl.ErrorMessage ?? "No se pudo descargar ese elemento.";
                }
            }

            StatusMessage = failed == 0
                ? $"Se importaron {done} audio(s)."
                : $"Se importaron {done} audio(s); {failed} falló(aron).";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"Descarga cancelada ({done} importado(s) antes de cancelar).";
        }
        finally
        {
            IsBusy = false;
            ShowProgress = false;
            ProgressPercent = 0;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    /// <summary>
    /// Consentimiento explícito antes de bajar yt-dlp (pedido del dueño: nunca se descarga algo de
    /// internet sin avisar). Se pregunta UNA sola vez -- ver <see cref="AppSettings.YtDlpConsentGiven"/>.
    /// Devuelve false (sin tocar <see cref="StatusMessage"/> más que para explicar por qué) si la
    /// usuaria no acepta o si la descarga en sí falla.
    /// </summary>
    private async Task<bool> EnsureYtDlpReadyAsync(CancellationToken ct)
    {
        if (_provider.IsAvailable)
            return true;

        if (!_settings.YtDlpConsentGiven)
        {
            var accepted = MessageBox.Show(
                "Para analizar y descargar audio desde una URL hace falta yt-dlp, una herramienta "
                + "externa y gratuita (~17 MB) que se descarga una sola vez desde GitHub. No se vuelve "
                + "a pedir después de aceptar.\n\n¿Querés descargarla ahora?",
                "Descargar yt-dlp",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;

            if (!accepted)
            {
                StatusMessage = "Hace falta descargar yt-dlp para continuar. Cancelado.";
                return false;
            }

            _settings.YtDlpConsentGiven = true;
            _settings.Save();
        }

        StatusMessage = "Descargando yt-dlp (única vez, ~17 MB)…";
        ShowProgress = true;
        var progress = new Progress<YtDlpDownloadProgress>(p =>
        {
            ProgressPercent = p.Percent;
            StatusMessage = $"Descargando yt-dlp: {p.Percent:0}%…";
        });

        try
        {
            await _provider.EnsureAvailableAsync(progress, ct);
            return true;
        }
        catch (YtDlpDownloadException ex)
        {
            StatusMessage = ex.Message;
            return false;
        }
        finally
        {
            ShowProgress = false;
            ProgressPercent = 0;
        }
    }
}
