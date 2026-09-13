using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // One anti-forgery token per process, guarding every state-changing web request.
        services.AddSingleton<CsrfTokenProvider>();

        // Add core services
        services.AddSingleton<AudioEngineService>();
        services.AddSingleton<PatternSequencer>();
        services.AddSingleton<UserSettingsService>();

        // Every settings mutation is validated, applied and written through this one service, so a
        // successful settings API call always means the same thing - and always survives a restart.
        services.AddSingleton<SettingsPersistenceService>();
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

        // Everything that talks to hardware or to the filesystem does it through one of these, so
        // the graph a test builds can swap in an in-memory directory or an output that never opens
        // a device without any service knowing the difference.
        services.AddSingleton<IAudioOutputFactory, NAudioOutputFactory>();
        services.AddSingleton<IDirectoryWatcherFactory, FileSystemDirectoryWatcherFactory>();
        services.AddSingleton<IJournalStorage, FileSystemJournalStorage>();
        services.AddSingleton<IPatternStorage, FileSystemPatternStorage>();

        services.AddSingleton<JournalPathDiscovery>();
        services.AddSingleton<SetupStateService>();
        services.AddSingleton<SystemHealthService>();

        // The support bundle and the errors it reports. The error ring is filled from the logging
        // pipeline (Program wires the provider, after it clears the default ones), so a bundle shows
        // the same failures the console did instead of a second list of things that can go wrong.
        services.AddSingleton<RecentErrorLog>();
        services.AddSingleton<DiagnosticsRedactor>();
        services.AddSingleton<DiagnosticsBundleService>();

        // Journal event pipeline: one ordered path for history, ship state,
        // pattern selection and audio, shared by live monitoring and replay.
        services.AddSingleton<IJournalEventStore, JournalEventStore>();
        services.AddSingleton<IJournalEventAudioSink>(sp => sp.GetRequiredService<EventMappingService>());
        services.AddSingleton<IShipPatternProvider>(sp => sp.GetRequiredService<ShipPatternService>());
        services.AddSingleton<IPatternCatalog>(sp => sp.GetRequiredService<PatternFileService>());
        services.AddSingleton<PatternSourceCatalogReconciler>();
        services.AddSingleton<IJournalEventPipeline, JournalEventPipeline>();

        // Replay outlives the request that starts it, so its cancellation source, task and status
        // live in one singleton the container disposes on shutdown - not in the controller.
        services.AddSingleton<JournalReplayService>();
        // IntensityCurveProcessor is a static class, no need to register
        // AdvancedWaveformGenerator and MultiLayerPatternGenerator are created as needed

        return services.AddEliteButtkickerControllers();
    }

    /// <summary>
    /// MVC plus every controller the process can resolve, registered in the same graph as its
    /// dependencies. Routing, model binding and the endpoint metadata the inventory is built from
    /// all come from here; <see cref="WebUiConfiguration"/> only maps what this registers.
    /// </summary>
    public static IServiceCollection AddEliteButtkickerControllers(this IServiceCollection services)
    {
        // Controllers are discovered from this assembly and no other. Naming the part explicitly,
        // rather than letting MVC scan whatever assembly happens to be the entry point, is what
        // makes the test server route exactly the endpoints the process routes.
        var parts = new ApplicationPartManager();
        parts.ApplicationParts.Add(new AssemblyPart(typeof(ServiceCollectionExtensions).Assembly));
        services.TryAddSingleton(parts);

        services
            .AddControllers(options => options.Conventions.Add(new UnroutedControllerConvention()))
            .AddJsonOptions(options =>
            {
                // One JSON contract for every endpoint: camelCase as the pages read it, enums by
                // name as the conflicts page sends and shows them, and the shared depth cap so
                // deeply nested request JSON is a bad request rather than a stack overflow.
                options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
                options.JsonSerializerOptions.MaxDepth = RequestLimits.MaxJsonDepth;
                options.JsonSerializerOptions.Converters.Add(
                    new JsonStringEnumConverter(allowIntegerValues: true));
            })
            // Controllers are activated out of this container, so a controller and the runtime hold
            // the very same singletons - there is no second graph behind MVC.
            .AddControllersAsServices();

        services.AddEndpointsApiExplorer();

        // Mapped by MapControllers in WebUiConfiguration. Transient because MVC hands each request's
        // ControllerContext to the instance it activates; a shared instance would race on it.
        services.AddTransient<ConfigurationApiController>();
        services.AddTransient<PatternApiController>();
        services.AddTransient<AudioApiController>();
        services.AddTransient<JournalApiController>();
        services.AddTransient<PatternFilesController>();
        services.AddTransient<PatternEditorController>();
        services.AddTransient<ContextualIntelligenceApiController>();
        services.AddTransient<SetupApiController>();
        services.AddTransient<HealthApiController>();
        services.AddTransient<PatternSelectionController>();

        // Not routed today - UnroutedControllerConvention takes them back out of the application
        // model - but they belong to the same graph so they stay resolvable.
        services.AddTransient<UserSettingsController>();
        services.AddTransient<ShipPatternsController>();

        return services;
    }
}

/// <summary>
/// Keeps controllers that the web UI does not call out of the routing table. They are registered
/// and resolvable, but nothing may reach them over HTTP until the UI actually needs them.
/// </summary>
internal sealed class UnroutedControllerConvention : IApplicationModelConvention
{
    private static readonly HashSet<Type> Unrouted = new()
    {
        typeof(UserSettingsController),
        typeof(ShipPatternsController)
    };

    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers
                     .Where(c => Unrouted.Contains(c.ControllerType.AsType()))
                     .ToList())
        {
            application.Controllers.Remove(controller);
        }
    }
}
