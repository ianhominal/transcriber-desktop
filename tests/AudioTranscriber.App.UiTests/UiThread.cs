using System.Windows.Threading;

namespace AudioTranscriber.App.UiTests;

/// <summary>
/// Un único thread STA persistente (con Dispatcher corriendo) para TODO el proceso de tests.
/// <para/>
/// Se probó primero con [StaFact] (Xunit.StaFact), que arranca un thread STA NUEVO por cada método
/// de test. Eso choca con System.Windows.Application: es un singleton con afinidad fija al thread
/// que lo construyó (System.Windows.Application..ctor asigna su Dispatcher a
/// Dispatcher.CurrentDispatcher de ESE thread para siempre). Cualquier operación que exija ese
/// dispatcher específico — antes H.NotifyIcon.TaskbarIcon..ctor() (reemplazado por
/// System.Windows.Forms.NotifyIcon, ver TrayIconService), hoy TrayIconService.RequestExit tocando
/// Application.Current.Shutdown() — revienta con "El subproceso que realiza la llamada no puede
/// obtener acceso a este objeto porque el propietario es otro subproceso." en
/// CUALQUIER test STA que no sea, casualmente, el primero en crear la Application. Peor: bajo carga
/// se vieron incluso condiciones de carrera reales entre el chequeo "¿ya existe Application.Current?"
/// y la construcción, con varios tests fallando con "No se puede crear más de una instancia
/// Application en el mismo AppDomain" — evidencia de que distintos threads STA de [StaFact]
/// corrían la construcción casi en simultáneo pese a CollectionBehavior(DisableTestParallelization).
/// <para/>
/// La solución robusta: UN solo thread STA vivo (con Dispatcher.Run()) para toda la vida del
/// proceso de test, creado una única vez de forma perezosa y con lock. TODO el trabajo de UI de
/// TODOS los tests se marshalea a este mismo thread vía <see cref="Invoke"/>
/// (Dispatcher.Invoke), así que Application (y cualquier otra cosa afín a su thread, como
/// TaskbarIcon) siempre corre en el thread que realmente la creó.
/// </summary>
internal static class UiThread
{
    private static readonly Lazy<Dispatcher> LazyDispatcher = new(StartDispatcherThread, isThreadSafe: true);

    private static Dispatcher StartDispatcherThread()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "AudioTranscriber.UiTests.UiThread",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        ready.Wait();
        return dispatcher!;
    }

    /// <summary>
    /// Ejecuta <paramref name="action"/> de forma síncrona en el thread STA dedicado. Cualquier
    /// excepción que tire la acción se re-propaga tal cual al thread que llama (comportamiento
    /// estándar de Dispatcher.Invoke), así que Assert.* dentro de la acción funciona normal.
    /// </summary>
    public static void Invoke(Action action) => LazyDispatcher.Value.Invoke(action);
}
