namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Convierte la respuesta de <c>/api/sync/pull</c> (<see cref="PullResponse"/>) en el mapa
/// genérico que espera <see cref="SyncPlanner"/>. Deleted = true cuando el registro remoto
/// tiene <c>deleted_at</c> (tombstone server-side).
/// </summary>
public sealed class RemoteMapper
{
    public Dictionary<string, SyncItemState> Map(PullResponse response)
    {
        var result = new Dictionary<string, SyncItemState>();

        foreach (var p in response.Projects)
        {
            result[p.Id] = new SyncItemState(
                p.Id,
                SyncItemKind.Project,
                ContentHasher.Hash(p.Name, p.Icon, p.Description),
                p.UpdatedAt,
                Deleted: p.DeletedAt is not null);
        }

        foreach (var t in response.Transcriptions)
        {
            // Fix 2026-07-08 (v1.0.16): include whether the pull carries a downloadable audio URL.
            // Without this, the hash was blind to audio presence, so a transcription that went from
            // "no audio_url_signed" (transient backend failure) to "audio_url_signed present" on a
            // later pull produced the SAME hash as the stored baseline -- SyncPlanner never saw a
            // change and the audio was orphaned forever. Only the PRESENCE is hashed (not the
            // signed URL's VALUE, which changes every pull with a new token/expiry) so an item with
            // audio already downloaded stays stable cycle after cycle -- no oscillation.
            var hasAudioSigned = !string.IsNullOrEmpty(t.AudioUrlSigned);
            result[t.Id] = new SyncItemState(
                t.Id,
                SyncItemKind.Transcription,
                ContentHasher.Hash(t.Title, t.AudioName, t.Text, t.ProjectId, hasAudioSigned.ToString()),
                t.UpdatedAt,
                Deleted: t.DeletedAt is not null);
        }

        return result;
    }
}
