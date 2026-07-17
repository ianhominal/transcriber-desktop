using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class RemoteMapperTests
{
    private readonly RemoteMapper _mapper = new();

    [Fact]
    public void Map_ProyectosYTranscripciones_GeneraEstadoNoborrado()
    {
        var response = new PullResponse
        {
            Projects =
            {
                new RemoteProject { Id = "p1", Name = "Trabajo", UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(100) },
            },
            Transcriptions =
            {
                new RemoteTranscription { Id = "t1", ProjectId = "p1", Text = "hola", UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(200) },
            },
        };

        var map = _mapper.Map(response);

        Assert.Equal(2, map.Count);
        Assert.Equal(SyncItemKind.Project, map["p1"].Kind);
        Assert.False(map["p1"].Deleted);
        Assert.Equal(SyncItemKind.Transcription, map["t1"].Kind);
        Assert.False(map["t1"].Deleted);
    }

    [Fact]
    public void Map_ProyectoConDeletedAt_MarcaDeletedTrue()
    {
        var response = new PullResponse
        {
            Projects =
            {
                new RemoteProject
                {
                    Id = "p1", Name = "Trabajo",
                    UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(100),
                    DeletedAt = DateTimeOffset.FromUnixTimeSeconds(150),
                },
            },
        };

        var map = _mapper.Map(response);

        Assert.True(map["p1"].Deleted);
    }

    [Fact]
    public void Map_TranscripcionConDeletedAt_MarcaDeletedTrue()
    {
        var response = new PullResponse
        {
            Transcriptions =
            {
                new RemoteTranscription
                {
                    Id = "t1", Text = "hola",
                    UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(100),
                    DeletedAt = DateTimeOffset.FromUnixTimeSeconds(150),
                },
            },
        };

        var map = _mapper.Map(response);

        Assert.True(map["t1"].Deleted);
    }

    [Fact]
    public void Map_MismoContenido_ProduceMismoHash()
    {
        var a = new RemoteProject { Id = "p1", Name = "Trabajo", Description = "desc", UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(100) };
        var b = new RemoteProject { Id = "p1", Name = "Trabajo", Description = "desc", UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(999) };

        var map1 = _mapper.Map(new PullResponse { Projects = { a } });
        var map2 = _mapper.Map(new PullResponse { Projects = { b } });

        Assert.Equal(map1["p1"].ContentHash, map2["p1"].ContentHash);
    }

    // ---- Fix 2026-07-08 (v1.0.16): ContentHash must reflect audio-URL presence -----------------
    // Root cause of the "self-healing" bug: the hash formula ignored AudioUrlSigned entirely, so a
    // transcription that transitions from "no signed URL" to "signed URL present" on a later pull
    // produced the SAME hash as the stored baseline. SyncPlanner never saw a change, so the audio
    // download was never retried even though the backend now had a valid URL.

    [Fact]
    public void Map_TranscriptionWithAudioUrlSigned_ProducesDifferentHashThanWithoutIt()
    {
        var withoutAudio = new RemoteTranscription
        {
            Id = "t1", ProjectId = "p1", Title = "Nota", AudioName = "nota.mp3", Text = "hola",
            UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(100),
        };
        var withAudio = new RemoteTranscription
        {
            Id = "t1", ProjectId = "p1", Title = "Nota", AudioName = "nota.mp3", Text = "hola",
            AudioUrlSigned = "https://storage.example.com/signed/nota.mp3?token=abc123",
            UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(100),
        };

        var mapWithout = _mapper.Map(new PullResponse { Transcriptions = { withoutAudio } });
        var mapWith = _mapper.Map(new PullResponse { Transcriptions = { withAudio } });

        Assert.NotEqual(mapWithout["t1"].ContentHash, mapWith["t1"].ContentHash);
    }

    [Fact]
    public void Map_TranscriptionWithReSignedAudioUrl_ProducesSameHash()
    {
        // Only the PRESENCE of a signed URL should affect the hash, not its VALUE -- a re-signed
        // URL (new token/expiry) for the same item on a later pull must NOT look like a "change",
        // otherwise the audio would oscillate (re-download every cycle) instead of downloading once.
        var first = new RemoteTranscription
        {
            Id = "t1", ProjectId = "p1", Title = "Nota", AudioName = "nota.mp3", Text = "hola",
            AudioUrlSigned = "https://storage.example.com/signed/nota.mp3?token=abc123",
            UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(100),
        };
        var reSigned = new RemoteTranscription
        {
            Id = "t1", ProjectId = "p1", Title = "Nota", AudioName = "nota.mp3", Text = "hola",
            AudioUrlSigned = "https://storage.example.com/signed/nota.mp3?token=xyz-new-expiry",
            UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(200),
        };

        var map1 = _mapper.Map(new PullResponse { Transcriptions = { first } });
        var map2 = _mapper.Map(new PullResponse { Transcriptions = { reSigned } });

        Assert.Equal(map1["t1"].ContentHash, map2["t1"].ContentHash);
    }
}
