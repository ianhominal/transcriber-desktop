using System.IO;
using AudioTranscriber.Core.Workspaces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioTranscriber.App.ViewModels;

/// <summary>Envoltorio observable de <see cref="AudioItem"/> para la lista de la UI.</summary>
public partial class AudioItemVm : ObservableObject
{
    /// <summary>Modelo original (para operaciones de workspace: mover, renombrar, borrar).</summary>
    public AudioItem Model { get; }

    public string FileName { get; }
    public string FullPath { get; }
    public string TranscriptPath { get; }

    /// <summary>
    /// False para transcripciones SOLO TEXTO (sin archivo de audio real, ver
    /// <see cref="AudioItem.HasAudio"/>): la UI debe deshabilitar/ocultar reproducir, transcribir y
    /// cualquier operación que dependa de un archivo de audio real.
    /// </summary>
    public bool HasAudio { get; }

    /// <summary>Peso del archivo en bytes (0 si no se pudo leer, o si no hay audio real).</summary>
    public long SizeBytes { get; }

    [ObservableProperty]
    private bool _hasTranscript;

    /// <summary>Selección en el árbol (bindeado a TreeViewItem.IsSelected).</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Id remoto (Supabase) de esta transcripción, CONFIRMADO por el último sync exitoso (ver
    /// <see cref="ViewModels.MainViewModel"/>, método privado que resuelve esto tras cada
    /// <c>RefreshAudios</c> usando la baseline de <c>SyncIndex</c> -- solo se completa acá si el
    /// item efectivamente terminó un ciclo de sync, no apenas se creó localmente). Null cuando la
    /// nota todavía no se sincronizó nunca: la UI usa esto para deshabilitar "Abrir con IA" (no
    /// existe URL remota confiable todavía).
    /// </summary>
    [ObservableProperty]
    private string? _remoteId;

    /// <summary>True si <see cref="RemoteId"/> ya se resolvió (nota confirmada en el servidor).</summary>
    public bool IsSyncedRemotely => !string.IsNullOrEmpty(RemoteId);

    partial void OnRemoteIdChanged(string? value) => OnPropertyChanged(nameof(IsSyncedRemotely));

    /// <summary>
    /// Fecha de creación LOCAL del .txt de transcript, usada como proxy de "cuándo se capturó la
    /// nota" para el "Resurfacing" (ver <see cref="AudioTranscriber.Core.Notes.ResurfaceCandidatePicker"/>).
    /// Null si todavía no hay transcript en disco. Misma limitación que documenta la web para su
    /// propio <c>created_at</c> (no hay tracking de "última vez abierta") -- acá, además, no hay
    /// columna en ninguna base: es la fecha del archivo tal cual la ve el sistema de archivos.
    /// </summary>
    public DateTime? CreatedAtLocal { get; }

    public AudioItemVm(AudioItem item)
    {
        Model = item;
        FileName = item.FileName;
        FullPath = item.FullPath;
        TranscriptPath = item.TranscriptPath;
        HasAudio = item.HasAudio;
        _hasTranscript = item.HasTranscript;

        if (HasAudio)
        {
            try { SizeBytes = new FileInfo(item.FullPath).Length; }
            catch { SizeBytes = 0; }
        }

        if (_hasTranscript)
        {
            try { CreatedAtLocal = File.GetCreationTime(TranscriptPath); }
            catch { CreatedAtLocal = null; }
        }
    }

    /// <summary>Peso legible: KB o MB.</summary>
    public string SizeText => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024.0:0.0} MB"
        : $"{SizeBytes / 1024.0:0} KB";

    /// <summary>Marca visual en la lista: ✓ si ya tiene transcript, ● si está pendiente.</summary>
    public string StatusGlyph => HasTranscript ? "✓" : "●";

    partial void OnHasTranscriptChanged(bool value) => OnPropertyChanged(nameof(StatusGlyph));
}
