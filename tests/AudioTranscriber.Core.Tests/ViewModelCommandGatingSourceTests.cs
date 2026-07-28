using System.Text.RegularExpressions;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Analiza el CÓDIGO FUENTE de los ViewModels buscando un bug que el compilador no ve y que ya se
/// coló TRES veces: un comando con <c>CanExecute = nameof(CanFoo)</c> cuya condición depende de una
/// propiedad <c>[ObservableProperty]</c> que NO declara
/// <c>[NotifyCanExecuteChangedFor(nameof(FooCommand))]</c>.
/// <para/>
/// Cuando falta ese atributo, CommunityToolkit.Mvvm evalúa el <c>CanExecute</c> una sola vez y nadie
/// se lo vuelve a preguntar: el botón queda deshabilitado para siempre. No hay error, no hay
/// warning, y en la app se ve como "el botón está en gris y no sé por qué".
/// <para/>
/// Historial de este mismo olvido: "Transcribir" (2026-07-17), "Compartir" (2026-07-28) e "Invitar"
/// (2026-07-28, media hora después del anterior). Las dos primeras veces se dejó un comentario de
/// advertencia en el código; no alcanzó, porque un comentario no rompe el build.
/// <para/>
/// Vive en Core.Tests y no en UiTests a propósito: es análisis de texto sobre archivos, no necesita
/// WPF ni un hilo STA, y corre en la suite rápida que se ejecuta todo el tiempo.
/// </summary>
public class ViewModelCommandGatingSourceTests
{
    private static string ViewModelsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "AudioTranscriber.App", "ViewModels");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"No se encontró src/AudioTranscriber.App/ViewModels subiendo desde {AppContext.BaseDirectory}.");
    }

    /// <summary>`private bool CanFoo(...)` con su cuerpo, sea expression-bodied o con llaves.</summary>
    private static readonly Regex CanMethod = new(
        @"(?:private|public|protected)\s+bool\s+(Can\w+)\s*\([^)]*\)\s*(?:=>(?<expr>[^;]+);|\{(?<block>(?:[^{}]|\{[^{}]*\})*)\})",
        RegexOptions.Compiled);

    /// <summary>Campo con [ObservableProperty] y los atributos que lo acompañan.</summary>
    private static readonly Regex ObservableField = new(
        @"\[ObservableProperty\](?<attrs>(?:\s*\[[^\]]*\])*)\s*(?:private|internal)\s+[\w\?<>,\[\]\.]+\s+_(?<name>\w+)\s*(?:=|;)",
        RegexOptions.Compiled);

    public static TheoryData<string> ViewModelFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(ViewModelsDirectory(), "*.cs", SearchOption.AllDirectories))
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(ViewModelFiles))]
    public void Cada_propiedad_que_condiciona_un_comando_lo_notifica(string fileName)
    {
        var source = File.ReadAllText(Path.Combine(ViewModelsDirectory(), fileName));

        // Propiedad observable -> comandos que ya declara notificar.
        var notifiedBy = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (Match field in ObservableField.Matches(source))
        {
            var raw = field.Groups["name"].Value;
            var property = char.ToUpperInvariant(raw[0]) + raw[1..];
            var commands = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match notify in Regex.Matches(field.Groups["attrs"].Value,
                         @"NotifyCanExecuteChangedFor\(nameof\((\w+)\)\)"))
                commands.Add(notify.Groups[1].Value);
            notifiedBy[property] = commands;
        }

        if (notifiedBy.Count == 0) return; // sin propiedades observables no hay nada que verificar

        var problems = new List<string>();

        foreach (Match can in CanMethod.Matches(source))
        {
            var canName = can.Groups[1].Value;

            // El comando generado se llama igual que el método que decora [RelayCommand], no que el
            // CanExecute. Se busca qué método lo usa y se deriva de ahí (Async se pela, igual que
            // hace el generador del toolkit).
            var owner = Regex.Match(source,
                @"\[RelayCommand\([^)]*CanExecute\s*=\s*nameof\(" + Regex.Escape(canName) + @"\)[^)]*\)\]\s*(?:private|public)\s+(?:async\s+)?[\w\?<>\.]+\s+(\w+)\s*\(");
            if (!owner.Success) continue;

            var command = Regex.Replace(owner.Groups[1].Value, "Async$", "") + "Command";
            var body = can.Groups["expr"].Success ? can.Groups["expr"].Value : can.Groups["block"].Value;

            foreach (var (property, commands) in notifiedBy)
            {
                // \b para no confundir `IsBusy` con `IsBusyDownloading`.
                if (!Regex.IsMatch(body, $@"\b{Regex.Escape(property)}\b")) continue;
                if (commands.Contains(command)) continue;

                problems.Add(
                    $"{canName}() depende de '{property}', pero '{property}' no declara " +
                    $"[NotifyCanExecuteChangedFor(nameof({command}))]. El botón va a quedar " +
                    $"deshabilitado y no se va a enterar cuando '{property}' cambie.");
            }
        }

        Assert.True(problems.Count == 0, $"{fileName}:\n  - " + string.Join("\n  - ", problems));
    }
}
