using System.Text.Json;
using System.Text.Json.Serialization;
using EDButtkicker.Hosting;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The schema gate every pattern pack passes before it is indexed or played: the bounds and timing
/// relationships <see cref="PatternSchemaValidator"/> enforces, the diagnostics a rejected pack gets
/// back, and the promise that a bad file never costs the catalog a pack that was already good.
/// Nothing here opens an audio device - validation is pure file handling.
/// </summary>
public class PatternSchemaValidationTests
{
    // ---- The validator itself: every rejection names the field and the value -------------------

    [Fact]
    public void AWellFormedPack_HasNoDiagnostics()
    {
        Assert.Empty(PatternSchemaValidator.Validate(Pack()));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(0)]
    [InlineData(101)]
    public void FrequencyOutsideTheSupportedRange_IsRejectedByField(int frequency)
    {
        var pack = Pack();
        Pattern(pack).Frequency = frequency;

        var errors = PatternSchemaValidator.Validate(pack);

        Assert.Contains(errors, e => e.Contains("event 'HullDamage'") &&
                                     e.Contains($"frequency is {frequency}Hz") &&
                                     e.Contains("10-100Hz"));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    public void FrequencyOnTheBoundary_IsAccepted(int frequency)
    {
        var pack = Pack();
        Pattern(pack).Frequency = frequency;

        Assert.Empty(PatternSchemaValidator.Validate(pack));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void IntensityOutsideOneToOneHundred_IsRejectedByField(int intensity)
    {
        var pack = Pack();
        Pattern(pack).Intensity = intensity;

        var errors = PatternSchemaValidator.Validate(pack);

        Assert.Contains(errors, e => e.Contains($"intensity is {intensity}%") && e.Contains("1-100%"));
    }

    [Fact]
    public void DurationBelowTheFiftyMillisecondFloor_IsRejected()
    {
        var pack = Pack();
        Pattern(pack).Duration = 49;
        Pattern(pack).FadeIn = 0;
        Pattern(pack).FadeOut = 0;

        var errors = PatternSchemaValidator.Validate(pack);

        Assert.Contains(errors, e => e.Contains("duration is 49ms") && e.Contains("50ms minimum"));
    }

    [Fact]
    public void FadesThatDoNotFitInsideTheDuration_AreRejected()
    {
        var pack = Pack();
        Pattern(pack).Duration = 500;
        Pattern(pack).FadeIn = 300;
        Pattern(pack).FadeOut = 300;

        var errors = PatternSchemaValidator.Validate(pack);

        Assert.Contains(errors, e => e.Contains("fadeIn (300ms) + fadeOut (300ms) exceeds duration (500ms)"));
    }

    [Fact]
    public void FadesThatExactlyFillTheDuration_AreAccepted()
    {
        var pack = Pack();
        Pattern(pack).Duration = 500;
        Pattern(pack).FadeIn = 200;
        Pattern(pack).FadeOut = 300;

        Assert.Empty(PatternSchemaValidator.Validate(pack));
    }

    [Fact]
    public void AFadeLongerThanTheFadeCap_IsRejected()
    {
        var pack = Pack();
        Pattern(pack).Duration = 10000;
        Pattern(pack).FadeIn = 5001;

        var errors = PatternSchemaValidator.Validate(pack);

        Assert.Contains(errors, e => e.Contains("fadeIn is 5001ms") && e.Contains("0-5000ms"));
    }

    [Fact]
    public void DamageScalingFloorAboveItsCeiling_IsRejected()
    {
        var pack = Pack();
        Pattern(pack).MinIntensity = 80;
        Pattern(pack).MaxIntensity = 40;

        var errors = PatternSchemaValidator.Validate(pack);

        Assert.Contains(errors, e => e.Contains("minIntensity (80%) is greater than maxIntensity (40%)"));
    }

    [Fact]
    public void DamageScalingIntensityOutsideZeroToOneHundred_IsRejected()
    {
        var pack = Pack();
        Pattern(pack).MaxIntensity = 150;

        var errors = PatternSchemaValidator.Validate(pack);

        Assert.Contains(errors, e => e.Contains("maxIntensity is 150%") && e.Contains("0-100%"));
    }

    [Theory]
    [InlineData(1.5f)]
    [InlineData(-0.1f)]
    public void ALayerAmplitudeOutsideZeroToOne_IsRejectedByLayerIndex(float amplitude)
    {
        var pack = Pack();
        Pattern(pack).Layers.Add(new PatternLayer { Frequency = 40, Amplitude = amplitude });

        var errors = PatternSchemaValidator.Validate(pack);

        Assert.Contains(errors, e => e.Contains("layer 0") && e.Contains("amplitude is") && e.Contains("0-1"));
    }

    [Fact]
    public void ALayerStartingAfterThePatternEnds_IsRejected()
    {
        var pack = Pack();
        Pattern(pack).Duration = 500;
        Pattern(pack).Layers.Add(new PatternLayer { Frequency = 40, Amplitude = 0.5f, StartTime = 500 });

        var errors = PatternSchemaValidator.Validate(pack);

        Assert.Contains(errors, e => e.Contains("layer 0") && e.Contains("startTime is 500ms") &&
                                     e.Contains("pattern duration (500ms)"));
    }

    [Fact]
    public void ALayerWhoseFadesOverrunItsOwnSpan_IsRejected()
    {
        var pack = Pack();
        Pattern(pack).Duration = 1000;
        Pattern(pack).Layers.Add(new PatternLayer
        {
            Frequency = 40,
            Amplitude = 0.5f,
            StartTime = 0,
            Duration = 200,
            FadeIn = 150,
            FadeOut = 150
        });

        var errors = PatternSchemaValidator.Validate(pack);

        Assert.Contains(errors, e => e.Contains("layer 0") && e.Contains("exceeds the layer's 200ms span"));
    }

    [Fact]
    public void ALayerFrequencyOutsideTheSupportedRange_IsRejected()
    {
        var pack = Pack();
        Pattern(pack).Layers.Add(new PatternLayer { Frequency = 500, Amplitude = 0.5f });

        var errors = PatternSchemaValidator.Validate(pack);

        Assert.Contains(errors, e => e.Contains("layer 0") && e.Contains("frequency is 500Hz"));
    }

    [Fact]
    public void CurvePointsOutsideZeroToOne_AreRejectedByIndex()
    {
        var pack = Pack();
        Pattern(pack).IntensityCurve = IntensityCurve.Custom;
        Pattern(pack).CustomCurvePoints.Add(new CurvePoint { Time = 0f, Intensity = 0f });
        Pattern(pack).CustomCurvePoints.Add(new CurvePoint { Time = 1.4f, Intensity = 2f });

        var errors = PatternSchemaValidator.Validate(pack);

        Assert.Contains(errors, e => e.Contains("customCurvePoints[1]") && e.Contains("time is 1.4"));
        Assert.Contains(errors, e => e.Contains("customCurvePoints[1]") && e.Contains("intensity is 2"));
    }

    [Fact]
    public void CurvePointsThatGoBackwardsInTime_AreRejected()
    {
        var pack = Pack();
        Pattern(pack).IntensityCurve = IntensityCurve.Custom;
        Pattern(pack).CustomCurvePoints.Add(new CurvePoint { Time = 0.6f, Intensity = 0.2f });
        Pattern(pack).CustomCurvePoints.Add(new CurvePoint { Time = 0.2f, Intensity = 0.4f });

        var errors = PatternSchemaValidator.Validate(pack);

        Assert.Contains(errors, e => e.Contains("customCurvePoints[1]") && e.Contains("goes backwards"));
    }

    [Fact]
    public void ACustomCurveWithNoPoints_IsRejected()
    {
        var pack = Pack();
        Pattern(pack).IntensityCurve = IntensityCurve.Custom;

        var errors = PatternSchemaValidator.Validate(pack);

        Assert.Contains(errors, e => e.Contains("intensityCurve is Custom but customCurvePoints is empty"));
    }

    [Fact]
    public void ABlankChainedPatternName_IsRejected()
    {
        var pack = Pack();
        Pattern(pack).ChainedPatterns.Add("   ");

        var errors = PatternSchemaValidator.Validate(pack);

        Assert.Contains(errors, e => e.Contains("chainedPatterns contains a blank pattern name"));
    }

    /// <summary>
    /// A misspelt condition key is the one mistake the runtime cannot report as a failure: the
    /// sequencer has no branch for it, so the gate evaluates to "met" and the pattern plays whenever
    /// its event fires. The validator has to be the thing that notices, and the diagnostic has to
    /// name both the typo and what the author probably meant to write.
    /// </summary>
    [Fact]
    public void AMisspeltConditionKey_IsRejectedWithTheValidKeysListed()
    {
        var pack = Pack();
        Pattern(pack).Conditions = new Dictionary<string, object> { ["time_od_day"] = "morning" };

        var errors = PatternSchemaValidator.Validate(pack);

        Assert.Contains(errors, e => e.Contains("event 'HullDamage'") &&
                                     e.Contains("'time_od_day'") &&
                                     e.Contains("not a recognised condition") &&
                                     e.Contains("time_of_day"));
    }

    /// <summary>Every key the sequencer actually switches over is accepted, one pack at a time.</summary>
    [Fact]
    public void EveryConditionKeyTheRuntimeEvaluates_IsAccepted()
    {
        foreach (var key in PatternSchemaValidator.KnownConditionKeys)
        {
            var pack = Pack();
            Pattern(pack).Conditions = new Dictionary<string, object> { [key] = 0.5 };

            Assert.Empty(PatternSchemaValidator.Validate(pack));
        }
    }

    /// <summary>The sequencer lowercases a key before matching it, so casing is not a typo.</summary>
    [Fact]
    public void AConditionKeyInADifferentCase_IsAcceptedTheWayTheSequencerAcceptsIt()
    {
        var pack = Pack();
        Pattern(pack).Conditions = new Dictionary<string, object> { ["Health_Below"] = 0.5 };

        Assert.Empty(PatternSchemaValidator.Validate(pack));
    }

    [Fact]
    public void ABlankConditionKey_IsRejected()
    {
        var pack = Pack();
        Pattern(pack).Conditions = new Dictionary<string, object> { ["  "] = 1 };

        Assert.Contains(PatternSchemaValidator.Validate(pack),
            e => e.Contains("conditions contains a blank property name"));
    }

    [Fact]
    public void AFutureSchemaVersion_IsRejectedRatherThanGuessedAt()
    {
        var pack = Pack();
        pack.SchemaVersion = "3.0";

        var errors = PatternSchemaValidator.Validate(pack);

        Assert.Contains(errors, e => e.Contains("schemaVersion") && e.Contains("'3.0'"));
    }

    /// <summary>
    /// The validator judges a pack on the shape it has. The current schema is obviously fine; so is
    /// an older-but-supported one, because the loader migrates a pack before validating it and a
    /// pack handed straight here is structurally the same file either way.
    /// </summary>
    [Fact]
    public void TheCurrentSchemaVersionOrAnOlderSupportedOneOrNone_IsAccepted()
    {
        var current = Pack();
        current.SchemaVersion = PatternSchemaVersion.Current.ToString();

        var migratable = Pack();
        migratable.SchemaVersion = "1.0";

        Assert.Empty(PatternSchemaValidator.Validate(current));
        Assert.Empty(PatternSchemaValidator.Validate(migratable));
        Assert.Empty(PatternSchemaValidator.Validate(Pack()));
    }

    [Fact]
    public void APackWithNoShips_IsRejected()
    {
        var pack = Pack();
        pack.Ships.Clear();

        Assert.Contains(PatternSchemaValidator.Validate(pack), e => e.Contains("at least one ship"));
    }

    [Fact]
    public void AnUnnamedPack_IsRejected()
    {
        var pack = Pack();
        pack.Metadata.Name = "  ";

        Assert.Contains(PatternSchemaValidator.Validate(pack), e => e.Contains("metadata.name"));
    }

    /// <summary>Counts stay defined once, in the limits guard; the validator calls through to it.</summary>
    [Fact]
    public void CountLimits_ComeFromTheLimitsGuardRatherThanBeingRestated()
    {
        var pack = Pack();
        Pattern(pack).Layers = Enumerable.Range(0, RequestLimits.MaxPatternLayers + 1)
            .Select(_ => new PatternLayer { Frequency = 40, Amplitude = 0.5f })
            .ToList();

        var errors = PatternSchemaValidator.Validate(pack);

        var layerCountErrors = errors.Where(e => e.Contains("may not define more than")).ToList();
        Assert.Single(layerCountErrors);
        Assert.Contains(RequestLimits.MaxPatternLayers.ToString(), layerCountErrors[0]);
        Assert.Contains("event 'HullDamage'", layerCountErrors[0]);
    }

    /// <summary>The maximum duration is the limits guard's rule, and it still applies here.</summary>
    [Fact]
    public void DurationOverTheCap_IsRejectedThroughTheLimitsGuard()
    {
        var pack = Pack();
        Pattern(pack).Duration = RequestLimits.MaxPatternDurationMs + 1;

        Assert.Contains(PatternSchemaValidator.Validate(pack),
            e => e.Contains($"Duration must not exceed {RequestLimits.MaxPatternDurationMs}ms"));
    }

    /// <summary>
    /// Enum names are already policed by the deserializer, which is why the validator does not check
    /// them again: an unknown <c>pattern</c>, <c>intensityCurve</c>, <c>curve</c> or <c>waveform</c>
    /// never produces a <see cref="HapticPattern"/> at all.
    /// </summary>
    [Theory]
    [InlineData("pattern", "Teleport")]
    [InlineData("intensityCurve", "Parabolic")]
    [InlineData("waveform", "Pulse")]
    public void AnUnknownEnumName_IsRefusedWhileTheFileIsParsed(string field, string badValue)
    {
        var json = $$"""
        {
          "name": "Hull Damage", "frequency": 40, "intensity": 80, "duration": 500, "{{field}}": "{{badValue}}"
        }
        """;

        var exception = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<HapticPattern>(json, PatternJsonOptions));

        // The converter names the field it choked on, which is the diagnostic the loader logs.
        Assert.Contains($"$.{field}", exception.Message);
    }

    // ---- The loader: rejected files are diagnosed, and never cost the catalog a good pack -------

    [Fact]
    public async Task AValidFile_IsLoadedAndIndexed()
    {
        using var temp = new TempDirectory("edbk-schema-valid");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "good.json"), PackJson());

        using var service = Service(temp, out _, out _);
        await service.LoadAllPatternsAsync();

        Assert.Contains("sidewinder", service.GetAllShipTypes());

        var pattern = service.GetPatternForShipEvent("sidewinder", "HullDamage");
        Assert.NotNull(pattern);
        Assert.Equal(45, pattern!.Frequency);
        Assert.Equal(500, pattern.Duration);
    }

    /// <summary>
    /// Packs under <c>patterns/</c> spell a layer's amplitude and start offset <c>intensity</c> and
    /// <c>delay</c>. They must keep their meaning rather than silently falling back to the defaults.
    /// </summary>
    [Fact]
    public async Task LegacyLayerFieldNames_StillCarryTheirValues()
    {
        using var temp = new TempDirectory("edbk-schema-legacy");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "legacy.json"), PackJson(layers: """
        "layers": [ { "frequency": 30, "intensity": 0.6, "delay": 100, "duration": 200, "waveform": "Sine" } ],
        """));

        using var service = Service(temp, out _, out _);
        await service.LoadAllPatternsAsync();

        var pattern = service.GetPatternForShipEvent("sidewinder", "HullDamage");
        var layer = Assert.Single(pattern!.Layers);
        Assert.Equal(0.6f, layer.Amplitude, 3);
        Assert.Equal(100, layer.StartTime);
        Assert.Equal(200, layer.Duration);
    }

    [Fact]
    public async Task AnOutOfBoundsFile_IsRejectedWithADiagnosticNamingTheField()
    {
        using var temp = new TempDirectory("edbk-schema-bounds");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "bad.json"), PackJson(frequency: 500));

        using var service = Service(temp, out var log, out _);
        await service.LoadAllPatternsAsync();

        Assert.Empty(service.GetAllShipTypes());
        Assert.Contains(log.Messages, m => m.Contains("Rejected pattern file") &&
                                           m.Contains("event 'HullDamage'") &&
                                           m.Contains("frequency is 500Hz"));
    }

    [Fact]
    public async Task AFileWithABadTimingRelationship_IsRejectedWithADiagnostic()
    {
        using var temp = new TempDirectory("edbk-schema-timing");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "bad.json"),
            PackJson(duration: 400, fadeIn: 300, fadeOut: 300));

        using var service = Service(temp, out var log, out _);
        await service.LoadAllPatternsAsync();

        Assert.Empty(service.GetAllShipTypes());
        Assert.Contains(log.Messages, m => m.Contains("fadeIn (300ms) + fadeOut (300ms) exceeds duration (400ms)"));
    }

    [Fact]
    public async Task ABadFileDuringAFullReload_KeepsTheLastKnownGoodCatalog()
    {
        using var temp = new TempDirectory("edbk-schema-reload-all");
        var editedPath = Path.Combine(temp.Path, "edited.json");
        var otherPath = Path.Combine(temp.Path, "other.json");

        await File.WriteAllTextAsync(editedPath, PackJson(pack: "Edited Pack", intensity: 80));
        await File.WriteAllTextAsync(otherPath, PackJson(ship: "anaconda", pack: "Other Pack"));

        using var service = Service(temp, out var log, out _);
        await service.LoadAllPatternsAsync();
        Assert.Equal(new[] { "anaconda", "sidewinder" }, service.GetAllShipTypes());

        // The edit that breaks the file must not cost the ship its patterns, nor take the untouched
        // pack down with it.
        await File.WriteAllTextAsync(editedPath, PackJson(pack: "Edited Pack", intensity: 900));
        await service.LoadAllPatternsAsync();

        Assert.Equal(new[] { "anaconda", "sidewinder" }, service.GetAllShipTypes());
        Assert.Equal(80, service.GetPatternForShipEvent("sidewinder", "HullDamage")!.Intensity);
        Assert.Equal(2, service.GetAllPatternPacks().Count);
        Assert.Contains(log.Messages, m => m.Contains("Keeping the last known-good version"));
    }

    [Fact]
    public async Task AFileThatIsNotEvenJson_DoesNotDropWhatWasAlreadyLoaded()
    {
        using var temp = new TempDirectory("edbk-schema-reload-json");
        var path = Path.Combine(temp.Path, "pack.json");

        await File.WriteAllTextAsync(path, PackJson());
        using var service = Service(temp, out _, out _);
        await service.LoadAllPatternsAsync();

        await File.WriteAllTextAsync(path, "{ this is not json");
        await service.LoadAllPatternsAsync();

        Assert.Contains("sidewinder", service.GetAllShipTypes());
    }

    [Fact]
    public async Task ARejectedWatcherReload_LeavesThePreviouslyLoadedVersionInPlace()
    {
        using var temp = new TempDirectory("edbk-schema-reload-one");
        var path = Path.Combine(temp.Path, "pack.json");
        await File.WriteAllTextAsync(path, PackJson(intensity: 80));

        using var service = Service(temp, out var log, out var watcherFactory);
        await service.LoadAllPatternsAsync();

        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        log.OnMessage += message =>
        {
            if (message.Contains("the previously loaded version stays in the catalog"))
            {
                rejected.TrySetResult();
            }
        };

        await File.WriteAllTextAsync(path, PackJson(intensity: 900));

        var watcher = Assert.Single(watcherFactory.Created);
        watcher.Raise(new WatchedFileEvent(WatchedFileChange.Changed, path));

        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("sidewinder", service.GetAllShipTypes());
        Assert.Equal(80, service.GetPatternForShipEvent("sidewinder", "HullDamage")!.Intensity);
    }

    /// <summary>
    /// The packs this repository ships are the first thing the schema has to accept. Anything under
    /// <c>patterns/</c> that declares ships is a pack; <c>default-patterns.json</c> is an event
    /// mapping config that happens to live there and declares none, so it is not one.
    /// </summary>
    [Fact]
    public void EveryPackShippedInTheRepository_SatisfiesTheCanonicalSchema()
    {
        var patternsDirectory = FindRepositoryPatternsDirectory();
        Assert.NotNull(patternsDirectory);

        var checkedPacks = 0;

        foreach (var path in Directory.GetFiles(patternsDirectory!, "*.json", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(path).Equals("schema.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var json = File.ReadAllText(path);
            if (!json.Contains("\"ships\""))
            {
                continue;
            }

            var pack = JsonSerializer.Deserialize<PatternFile>(json, PatternJsonOptions);
            Assert.NotNull(pack);

            var errors = PatternSchemaValidator.Validate(pack!);
            Assert.True(errors.Count == 0,
                $"{Path.GetFileName(path)} is rejected by the schema: {string.Join("; ", errors)}");
            checkedPacks++;
        }

        Assert.True(checkedPacks > 0, "no shipped pattern packs were found to check");
    }

    /// <summary>
    /// Walks up from the test binary to the checkout root, so the test does not depend on the
    /// working directory. The solution file anchors it: the build also copies <c>patterns/</c> next
    /// to the test binary, and that copy collects whatever other tests happened to save there.
    /// </summary>
    private static string? FindRepositoryPatternsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EDButtkicker.sln")))
            {
                return Path.Combine(directory.FullName, "patterns");
            }

            directory = directory.Parent;
        }

        return null;
    }

    // ---- Fixtures -------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions PatternJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static PatternFileService Service(
        TempDirectory temp, out CapturingLogger log, out FakeDirectoryWatcherFactory watcherFactory)
    {
        log = new CapturingLogger();
        watcherFactory = new FakeDirectoryWatcherFactory();

        return new PatternFileService(
            log,
            temp.Path,
            new PatternWatchOptions
            {
                DebounceWindow = TimeSpan.FromMilliseconds(50),
                StabilityWindow = TimeSpan.FromMilliseconds(25),
                MaxStabilityWait = TimeSpan.FromSeconds(3),
                Capacity = 64
            },
            storage: FileSystemPatternStorage.Instance,
            watcherFactory: watcherFactory,
            timeProvider: TimeProvider.System);
    }

    private static HapticPattern Pattern(PatternFile pack) => pack.Ships["sidewinder"].Events!["HullDamage"];

    private static PatternFile Pack() => new()
    {
        Metadata = new PatternFileMetadata
        {
            Name = "Schema Test",
            Version = "1.0.0",
            Author = "Tester"
        },
        Ships = new Dictionary<string, ShipPatternData>
        {
            ["sidewinder"] = new()
            {
                DisplayName = "Sidewinder",
                Class = "small",
                Role = "combat",
                Events = new Dictionary<string, HapticPattern>
                {
                    ["HullDamage"] = new()
                    {
                        Name = "Hull Damage",
                        Pattern = PatternType.SharpPulse,
                        Frequency = 45,
                        Intensity = 80,
                        Duration = 500,
                        FadeIn = 50,
                        FadeOut = 100
                    }
                }
            }
        }
    };

    private static string PackJson(
        string ship = "sidewinder",
        string pack = "Schema Test",
        int frequency = 45,
        int intensity = 80,
        int duration = 500,
        int fadeIn = 50,
        int fadeOut = 100,
        string layers = "") => $$"""
        {
          "metadata": { "name": "{{pack}}", "version": "1.0.0", "author": "Tester", "description": "d", "tags": [], "created": "2026-01-01T00:00:00Z", "compatibility": "1.0.0" },
          "ships": {
            "{{ship}}": {
              "displayName": "Test Ship",
              "class": "small",
              "role": "combat",
              "events": {
                "HullDamage": {
                  "name": "Hull Damage",
                  "pattern": "SharpPulse",
                  "frequency": {{frequency}},
                  "intensity": {{intensity}},
                  "duration": {{duration}},
                  "fadeIn": {{fadeIn}},
                  "fadeOut": {{fadeOut}},
                  {{layers}}
                  "intensityCurve": "Linear"
                }
              }
            }
          }
        }
        """;

    /// <summary>Keeps every formatted log line so a test can assert on the diagnostic a pack got.</summary>
    private sealed class CapturingLogger : ILogger<PatternFileService>
    {
        private readonly List<string> _messages = new();

        public event Action<string>? OnMessage;

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return _messages.ToList();
                }
            }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);

            lock (_messages)
            {
                _messages.Add(message);
            }

            OnMessage?.Invoke(message);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    /// <summary>Hands out watchers the test raises events on, so no OS event delivery is involved.</summary>
    private sealed class FakeDirectoryWatcherFactory : IDirectoryWatcherFactory
    {
        private readonly List<FakeDirectoryWatcher> _created = new();

        public IReadOnlyList<FakeDirectoryWatcher> Created
        {
            get
            {
                lock (_created)
                {
                    return _created.ToList();
                }
            }
        }

        public IDirectoryWatcher Create(string directory, string filter, bool includeSubdirectories = false)
        {
            var watcher = new FakeDirectoryWatcher();

            lock (_created)
            {
                _created.Add(watcher);
            }

            return watcher;
        }
    }

    private sealed class FakeDirectoryWatcher : IDirectoryWatcher
    {
        public event Action<WatchedFileEvent>? Changed;

        /// <summary>A fake watcher never loses events, so nothing ever subscribes meaningfully.</summary>
        public event Action<Exception>? Error
        {
            add { }
            remove { }
        }

        public void Start()
        {
        }

        public void Raise(WatchedFileEvent e) => Changed?.Invoke(e);

        public void Dispose() => Changed = null;
    }
}
