using System;
using System.IO;
using AudioTranscriber.Core.Workspaces;
using Xunit;

namespace AudioTranscriber.Core.Tests;

public class WorkspaceErrorFormatterTests
{
    /// The exact exception a real user hit: FileSystemWatcher on a folder that did not exist yet.
    private static ArgumentException RealWatcherFailure() =>
        new("The directory name 'C:\\Users\\Sofia\\OneDrive\\Documentos\\AudioTranscriber' does not exist.", "path");

    [Fact]
    public void AddFiles_NeverLeaksTheRawExceptionText()
    {
        var message = WorkspaceErrorFormatter.FriendlyAddFilesError(RealWatcherFailure());

        Assert.DoesNotContain("Parameter", message);
        Assert.DoesNotContain("directory name", message);
        Assert.DoesNotContain("C:\\Users", message);
    }

    [Fact]
    public void AddFiles_MissingFolder_TellsTheUserWhatToDo()
    {
        var message = WorkspaceErrorFormatter.FriendlyAddFilesError(RealWatcherFailure());

        Assert.Contains("carpeta de trabajo", message);
        Assert.Contains("Configuración", message);
    }

    [Fact]
    public void AddFiles_DirectoryNotFound_IsTreatedAsAMissingFolder()
    {
        var message = WorkspaceErrorFormatter.FriendlyAddFilesError(new DirectoryNotFoundException("nope"));

        Assert.Contains("No encontramos la carpeta de trabajo", message);
    }

    [Fact]
    public void AddFiles_PermissionDenied_SuggestsAnotherFolder()
    {
        var message = WorkspaceErrorFormatter.FriendlyAddFilesError(new UnauthorizedAccessException("denied"));

        Assert.Contains("permiso", message);
        Assert.Contains("Configuración", message);
    }

    [Fact]
    public void AddFiles_IoFailure_MentionsCloudSyncAsALikelyCause()
    {
        var message = WorkspaceErrorFormatter.FriendlyAddFilesError(new IOException("locked"));

        Assert.Contains("sincronizando", message);
    }

    [Fact]
    public void AddFiles_UnknownFailure_FallsBackToAGenericMessage()
    {
        var message = WorkspaceErrorFormatter.FriendlyAddFilesError(new InvalidOperationException("boom"));

        Assert.Equal("No se pudieron agregar los archivos. Probá de nuevo.", message);
    }

    [Fact]
    public void Recording_MissingFolder_TellsTheUserWhatToDo()
    {
        var message = WorkspaceErrorFormatter.FriendlyRecordingError(RealWatcherFailure());

        Assert.Contains("carpeta de trabajo", message);
        Assert.DoesNotContain("Parameter", message);
    }

    [Fact]
    public void Recording_UnknownFailure_FallsBackToARecordingSpecificMessage()
    {
        var message = WorkspaceErrorFormatter.FriendlyRecordingError(new InvalidOperationException("boom"));

        Assert.Equal("No se pudo empezar a grabar. Probá de nuevo.", message);
    }
}
