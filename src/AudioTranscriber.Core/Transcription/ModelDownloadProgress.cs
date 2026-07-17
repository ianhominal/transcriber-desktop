namespace AudioTranscriber.Core.Transcription;

/// <summary>Progreso de descarga del modelo, para mostrar una barra real en la UI.</summary>
public readonly record struct ModelDownloadProgress(long BytesReceived, long TotalBytes)
{
    /// <summary>Porcentaje 0–100. 0 si el total es desconocido.</summary>
    public double Percent => TotalBytes > 0 ? BytesReceived * 100.0 / TotalBytes : 0;

    public double MegabytesReceived => BytesReceived / 1024.0 / 1024.0;
    public double TotalMegabytes => TotalBytes / 1024.0 / 1024.0;
}
