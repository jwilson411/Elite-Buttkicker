using Microsoft.Extensions.DependencyInjection;
using EDButtkicker.Configuration;
using EDButtkicker.Controllers;
using EDButtkicker.Services;

namespace EDButtkicker.Hosting;

/// <summary>
/// The single composition root. Program and the integration tests both register through here,
/// so the runtime services and the web API resolve from one service graph - there is no second
/// container and no singleton that exists twice.
/// Hosted services are deliberately NOT registered here: they are a Program concern, so tests can
/// build the same graph without starting journal watching, status polling or audio hardware.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers every runtime service and every controller dependency in one graph.
    /// </summary>
    public static IServiceCollection AddEliteButtkicker(this IServiceCollection services, AppSettings appSettings)
    {
        services.AddSingleton(appSettings);

        // The clock is injected so time-dependent behaviour (event rate limits) is deterministic
        // in tests; the process always runs on the system clock.
        services.AddSingleton(TimeProvider.System);

        // Add core services
        services.AddSingleton<AudioEngineService>();
        services.AddSingleton<PatternSequencer>();
        services.AddSingleton<UserSettingsService>();
        services.AddSingleton<ContextualIntelligenceService>();
        services.AddSingleton<EventMappingService>();
        services.AddSingleton<ShipTrackingService>();
        services.AddSingleton<PatternFileService>();
        services.AddSingleton<PatternSelectionService>();
        services.AddSingleton<ShipPatternService>();

        // First-run setup and the health checklist. The monitor status object is shared state
        // between the journal watcher and the health API, so it has to be one singleton; the device
        // catalog is behind an interface so health checks work where WASAPI does not exist.
        services.AddSingleton<JournalMonitorStatus>();
        services.AddSingleton<IAudioDeviceCatalog, WasapiAudioDeviceCatalog>();
        services.AddSingleton<JournalPathDiscovery>();
        services.AddSingleton<SetupStateService>();
        services.AddSingleton<SystemHealthService>();

        // Journal event pipeline: one ordered path for history, ship state,
        // pattern selection and audio, shared by live monitoring and replay.
        services.AddSingleton<IJournalEventStore, JournalEventStore>();
        services.AddSingleton<IJournalEventAudioSink>(sp => sp.GetRequiredService<EventMappingService>());
        services.AddSingleton<IShipPatternProvider>(sp => sp.GetRequiredService<ShipPatternService>());
        services.AddSingleton<IPatternCatalog>(sp => sp.GetRequiredService<PatternFileService>());
        services.AddSingleton<PatternSourceCatalogReconciler>();
        services.AddSingleton<IJournalEventPipeline, JournalEventPipeline>();
        // IntensityCurveProcessor is a static class, no need to register
        // AdvancedWaveformGenerator and MultiLayerPatternGenerator are created as needed

        return services.AddEliteButtkickerControllers();
    }

    /// <summary>
    /// Every controller the process can resolve, registered in the same graph as its dependencies.
    /// </summary>
    public static IServiceCollection AddEliteButtkickerControllers(this IServiceCollection services)
    {
        // Routed by the web UI middleware in WebUiConfiguration.
        services.AddSingleton<ConfigurationApiController>();
        services.AddSingleton<PatternApiController>();
        services.AddSingleton<AudioApiController>();
        services.AddSingleton<JournalApiController>();
        services.AddSingleton<PatternFilesController>();
        services.AddSingleton<PatternEditorController>();
        services.AddSingleton<ContextualIntelligenceApiController>();
        services.AddSingleton<SetupApiController>();
        services.AddSingleton<HealthApiController>();

        // Not routed today, but they belong to the same graph so they stay resolvable.
        services.AddSingleton<UserSettingsController>();
        services.AddSingleton<PatternSelectionController>();
        services.AddSingleton<ShipPatternsController>();

        return services;
    }
}
