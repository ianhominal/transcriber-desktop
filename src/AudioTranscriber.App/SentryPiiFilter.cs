using AudioTranscriber.Core.Observability;
using Sentry;
using Sentry.Protocol;

namespace AudioTranscriber.App;

/// <summary>
/// Traduce entre los tipos reales del SDK de Sentry (<see cref="SentryEvent"/>/<see cref="Breadcrumb"/>)
/// y la lógica pura de scrubbing de <see cref="SentryEventScrubber"/> (Core, sin dependencia de
/// Sentry). Enganchado en <see cref="SentryBootstrap.Init"/> vía <c>SetBeforeSend</c>/
/// <c>SetBeforeBreadcrumb</c>: corre SIEMPRE antes de que cualquier evento salga del proceso, sea
/// cual sea el punto que disparó la captura (DispatcherUnhandledException, las integraciones
/// automáticas de AppDomain/TaskScheduler, o un <c>SentrySdk.CaptureException</c>/
/// <c>AddBreadcrumb</c> manual desde <see cref="SyncCoordinator"/>) — el scrubbing queda
/// centralizado acá en vez de repetido en cada punto de captura.
/// </summary>
public static class SentryPiiFilter
{
    /// <summary>Callback para <c>SentryOptions.SetBeforeSend</c>: nunca descarta el evento, solo lo limpia.</summary>
    public static SentryEvent Apply(SentryEvent sentryEvent)
    {
        if (sentryEvent.Message is { } message)
            sentryEvent.Message = SentryEventScrubber.ScrubText(message.Formatted ?? message.Message);

        foreach (var exception in sentryEvent.SentryExceptions ?? [])
            exception.Value = SentryEventScrubber.ScrubText(exception.Value);

        foreach (var key in sentryEvent.Extra.Keys.ToList())
            sentryEvent.SetExtra(key, SentryEventScrubber.ScrubValueForKey(key, sentryEvent.Extra[key]?.ToString()));

        foreach (var key in sentryEvent.Tags.Keys.ToList())
            sentryEvent.SetTag(key, SentryEventScrubber.ScrubValueForKey(key, sentryEvent.Tags[key]) ?? string.Empty);

        return sentryEvent;
    }

    /// <summary>Callback para <c>SentryOptions.SetBeforeBreadcrumb</c>: nunca descarta el breadcrumb, solo lo limpia.</summary>
    public static Breadcrumb Apply(Breadcrumb breadcrumb)
    {
        Dictionary<string, string>? scrubbedData = null;
        if (breadcrumb.Data is { } data)
        {
            scrubbedData = new Dictionary<string, string>(data.Count);
            foreach (var (key, value) in data)
                scrubbedData[key] = SentryEventScrubber.ScrubValueForKey(key, value) ?? string.Empty;
        }

        return new Breadcrumb(
            message: SentryEventScrubber.ScrubText(breadcrumb.Message) ?? string.Empty,
            type: breadcrumb.Type ?? string.Empty,
            data: scrubbedData,
            category: breadcrumb.Category,
            level: breadcrumb.Level);
    }
}
