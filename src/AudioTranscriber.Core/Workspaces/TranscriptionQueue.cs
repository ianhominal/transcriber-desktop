namespace AudioTranscriber.Core.Workspaces;

/// <summary>
/// Cola FIFO de "audios pendientes de transcribir" (refactor de concurrencia 2026-07-27: grabar
/// mientras se transcribe otro audio, y encolar en vez de bloquear el botón "Transcribir" cuando
/// ya hay uno corriendo). Generaliza el batch por proyecto que antes vivía solo en
/// <c>MainViewModel.TranscribeProjectAsync</c> (loop con await secuencial reusando SelectedAudio) --
/// ahora ambos caminos (un click sobre un audio suelto, o "transcribir todo el proyecto") empujan acá
/// y un único worker en el ViewModel procesa de a uno.
///
/// Genérica en <typeparamref name="TKey"/> (identidad para dedupe/consulta, en la app la ruta
/// completa del audio) y <typeparamref name="TItem"/> (lo que hace falta para procesarlo, en la app
/// el propio <c>AudioItemVm</c>) -- deliberadamente desacoplada de la App (WPF) para poder testear
/// sin UI, mismo criterio que <see cref="BatchTranscribePlanner"/>.
///
/// Pensada para uso single-threaded (todo pasa por el hilo de UI en WPF): sin locks. Si algún día
/// se necesita desde otro hilo, hace falta sincronizar por fuera.
/// </summary>
public sealed class TranscriptionQueue<TKey, TItem> where TKey : notnull
{
    // Dos estructuras: _order mantiene el FIFO, _items resuelve key -> item y duplica como el
    // índice para IsQueued/dedupe en O(1) en vez de recorrer la lista.
    private readonly List<TKey> _order = new();
    private readonly Dictionary<TKey, TItem> _items = new();

    /// <summary>Cuántos audios esperan su turno.</summary>
    public int Count => _order.Count;

    /// <summary>True si no hay nada esperando turno.</summary>
    public bool IsEmpty => _order.Count == 0;

    /// <summary>True si <paramref name="key"/> ya está encolado ahora mismo.</summary>
    public bool IsQueued(TKey key) => _items.ContainsKey(key);

    /// <summary>
    /// Encola <paramref name="item"/> bajo <paramref name="key"/> al final de la cola. Dedupe: si
    /// <paramref name="key"/> ya está encolado, no hace nada y devuelve false -- tocar "Transcribir"
    /// dos veces sobre el mismo audio mientras espera turno no lo duplica en la cola.
    /// </summary>
    public bool Enqueue(TKey key, TItem item)
    {
        if (!_items.TryAdd(key, item))
            return false;

        _order.Add(key);
        return true;
    }

    /// <summary>
    /// Saca y devuelve el próximo audio (el que más tiempo lleva esperando), o
    /// <c>default(TItem)</c> (null para tipos referencia) si la cola está vacía.
    /// </summary>
    public TItem? Dequeue()
    {
        if (_order.Count == 0)
            return default;

        var key = _order[0];
        _order.RemoveAt(0);

        var item = _items[key];
        _items.Remove(key);
        return item;
    }

    /// <summary>
    /// Vacía la cola entera (ver "Cancelar" en MainViewModel: cancela la transcripción EN CURSO y
    /// además vacía lo pendiente -- la usuaria pidió frenar todo, no solo lo de ahora mismo).
    /// </summary>
    public void Clear()
    {
        _order.Clear();
        _items.Clear();
    }
}
