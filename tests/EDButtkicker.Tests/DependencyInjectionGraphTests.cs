using System.Net;
using System.Reflection;
using System.Text;
using EDButtkicker.Configuration;
using EDButtkicker.Controllers;
using EDButtkicker.Hosting;
using EDButtkicker.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Guards the single composition root: the runtime services and the web controllers must resolve
/// from one graph, and every route the web UI maps must actually be servable from it.
/// Nothing here touches audio hardware, binds a port, or starts a hosted service.
/// </summary>
public class DependencyInjectionGraphTests : IClassFixture<WebUiTestServerFixture>
{
    private readonly WebUiTestServerFixture _fixture;

    public DependencyInjectionGraphTests(WebUiTestServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void AddEliteButtkicker_BuildsAValidatedServiceGraph()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEliteButtkicker(new AppSettings());

        // ValidateOnBuild turns a missing controller dependency into a build failure instead of a
        // 500 on the first request that needs it; ValidateScopes catches captive dependencies.
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        Assert.NotNull(provider);
    }

    [Fact]
    public void ContextualIntelligenceApiController_Resolves()
    {
        var controller = _fixture.Services.GetRequiredService<ContextualIntelligenceApiController>();

        Assert.NotNull(controller);
    }

    [Fact]
    public void PatternFileService_IsTheSameInstanceForControllersAndRuntime()
    {
        var runtimeSingleton = _fixture.Services.GetRequiredService<PatternFileService>();

        var patternFilesController = _fixture.Services.GetRequiredService<PatternFilesController>();
        var patternEditorController = _fixture.Services.GetRequiredService<PatternEditorController>();

        Assert.True(ReferenceEquals(
            runtimeSingleton,
            GetPrivateDependency<PatternFileService>(patternFilesController)));
        Assert.True(ReferenceEquals(
            runtimeSingleton,
            GetPrivateDependency<PatternFileService>(patternEditorController)));

        // The catalog interface the journal pipeline consumes has to be that same object too,
        // otherwise pattern reloads through the API would never reach playback.
        Assert.True(ReferenceEquals(runtimeSingleton, _fixture.Services.GetRequiredService<IPatternCatalog>()));
    }

    [Fact]
    public void ContextualIntelligenceService_UsedByController_IsTheRuntimeSingleton()
    {
        var runtimeSingleton = _fixture.Services.GetRequiredService<ContextualIntelligenceService>();
        var controller = _fixture.Services.GetRequiredService<ContextualIntelligenceApiController>();

        Assert.True(ReferenceEquals(
            runtimeSingleton,
            GetPrivateDependency<ContextualIntelligenceService>(controller)));
    }

    [Fact]
    public async Task GetContextStatus_IsSuccessful()
    {
        // Smoking gun for the old split container: ContextualIntelligenceService was missing from
        // the web container, so this route used to fail outright.
        var response = await _fixture.Client.GetAsync("/api/context/status");

        Assert.True(
            response.IsSuccessStatusCode,
            $"GET /api/context/status returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Theory]
    [MemberData(nameof(MappedRoutes))]
    public async Task EveryMappedRoute_IsHandledWithoutServerError(string method, string path)
    {
        var response = await SendAsync(method, path);
        var body = await response.Content.ReadAsStringAsync();
        var status = (int)response.StatusCode;

        // 4xx is fine - these requests carry deliberately minimal bodies. 5xx means the route could
        // not be served at all, which is what a broken service graph looks like from the outside.
        // The one documented exception is the import endpoint, which answers 501 by design.
        var allowNotImplemented = path == "/api/PatternFiles/import";

        Assert.True(
            status < 500 || (allowNotImplemented && status == (int)HttpStatusCode.NotImplemented),
            $"{method} {path} returned {status}: {body}");
    }

    [Theory]
    [MemberData(nameof(ReadOnlyRoutes))]
    public async Task ReadOnlyGetRoutes_AreSuccessful(string path)
    {
        var response = await _fixture.Client.GetAsync(path);

        Assert.True(
            response.IsSuccessStatusCode,
            $"GET {path} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>Every route wired up in <see cref="WebUiConfiguration.Configure"/>.</summary>
    public static TheoryData<string, string> MappedRoutes() => new()
    {
        { "GET", "/api/csrf" },
        { "GET", "/api/config" },
        { "POST", "/api/config" },
        { "GET", "/api/config/export" },
        { "POST", "/api/config/import" },
        { "GET", "/api/patterns" },
        { "POST", "/api/patterns" },
        { "POST", "/api/patterns/FSDJump/test" },
        { "PUT", "/api/patterns/FSDJump" },
        { "DELETE", "/api/patterns/FSDJump" },
        { "POST", "/api/patterns/test/custom" },
        { "GET", "/api/audio/devices" },
        { "POST", "/api/audio/device" },
        { "POST", "/api/audio/test" },
        { "GET", "/api/journal/status" },
        { "POST", "/api/journal/path" },
        { "GET", "/api/journal/events/recent" },
        { "POST", "/api/journal/replay/start" },
        { "POST", "/api/journal/replay/stop" },
        { "GET", "/api/journal/replay/status" },
        { "POST", "/api/PatternFiles/reload" },
        { "POST", "/api/PatternFiles/export" },
        { "POST", "/api/PatternFiles/import" },
        { "GET", "/api/PatternFiles/packs" },
        { "GET", "/api/PatternEditor/templates" },
        { "POST", "/api/PatternEditor/create" },
        { "POST", "/api/PatternEditor/save" },
        { "POST", "/api/PatternEditor/validate" },
        { "POST", "/api/PatternEditor/test" },
        { "GET", "/api/PatternEditor/load/dummy.json" },
        { "GET", "/api/PatternEditor/user-files/test-author" },
        { "GET", "/api/context/status" },
        { "POST", "/api/context/config" },
        { "GET", "/api/context/predictions" },
        { "GET", "/api/setup/status" },
        { "GET", "/api/setup/journal/candidates" },
        { "POST", "/api/setup/journal" },
        { "POST", "/api/setup/audio/device" },
        { "POST", "/api/setup/audio/test" },
        { "POST", "/api/setup/complete" },
        { "POST", "/api/setup/reopen" },
        { "GET", "/api/health" },
        { "POST", "/api/health/journal/retry" },
        { "POST", "/api/health/audio/retry" },
        { "GET", "/" }
    };

    /// <summary>GETs that need no request body, so anything but success is a real failure.</summary>
    public static TheoryData<string> ReadOnlyRoutes() => new()
    {
        "/api/csrf",
        "/api/config",
        "/api/patterns",
        "/api/journal/status",
        "/api/audio/devices",
        "/api/PatternFiles/packs",
        "/api/PatternEditor/templates",
        "/api/context/predictions",
        "/api/setup/status",
        "/api/setup/journal/candidates",
        "/api/health",
        "/"
    };

    private Task<HttpResponseMessage> SendAsync(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);

        // Send a minimal JSON body on writes so we are testing DI and routing rather than
        // whatever a handler does with an empty stream.
        if (method is "POST" or "PUT")
        {
            request.Content = new StringContent(MinimalBodyFor(path), Encoding.UTF8, "application/json");
        }

        return _fixture.Client.SendAsync(request);
    }

    private static string MinimalBodyFor(string path) => path switch
    {
        "/api/patterns" => """{"eventType":"FSDJump","pattern":{"name":"Test","pattern":"SharpPulse","frequency":40,"intensity":50,"duration":500}}""",
        "/api/patterns/FSDJump" => """{"eventType":"FSDJump","pattern":{"name":"Test","pattern":"SharpPulse","frequency":40,"intensity":50,"duration":500}}""",
        _ => "{}"
    };

    private static T GetPrivateDependency<T>(object instance) where T : class
    {
        var field = instance.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(f => f.FieldType == typeof(T));

        Assert.True(field != null, $"{instance.GetType().Name} has no {typeof(T).Name} field to compare");

        return (T)field!.GetValue(instance)!;
    }
}

/// <summary>
/// Hosts the real web pipeline on a TestServer: same registrations and same Configure callback as
/// Program, minus Kestrel, minus the hosted services (journal watching, status polling, browser
/// launch) and minus any audio device initialisation.
/// </summary>
public sealed class WebUiTestServerFixture : IDisposable
{
    private readonly IWebHost _host;
    private readonly TempDirectory _setupStateDir = new("edbk-di-setup");

    public WebUiTestServerFixture()
    {
        var settings = new AppSettings();

        _host = new WebHostBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

                // The one composition root, exactly as Program registers it.
                services.AddEliteButtkicker(settings);

                // The only redirection: setup completion is written to a temp directory so probing
                // the setup routes cannot mark a developer's own first run as done.
                services.Replace(ServiceDescriptor.Singleton(
                    new SetupStateService(NullLogger<SetupStateService>.Instance, _setupStateDir.Path)));

                // Deliberately no AddHostedService: no JournalMonitorService, no StatusMonitorService,
                // no WebConfigurationService, and AudioEngineService is never Initialize()d.
                Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
            })
            .Configure(WebUiConfiguration.Configure)
            .Build();

        _host.Start();
        Client = LoopbackTestClient.Create(_host);
        RawClient = _host.GetTestClient();
    }

    /// <summary>Speaks like the application's own page: loopback Host, same-origin Origin, token.</summary>
    public HttpClient Client { get; }

    /// <summary>No default headers at all, so a test can spell out exactly what a caller sent.</summary>
    public HttpClient RawClient { get; }

    public IServiceProvider Services => _host.Services;

    public void Dispose()
    {
        Client.Dispose();
        RawClient.Dispose();
        _host.Dispose();
        _setupStateDir.Dispose();
    }
}
