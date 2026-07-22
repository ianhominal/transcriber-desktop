using AudioTranscriber.App.ViewModels;
using AudioTranscriber.Core.Workspaces;

namespace AudioTranscriber.App.UiTests;

/// <summary>
/// Cubre la lógica de "qué vista mostrar en el panel derecho según la selección del árbol" (F3):
/// nada seleccionado o audio sin transcript todavía -> placeholder; proyecto seleccionado sin
/// audio -> vista de proyecto (listado de archivos); audio seleccionado -> editor de transcripción
/// (cubierto indirectamente: ShowProjectFilesView pasa a false). Ver MainViewModel.ShowProjectFilesView
/// / ShowEmptyPlaceholder y el binding de Visibility en MainWindow.xaml.
/// <para/>
/// MainViewModel hace I/O liviano en su constructor (settings persistidos en %LOCALAPPDATA%, igual
/// que WindowInstantiationUiTests) pero ninguna llamada de red, así que es seguro instanciarlo en
/// un test. Los ProjectVm/AudioItemVm de prueba se arman a mano sobre modelos Core en memoria, sin
/// tocar el filesystem real (AudioItemVm.SizeBytes cae a 0 si el archivo no existe, ver su ctor).
/// </summary>
public class MainViewModelProjectViewTests
{
    private static ProjectVm MakeProject(string name = "Demo") => new(new AudioProject
    {
        Name = name,
        FolderPath = $@"C:\nowhere\{name}",
        IsGeneral = false,
        Audios = Array.Empty<AudioItem>(),
    });

    private static AudioItemVm MakeAudio(string fileName = "nota.wav") => new(new AudioItem
    {
        FileName = fileName,
        FullPath = $@"C:\nowhere\{fileName}",
        TranscriptPath = $@"C:\nowhere\{fileName}.txt",
    });

    [Fact]
    public void Nada_seleccionado_muestra_el_placeholder_vacio() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();
            using var vm = new MainViewModel();

            Assert.True(vm.ShowEmptyPlaceholder);
            Assert.False(vm.ShowProjectFilesView);
        });

    [Fact]
    public void Proyecto_seleccionado_sin_audio_muestra_la_vista_de_proyecto() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();
            using var vm = new MainViewModel();

            vm.OnTreeSelectionChanged(MakeProject());

            Assert.True(vm.ShowProjectFilesView);
            Assert.False(vm.ShowEmptyPlaceholder);
        });

    [Fact]
    public void Seleccionar_un_audio_oculta_la_vista_de_proyecto()
    {
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();
            using var vm = new MainViewModel();

            var project = MakeProject();
            var audio = MakeAudio();
            project.Audios.Add(audio);

            vm.OnTreeSelectionChanged(project);
            Assert.True(vm.ShowProjectFilesView);

            vm.OnTreeSelectionChanged(audio);

            Assert.False(vm.ShowProjectFilesView);
            // Sin transcript todavía (TranscriptPath no existe en disco): vuelve al placeholder,
            // igual que el comportamiento original (pre-F3) para un audio recién elegido.
            Assert.True(vm.ShowEmptyPlaceholder);
        });
    }

    [Fact]
    public void SelectProjectFile_selecciona_el_audio_elegido_desde_el_listado_de_proyecto() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();
            using var vm = new MainViewModel();

            var project = MakeProject();
            var audio = MakeAudio();
            project.Audios.Add(audio);
            vm.OnTreeSelectionChanged(project);

            // Mismo mecanismo que MainWindow.xaml.cs.OnProjectFileSelectionChanged: togglear
            // IsSelected en el AudioItemVm (bind TwoWay con TreeViewItem.IsSelected) es lo que en
            // la UI real dispara TreeView.SelectedItemChanged -> OnTreeSelectionChanged. Acá no hay
            // TreeView real montado, así que se verifica el efecto directo: IsSelected queda en
            // true, listo para que el binding lo levante.
            vm.SelectProjectFile(audio);

            Assert.True(audio.IsSelected);
        });

    // ---- Multi-select nativo de ProjectFilesList (rediseño 2026-07-22) ----------------------------
    // SetSelectedProjectFiles es lo que MainWindow.xaml.cs.OnProjectFileSelectionChanged llama con
    // ListBox.SelectedItems en cada cambio de selección (ver ese handler): acá se verifica el
    // efecto sobre el VM sin necesitar un ListBox real montado, mismo criterio que el test de
    // SelectProjectFile de arriba.

    [Fact]
    public void SetSelectedProjectFiles_actualiza_el_contador_y_el_boton_de_borrar() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();
            using var vm = new MainViewModel();
            var audio1 = MakeAudio("uno.wav");
            var audio2 = MakeAudio("dos.wav");

            vm.SetSelectedProjectFiles(Array.Empty<AudioItemVm>());
            Assert.Equal(0, vm.SelectedProjectFilesCount);
            Assert.False(vm.ShowDeleteSelectedFilesButton);

            vm.SetSelectedProjectFiles(new[] { audio1 });
            Assert.Equal(1, vm.SelectedProjectFilesCount);
            // Con 1 solo el ListBox ya abrió la nota (ver OnProjectFileSelectionChanged) y este
            // panel se oculta solo -- el botón nunca llega a mostrarse para ese caso.
            Assert.False(vm.ShowDeleteSelectedFilesButton);

            vm.SetSelectedProjectFiles(new[] { audio1, audio2 });
            Assert.Equal(2, vm.SelectedProjectFilesCount);
            Assert.True(vm.ShowDeleteSelectedFilesButton);
        });

    [Fact]
    public void DeleteSelectedFilesCommand_CanExecute_depende_de_si_hay_algo_seleccionado() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();
            using var vm = new MainViewModel();
            var audio = MakeAudio();

            Assert.False(vm.DeleteSelectedFilesCommand.CanExecute(null));

            vm.SetSelectedProjectFiles(new[] { audio });
            Assert.True(vm.DeleteSelectedFilesCommand.CanExecute(null));

            vm.SetSelectedProjectFiles(Array.Empty<AudioItemVm>());
            Assert.False(vm.DeleteSelectedFilesCommand.CanExecute(null));
        });

    [Fact]
    public void Cambiar_de_proyecto_limpia_la_seleccion_de_archivos_del_listado() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();
            using var vm = new MainViewModel();

            var projectA = MakeProject("A");
            var audioA1 = MakeAudio("a1.wav");
            var audioA2 = MakeAudio("a2.wav");
            projectA.Audios.Add(audioA1);
            projectA.Audios.Add(audioA2);
            var projectB = MakeProject("B");

            vm.OnTreeSelectionChanged(projectA);
            vm.SetSelectedProjectFiles(new[] { audioA1, audioA2 });
            Assert.Equal(2, vm.SelectedProjectFilesCount);

            // Nueva selección de proyecto: las instancias marcadas arriba ya no son las del
            // ListBox recién bindeado -- ver MainViewModel.OnSelectedProjectChanged.
            vm.OnTreeSelectionChanged(projectB);

            Assert.Equal(0, vm.SelectedProjectFilesCount);
            Assert.False(vm.ShowDeleteSelectedFilesButton);
        });
}
