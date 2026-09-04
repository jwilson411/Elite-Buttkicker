using System.Text;
using System.Text.Json;
using EDButtkicker.Configuration;
using EDButtkicker.Hosting;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// A device list the test owns. Real enumeration talks to WASAPI, which does not exist on a CI
/// agent, so setup and health behaviour is pinned against a catalog instead of hardware.
/// </summary>
internal sealed class FakeAudioDeviceCatalog : IAudioDeviceCatalog
{
    private readonly List<AudioDevice> _devices;

    public FakeAudioDeviceCatalog(IEnumerable<AudioDevice> devices)
    {
        _devices = devices.ToList();
    }

    /// <summary>
    /// A stable stand-in for an MMDevice endpoint id, shaped like the real thing so that anything
    /// which round trips one through JSON or settings is exercised on the real character set.
    /// </summary>
    public static string EndpointIdFor(int index) => $"{{0.0.0.00000000}}.{{{index:D8}}}";

    /// <summary>
    /// The system default entry plus the named outputs, numbered as WASAPI would and each carrying
    /// its own endpoint id. The default entry gets an empty endpoint id, because it is a choice
    /// rather than an endpoint.
    /// </summary>
    public static FakeAudioDeviceCatalog With(params string[] names)
    {
        var devices = new List<AudioDevice>
        {
            new()
            {
                DeviceId = WasapiAudioDeviceCatalog.SystemDefaultDeviceId,
                Name = WasapiAudioDeviceCatalog.SystemDefaultDeviceName,
                Driver = "Default",
                Channels = 2,
                IsDefault = true,
                IsAvailable = true
            }
        };

        devices.AddRange(names.Select((name, index) => new AudioDevice
        {
            EndpointId = EndpointIdFor(index),
            DeviceId = index,
            Name = name,
            Driver = "WASAPI",
            Channels = 2,
            IsDefault = index == 0,
            IsAvailable = true
        }));

        return new FakeAudioDeviceCatalog(devices);
    }

    public IReadOnlyList<AudioDevice> GetDevices() => _devices;
}

/// <summary>
/// An audio engine that reports device state without opening one. <see cref="FailuresBeforeSuccess"/>
/// models the machine where the device only opens on a second attempt, which is what the health
/// retry exists for.
/// </summary>
internal sealed class FakeAudioEngine : AudioEngineService
{
    private readonly AppSettings _settings;
    private bool _opened;
    private bool _attempted;

    public FakeAudioEngine(AppSettings settings, bool canOpen = true, int failuresBeforeSuccess = 0)
        : base(NullLogger<AudioEngineService>.Instance, settings)
    {
        _settings = settings;
        CanOpen = canOpen;
        FailuresBeforeSuccess = failuresBeforeSuccess;
    }

    public bool CanOpen { get; set; }

    public int FailuresBeforeSuccess { get; set; }

    public string FailureReason { get; set; } = "no output device is available";

    public List<HapticPattern> Played { get; } = new();

    public int OpenAttempts { get; private set; }

    public override bool EnsureInitialized()
    {
        if (_opened) return true;

        _attempted = true;
        OpenAttempts++;

        if (!CanOpen || OpenAttempts <= FailuresBeforeSuccess)
        {
            return false;
        }

        _opened = true;
        return true;
    }

    public override AudioEngineStatus GetStatus() => new(
        _opened,
        _attempted && !_opened,
        _opened ? null : FailureReason,
        _settings.Audio.AudioDeviceName,
        _opened ? DateTime.UtcNow : null);

    public override Task PlayHapticPattern(HapticPattern pattern, JournalEvent? journalEvent = null)
    {
        if (!EnsureInitialized())
        {
            return Task.CompletedTask;
        }

        lock (Played)
        {
            Played.Add(pattern);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Records what the journal pipeline was handed, without touching audio or history.</summary>
internal sealed class RecordingJournalPipeline : IJournalEventPipeline
{
    private readonly List<JournalEvent> _processed = new();

    public IReadOnlyList<JournalEvent> Processed
    {
        get
        {
            lock (_processed)
            {
                return _processed.ToList();
            }
        }
    }

    public Task ProcessAsync(JournalEvent journalEvent, bool skipHistory = false)
    {
        lock (_processed)
        {
            _processed.Add(journalEvent);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// The real web pipeline on a TestServer - same registrations and same Configure callback as
/// Program - with the per-user state redirected into a temp directory, a device catalog the test
/// owns, and an audio engine that never opens hardware. No hosted services run.
/// </summary>
internal sealed class SetupTestHost : IDisposable
{
    private readonly IWebHost _host;

    public SetupTestHost(
        string settingsDirectory,
        AppSettings? settings = null,
        FakeAudioDeviceCatalog? deviceCatalog = null,
        FakeAudioEngine? audioEngine = null,
        IEnumerable<string>? journalSearchPaths = null)
    {
        Settings = settings ?? new AppSettings();
        DeviceCatalog = deviceCatalog ?? FakeAudioDeviceCatalog.With("ButtKicker Amp", "Headphones");
        AudioEngine = audioEngine ?? new FakeAudioEngine(Settings);

        _host = new WebHostBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
                services.AddEliteButtkicker(Settings);

                // Everything below only redirects state that would otherwise land in the real user
                // profile, or stands in for hardware. The graph itself is the production one.
                services.Replace(ServiceDescriptor.Singleton(
                    new UserSettingsService(NullLogger<UserSettingsService>.Instance, settingsDirectory)));
                services.Replace(ServiceDescriptor.Singleton(
                    new SetupStateService(NullLogger<SetupStateService>.Instance, settingsDirectory)));
                services.Replace(ServiceDescriptor.Singleton(
                    new JournalPathDiscovery(Settings, journalSearchPaths ?? Array.Empty<string>())));
                services.Replace(ServiceDescriptor.Singleton<IAudioDeviceCatalog>(DeviceCatalog));
                services.Replace(ServiceDescriptor.Singleton<AudioEngineService>(AudioEngine));
            })
            .Configure(WebUiConfiguration.Configure)
            .Build();

        _host.Start();
        Client = LoopbackTestClient.Create(_host);
    }

    public AppSettings Settings { get; }

    public FakeAudioDeviceCatalog DeviceCatalog { get; }

    public FakeAudioEngine AudioEngine { get; }

    public HttpClient Client { get; }

    public IServiceProvider Services => _host.Services;

    public async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await Client.GetAsync(path);
        Assert.True(response.IsSuccessStatusCode, $"GET {path} returned {(int)response.StatusCode}");

        return await ReadJsonAsync(response);
    }

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null) =>
        Client.PostAsync(
            path,
            new StringContent(body == null ? "{}" : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    public void Dispose()
    {
        Client.Dispose();
        _host.Dispose();
    }
}

internal static class SetupTestExtensions
{
    /// <summary>The step with the given id, so assertions read like the wizard does.</summary>
    public static JsonElement Step(this JsonElement setupStatus, string id) =>
        setupStatus.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("id").GetString() == id);

    public static bool IsStepComplete(this JsonElement setupStatus, string id) =>
        setupStatus.Step(id).GetProperty("complete").GetBoolean();

    /// <summary>One health indicator out of a report, in either the health or setup payload.</summary>
    public static JsonElement Component(this JsonElement healthReport, string id) =>
        healthReport.GetProperty("components").EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);

    public static string StatusOf(this JsonElement healthReport, string id) =>
        healthReport.Component(id).GetProperty("status").GetString()!;

    public static string ReasonOf(this JsonElement healthReport, string id) =>
        healthReport.Component(id).GetProperty("reason").GetString()!;

    /// <summary>Polls until <paramref name="condition"/> holds, so nothing waits on a fixed sleep.</summary>
    public static async Task WaitForAsync(Func<bool> condition, string description, int timeoutMs = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for {description}");
    }
}
