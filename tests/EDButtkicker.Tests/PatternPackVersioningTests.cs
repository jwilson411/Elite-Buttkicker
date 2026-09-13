using System.Text.Json;
using System.Text.Json.Nodes;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The schema-version contract a community pattern pack is read under: which versions this build
/// accepts, what a pack written against an older one is turned into, and what a pack from a newer
/// one is told. The promise that costs the most if it breaks is the one about the filesystem - a
/// pack the app migrates is a pack the app must not rewrite, because the author's file is the only
/// copy they have.
///
/// Nothing here opens an audio device; schema negotiation is pure file handling.
/// </summary>
public class PatternPackVersioningTests
{
    // ---- Parsing: what counts as a schema version ------------------------------------------------

    [Theory]
    [InlineData("1", 1, 0)]
    [InlineData("2", 2, 0)]
    [InlineData("1.0", 1, 0)]
    [InlineData("2.4", 2, 4)]
    [InlineData(" 2.4 ", 2, 4)]
    [InlineData("2.4.7", 2, 4)]
    [InlineData("10.11", 10, 11)]
    public void AVersionNumber_ParsesToItsMajorAndMinor(string text, int major, int minor)
    {
        Assert.True(PatternSchemaVersion.TryParse(text, out var version));
        Assert.Equal(new PatternSchemaVersion(major, minor), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("v2")]
    [InlineData("2.x")]
    [InlineData("-1")]
    [InlineData("2.-1")]
    [InlineData("2..0")]
    [InlineData("2.0-beta")]
    public void SomethingThatIsNotAVersionNumber_DoesNotParse(string? text)
    {
        Assert.False(PatternSchemaVersion.TryParse(text, out _));
    }

    [Fact]
    public void AParsedVersion_PrintsBackAsMajorDotMinor()
    {
        Assert.Equal("2.0", PatternSchemaVersion.Current.ToString());
        Assert.Equal("1.0", PatternSchemaVersion.OldestSupported.ToString());
        Assert.Equal("3.7", new PatternSchemaVersion(3, 7).ToString());
    }

    // ---- The compatibility rule ------------------------------------------------------------------

    [Theory]
    // A pack that says nothing predates the field, so it is a v1 pack and gets migrated.
    [InlineData(null, PatternSchemaCompatibility.Migratable)]
    [InlineData("", PatternSchemaCompatibility.Migratable)]
    [InlineData("1", PatternSchemaCompatibility.Migratable)]
    [InlineData("1.0", PatternSchemaCompatibility.Migratable)]
    [InlineData("1.9", PatternSchemaCompatibility.Migratable)]
    [InlineData("2", PatternSchemaCompatibility.Current)]
    [InlineData("2.0", PatternSchemaCompatibility.Current)]
    // A minor this build has never shipped would mean fields it would silently drop.
    [InlineData("2.1", PatternSchemaCompatibility.TooNew)]
    [InlineData("3.0", PatternSchemaCompatibility.TooNew)]
    [InlineData("99", PatternSchemaCompatibility.TooNew)]
    [InlineData("0.9", PatternSchemaCompatibility.TooOld)]
    [InlineData("latest", PatternSchemaCompatibility.Malformed)]
    public void EachDeclaredVersion_IsClassifiedByTheCompatibilityRule(
        string? declared, PatternSchemaCompatibility expected)
    {
        Assert.Equal(expected, PatternSchemaVersion.Classify(declared, out _));
    }

    [Fact]
    public void APackThatDeclaresNothing_ResolvesToTheVersionThatPredatedTheField()
    {
        PatternSchemaVersion.Classify(null, out var version);

        Assert.Equal(PatternSchemaVersion.AssumedWhenAbsent, version);
        Assert.Equal(PatternSchemaVersion.OldestSupported, version);
    }

    // ---- Migration, at the document level --------------------------------------------------------

    /// <summary>
    /// The v1 -> v2 rename, proved on the document rather than through the model: the deserializer
    /// also accepts the old spellings, so only looking at the loaded pattern would pass whether the
    /// migration ran or not.
    /// </summary>
    [Fact]
    public void AV1Document_HasItsLayerFieldsRenamedAndIsStampedCurrent()
    {
        var document = Document(LegacyPackJson);

        var result = PatternPackMigrator.Migrate(document, "legacy.json");

        Assert.True(result.Succeeded);
        Assert.True(result.WasMigrated);
        Assert.Equal(PatternSchemaVersion.OldestSupported, result.DeclaredVersion);
        Assert.Equal(PatternSchemaVersion.Current.ToString(), (string?)result.Pack!["schemaVersion"]);

        var layer = Layer(result.Pack);
        Assert.Equal(0.6, (double)layer["amplitude"]!, 3);
        Assert.Equal(100, (int)layer["startTime"]!);
        Assert.False(layer.ContainsKey("intensity"));
        Assert.False(layer.ContainsKey("delay"));

        // The rename is scoped to layers: a pattern's own intensity is a different field that
        // happens to share a name, and moving it would silence every migrated pack.
        Assert.Equal(80, (int)Event(result.Pack)["intensity"]!);
    }

    [Fact]
    public void ACurrentDocument_IsLeftAloneRatherThanMigrated()
    {
        var document = Document(CurrentPackJson);

        var result = PatternPackMigrator.Migrate(document, "current.json");

        Assert.True(result.Succeeded);
        Assert.False(result.WasMigrated);
        Assert.Null(result.MigrationSummary);
        Assert.Equal(PatternSchemaVersion.Current, result.DeclaredVersion);

        var layer = Layer(result.Pack!);
        Assert.Equal(0.6, (double)layer["amplitude"]!, 3);
        Assert.Equal(100, (int)layer["startTime"]!);
    }

    /// <summary>A hand-edited pack carrying both spellings is taken at its current-schema word.</summary>
    [Fact]
    public void ADocumentCarryingBothSpellings_KeepsTheCurrentOne()
    {
        var document = Document(PackJson(schemaVersion: null, layers: """
        "layers": [ { "frequency": 30, "amplitude": 0.25, "intensity": 0.9, "startTime": 40, "delay": 300 } ],
        """));

        var result = PatternPackMigrator.Migrate(document, "both.json");

        var layer = Layer(result.Pack!);
        Assert.Equal(0.25, (double)layer["amplitude"]!, 3);
        Assert.Equal(40, (int)layer["startTime"]!);
        Assert.False(layer.ContainsKey("intensity"));
        Assert.False(layer.ContainsKey("delay"));
    }

    [Theory]
    [InlineData("3.0", "reads at most v2.0")]
    [InlineData("2.1", "reads at most v2.0")]
    [InlineData("0.9", "older than the oldest schema")]
    [InlineData("latest", "is not a version number")]
    public void ADocumentThisBuildCannotRead_IsRejectedByNameAndVersion(string declared, string expectedPhrase)
    {
        var result = PatternPackMigrator.Migrate(Document(PackJson(declared)), "community-pack.json");

        Assert.False(result.Succeeded);
        Assert.Null(result.Pack);
        Assert.Contains("community-pack.json", result.Error);
        Assert.Contains(declared, result.Error);
        Assert.Contains(expectedPhrase, result.Error);
    }

    /// <summary>A pack missing the sections the migration walks is the validator's problem, not a crash.</summary>
    [Theory]
    [InlineData("""{ "schemaVersion": "1.0" }""")]
    [InlineData("""{ "schemaVersion": "1.0", "ships": "not an object" }""")]
    [InlineData("""{ "schemaVersion": "1.0", "ships": { "sidewinder": { "events": { "E": { "layers": "no" } } } } }""")]
    [InlineData("""{ "schemaVersion": "1.0", "ships": { "sidewinder": { "events": { "E": { "layers": [ null, 7 ] } } } } }""")]
    public void AMisshapenDocument_MigratesWithoutThrowing(string json)
    {
        var result = PatternPackMigrator.Migrate(Document(json), "odd.json");

        Assert.True(result.Succeeded);
        Assert.Equal(PatternSchemaVersion.Current.ToString(), (string?)result.Pack!["schemaVersion"]);
    }

    // ---- Migration, through the loader, with the file on disk watched ----------------------------

    /// <summary>
    /// The promise that matters most: a v1 pack loads with its values intact, and the file the user
    /// wrote is still the file the user wrote - not rewritten in place, and not quietly joined by a
    /// migrated copy the app decided to save for them.
    /// </summary>
    [Fact]
    public async Task AnOlderSupportedPack_IsMigratedInMemoryAndLeftUntouchedOnDisk()
    {
        using var temp = new TempDirectory("edbk-migrate");
        var path = Path.Combine(temp.Path, "legacy.json");
        await File.WriteAllTextAsync(path, LegacyPackJson);

        using var service = Service(temp, out _);

        var filesBefore = Snapshot(temp);
        var bytesBefore = await File.ReadAllBytesAsync(path);
        var writtenBefore = File.GetLastWriteTimeUtc(path);

        await service.LoadAllPatternsAsync();

        var pattern = service.GetPatternForShipEvent("sidewinder", "HullDamage");
        Assert.NotNull(pattern);

        // Migrated to the current representation: layer amplitude and start offset carry the values
        // the v1 file spelled 'intensity' and 'delay'.
        var layer = Assert.Single(pattern!.Layers);
        Assert.Equal(0.6f, layer.Amplitude, 3);
        Assert.Equal(100, layer.StartTime);
        Assert.Equal(200, layer.Duration);
        Assert.Equal(WaveformType.Sine, layer.Waveform);

        // The pattern's own fields are untouched by the layer rename.
        Assert.Equal(80, pattern.Intensity);
        Assert.Equal(45, pattern.Frequency);
        Assert.Equal(500, pattern.Duration);

        // The catalog reports the version it holds, not the one the file declared.
        var pack = Assert.Single(service.GetAllPatternPacks());
        Assert.Equal("Legacy Pack", pack.Name);

        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(path));
        Assert.Equal(writtenBefore, File.GetLastWriteTimeUtc(path));
        Assert.Equal(filesBefore, Snapshot(temp));
    }

    [Fact]
    public async Task APackDeclaringTheOlderVersionExplicitly_IsMigratedTheSameWay()
    {
        using var temp = new TempDirectory("edbk-migrate-declared");
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, "legacy.json"), PackJson("1.0", LegacyLayers));

        using var service = Service(temp, out var log);
        await service.LoadAllPatternsAsync();

        var layer = Assert.Single(service.GetPatternForShipEvent("sidewinder", "HullDamage")!.Layers);
        Assert.Equal(0.6f, layer.Amplitude, 3);
        Assert.Equal(100, layer.StartTime);

        // The migration is reported, and says plainly that the file was not touched.
        Assert.Contains(log.Messages, m =>
            m.Contains("Migrated pattern file") &&
            m.Contains("from pattern pack schema v1.0 to v2.0") &&
            m.Contains("left exactly as its author wrote it"));
    }

    // ---- Round trip ------------------------------------------------------------------------------

    /// <summary>
    /// Write a pack, load it, export it, load that: what the engine plays at the end is what the
    /// pack said at the start. The export is the app's own writer, so this also pins that whatever
    /// it stamps as a schema version is a version the loader accepts.
    /// </summary>
    [Fact]
    public async Task APackSurvivesAWriteLoadExportLoadRoundTrip()
    {
        using var temp = new TempDirectory("edbk-roundtrip");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "source.json"), CurrentPackJson);

        using var service = Service(temp, out _);
        await service.LoadAllPatternsAsync();

        var original = service.GetPatternForShipEvent("sidewinder", "HullDamage");
        Assert.NotNull(original);

        var exportPath = await service.ExportPatternPackAsync("Round Trip", new List<string> { "sidewinder" });

        // Whatever the exporter stamped has to be a version the loader reads as current, or every
        // export would come back through the migrator.
        var exported = Document(await File.ReadAllTextAsync(exportPath));
        Assert.Equal(PatternSchemaVersion.Current.ToString(), (string?)exported["schemaVersion"]);
        Assert.Equal(
            PatternSchemaCompatibility.Current,
            PatternSchemaVersion.Classify((string?)exported["schemaVersion"], out _));

        await service.LoadAllPatternsAsync();

        var reloaded = service.GetPatternForShipEvent("sidewinder", "HullDamage", preferredPack: "Round Trip");
        Assert.NotNull(reloaded);
        AssertSameEffectivePattern(original!, reloaded!);
    }

    /// <summary>A v1 pack that has been round-tripped through the exporter comes out as a v2 pack.</summary>
    [Fact]
    public async Task ExportingAMigratedPack_WritesTheCurrentSchemaWithCurrentFieldNames()
    {
        using var temp = new TempDirectory("edbk-roundtrip-legacy");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "legacy.json"), LegacyPackJson);

        using var service = Service(temp, out _);
        await service.LoadAllPatternsAsync();

        var original = service.GetPatternForShipEvent("sidewinder", "HullDamage");
        var exportPath = await service.ExportPatternPackAsync("Upgraded", new List<string> { "sidewinder" });

        var exported = Document(await File.ReadAllTextAsync(exportPath));
        var layer = Layer(exported);
        Assert.Equal(0.6, (double)layer["amplitude"]!, 3);
        Assert.Equal(100, (int)layer["startTime"]!);
        Assert.False(layer.ContainsKey("delay"));

        await service.LoadAllPatternsAsync();

        var reloaded = service.GetPatternForShipEvent("sidewinder", "HullDamage", preferredPack: "Upgraded");
        Assert.NotNull(reloaded);
        AssertSameEffectivePattern(original!, reloaded!);
    }

    // ---- Incompatible versions -------------------------------------------------------------------

    /// <summary>
    /// One pack from a newer EDButtkicker must cost the catalog exactly that pack: it is refused with
    /// a message its author can act on, nothing throws out of the load, and every other pack in the
    /// directory is indexed as usual.
    /// </summary>
    [Fact]
    public async Task APackFromANewerSchema_IsRejectedWithoutCostingTheRestOfTheCatalog()
    {
        using var temp = new TempDirectory("edbk-too-new");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "good.json"), CurrentPackJson);
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, "from-the-future.json"),
            PackJson("99.4", LegacyLayers, packName: "Future Pack", ship: "anaconda"));

        using var service = Service(temp, out var log);

        // The load completes rather than throwing the bad file's problem at the caller.
        await service.LoadAllPatternsAsync();

        // The good pack is indexed and playable; the future one is simply not there.
        Assert.Contains("sidewinder", service.GetAllShipTypes());
        Assert.DoesNotContain("anaconda", service.GetAllShipTypes());
        Assert.NotNull(service.GetPatternForShipEvent("sidewinder", "HullDamage"));
        var pack = Assert.Single(service.GetAllPatternPacks());
        Assert.Equal("Current Pack", pack.Name);

        // The diagnostic names the file, the version it asked for, and the highest one available.
        Assert.Contains(log.Messages, m =>
            m.Contains("from-the-future.json") &&
            m.Contains("v99.4") &&
            m.Contains($"reads at most v{PatternSchemaVersion.Current}"));
    }

    /// <summary>
    /// The same rule on a reload: editing a loaded pack to declare a version this build cannot read
    /// leaves the version that was already playing in place, exactly as any other rejected edit does.
    /// </summary>
    [Fact]
    public async Task AnEditThatDeclaresAFutureSchema_LeavesTheLoadedVersionInPlace()
    {
        using var temp = new TempDirectory("edbk-too-new-reload");
        var path = Path.Combine(temp.Path, "pack.json");
        await File.WriteAllTextAsync(path, CurrentPackJson);

        using var service = Service(temp, out var log);
        await service.LoadAllPatternsAsync();
        Assert.NotNull(service.GetPatternForShipEvent("sidewinder", "HullDamage"));

        await File.WriteAllTextAsync(path, PackJson("3.0", LegacyLayers, packName: "Current Pack"));
        await service.LoadAllPatternsAsync();

        var stillThere = service.GetPatternForShipEvent("sidewinder", "HullDamage");
        Assert.NotNull(stillThere);
        Assert.Equal(45, stillThere!.Frequency);

        Assert.Contains(log.Messages, m => m.Contains("Keeping the last known-good version"));
    }

    // ---- Fixtures --------------------------------------------------------------------------------

    private static void AssertSameEffectivePattern(HapticPattern expected, HapticPattern actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Pattern, actual.Pattern);
        Assert.Equal(expected.Frequency, actual.Frequency);
        Assert.Equal(expected.Intensity, actual.Intensity);
        Assert.Equal(expected.Duration, actual.Duration);
        Assert.Equal(expected.FadeIn, actual.FadeIn);
        Assert.Equal(expected.FadeOut, actual.FadeOut);
        Assert.Equal(expected.IntensityCurve, actual.IntensityCurve);
        Assert.Equal(expected.Waveform, actual.Waveform);
        Assert.Equal(expected.MinIntensity, actual.MinIntensity);
        Assert.Equal(expected.MaxIntensity, actual.MaxIntensity);
        Assert.Equal(expected.IntensityFromDamage, actual.IntensityFromDamage);
        Assert.Equal(expected.Layers.Count, actual.Layers.Count);

        for (var index = 0; index < expected.Layers.Count; index++)
        {
            Assert.Equal(expected.Layers[index].Frequency, actual.Layers[index].Frequency);
            Assert.Equal(expected.Layers[index].Amplitude, actual.Layers[index].Amplitude, 3);
            Assert.Equal(expected.Layers[index].StartTime, actual.Layers[index].StartTime);
            Assert.Equal(expected.Layers[index].Duration, actual.Layers[index].Duration);
            Assert.Equal(expected.Layers[index].Waveform, actual.Layers[index].Waveform);
            Assert.Equal(expected.Layers[index].PhaseOffset, actual.Layers[index].PhaseOffset);
        }
    }

    /// <summary>Every file under the pattern root, with its size - enough to see a stray write.</summary>
    private static List<string> Snapshot(TempDirectory temp) =>
        Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories)
            .Select(path => $"{Path.GetRelativePath(temp.Path, path)}:{new FileInfo(path).Length}")
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();

    private static JsonObject Document(string json) =>
        (JsonObject)JsonNode.Parse(json, nodeOptions: null, new JsonDocumentOptions { AllowTrailingCommas = true })!;

    private static JsonObject Event(JsonObject pack) =>
        (JsonObject)pack["ships"]!["sidewinder"]!["events"]!["HullDamage"]!;

    private static JsonObject Layer(JsonObject pack) => (JsonObject)Event(pack)["layers"]![0]!;

    private const string LegacyLayers = """
        "layers": [ { "frequency": 30, "intensity": 0.6, "delay": 100, "duration": 200, "waveform": "Sine" } ],
        """;

    private const string CurrentLayers = """
        "layers": [ { "frequency": 30, "amplitude": 0.6, "startTime": 100, "duration": 200, "waveform": "Sine" } ],
        """;

    /// <summary>A pack as the first release wrote them: no schemaVersion, v1 layer field names.</summary>
    private static string LegacyPackJson => PackJson(schemaVersion: null, LegacyLayers, packName: "Legacy Pack");

    private static string CurrentPackJson =>
        PackJson(PatternSchemaVersion.Current.ToString(), CurrentLayers, packName: "Current Pack");

    private static string PackJson(
        string? schemaVersion,
        string layers = "",
        string packName = "Version Test",
        string ship = "sidewinder")
    {
        var declaration = schemaVersion == null ? "" : $"""
          "schemaVersion": "{schemaVersion}",
        """;

        return $$"""
        {
          {{declaration}}
          "metadata": { "name": "{{packName}}", "version": "1.0.0", "author": "Tester" },
          "ships": {
            "{{ship}}": {
              "displayName": "Test Ship",
              "class": "small",
              "role": "combat",
              "events": {
                "HullDamage": {
                  "name": "Hull Damage",
                  "pattern": "MultiLayer",
                  "frequency": 45,
                  "intensity": 80,
                  "duration": 500,
                  "fadeIn": 50,
                  "fadeOut": 100,
                  {{layers}}
                  "intensityCurve": "Linear"
                }
              }
            }
          }
        }
        """;
    }

    private static PatternFileService Service(TempDirectory temp, out CapturingLogger log)
    {
        log = new CapturingLogger();

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
            // A fake watcher so the OS never races the test by reloading behind an assertion.
            watcherFactory: new SilentDirectoryWatcherFactory(),
            timeProvider: TimeProvider.System);
    }

    /// <summary>Keeps every formatted log line so a test can assert on the diagnostic a pack got.</summary>
    private sealed class CapturingLogger : ILogger<PatternFileService>
    {
        private readonly List<string> _messages = new();

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
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    /// <summary>A watcher that never raises anything: these tests drive loads by hand.</summary>
    private sealed class SilentDirectoryWatcherFactory : IDirectoryWatcherFactory
    {
        public IDirectoryWatcher Create(string directory, string filter, bool includeSubdirectories = false) =>
            new SilentDirectoryWatcher();

        private sealed class SilentDirectoryWatcher : IDirectoryWatcher
        {
            public event Action<WatchedFileEvent>? Changed
            {
                add { }
                remove { }
            }

            public event Action<Exception>? Error
            {
                add { }
                remove { }
            }

            public void Start()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
