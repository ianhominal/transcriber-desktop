using AudioTranscriber.App.ViewModels;
using AudioTranscriber.Core.Workspaces;

namespace AudioTranscriber.App.UiTests;

/// <summary>
/// Los comandos cuyo <c>CanExecute</c> depende de <c>SelectedProject</c> tienen que RE-EVALUARSE
/// cuando esa selección cambia. Con CommunityToolkit.Mvvm eso no es automático: hace falta declarar
/// <c>[NotifyCanExecuteChangedFor(nameof(XxxCommand))]</c> sobre la propiedad. Si falta, el botón
/// evalúa su condición UNA sola vez (al arranque, sin proyecto elegido) y se queda deshabilitado
/// para siempre, sin ningún error ni warning de compilación.
/// <para/>
/// Este archivo existe porque ese error ya se cometió DOS veces en <c>MainViewModel</c>: primero con
/// "Transcribir" (2026-07-17) y después con "Compartir" (2026-07-28, reportado por el dueño como
/// "el botón me sale disabled"). El comentario de advertencia estaba escrito en el código y no
/// alcanzó — un comentario no falla el build; un test sí.
/// </summary>
public class MainViewModelCommandGatingTests
{
    private static ProjectVm MakeProject(string name = "Demo", bool isGeneral = false) => new(new AudioProject
    {
        Name = name,
        FolderPath = $@"C:\nowhere\{name}",
        IsGeneral = isGeneral,
        Audios = Array.Empty<AudioItem>(),
    });

    [Fact]
    public void Compartir_se_habilita_al_elegir_un_proyecto() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();
            using var vm = new MainViewModel();

            Assert.False(vm.OpenShareWindowCommand.CanExecute(null));

            vm.OnTreeSelectionChanged(MakeProject());

            Assert.True(vm.OpenShareWindowCommand.CanExecute(null),
                "sin [NotifyCanExecuteChangedFor(nameof(OpenShareWindowCommand))] sobre SelectedProject, " +
                "el botón Compartir queda deshabilitado para siempre");
        });

    [Fact]
    public void Compartir_sigue_deshabilitado_en_el_proyecto_General() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();
            using var vm = new MainViewModel();

            vm.OnTreeSelectionChanged(MakeProject("General", isGeneral: true));

            Assert.False(vm.OpenShareWindowCommand.CanExecute(null),
                "el proyecto General no es compartible: las notas sueltas no tienen de dónde heredar permisos");
        });

    [Fact]
    public void Compartir_vuelve_a_deshabilitarse_al_pasar_a_General() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();
            using var vm = new MainViewModel();

            vm.OnTreeSelectionChanged(MakeProject("Trabajo"));
            Assert.True(vm.OpenShareWindowCommand.CanExecute(null));

            vm.OnTreeSelectionChanged(MakeProject("General", isGeneral: true));

            Assert.False(vm.OpenShareWindowCommand.CanExecute(null),
                "la re-evaluación tiene que funcionar en los DOS sentidos, no solo al habilitar");
        });

    // Nota sobre lo que este archivo NO verifica: soltar la selección del árbol
    // (`OnTreeSelectionChanged(null)`) deliberadamente NO limpia `SelectedProject` -- el switch de
    // ese método no tiene rama para null. Es intencional: el proyecto sigue activo como destino de
    // "agregar audio acá". Un primer intento de test asumió lo contrario y falló; el equivocado era
    // el test, no la app.

    /// <summary>
    /// El asistente de proyecto comparte exactamente el mismo gate. Se verifica junto al de
    /// Compartir para que, si alguien toca la lista de notificaciones, se caigan los dos y quede
    /// claro que el problema es la lista y no un comando puntual.
    /// </summary>
    [Fact]
    public void AsistenteDeProyecto_se_habilita_al_elegir_un_proyecto() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();
            using var vm = new MainViewModel();

            Assert.False(vm.OpenProjectAssistantCommand.CanExecute(null));

            vm.OnTreeSelectionChanged(MakeProject());

            Assert.True(vm.OpenProjectAssistantCommand.CanExecute(null));
        });
}
