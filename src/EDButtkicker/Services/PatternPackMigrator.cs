using System.Text.Json.Nodes;

namespace EDButtkicker.Services;

/// <summary>
/// What negotiating one pack's schema version produced: either a pack document in the current
/// schema, ready to deserialize, or the reason it was refused.
/// </summary>
public sealed class PatternPackMigrationResult
{
    private PatternPackMigrationResult(
        JsonObject? pack, PatternSchemaVersion declaredVersion, string? migrationSummary, string? error)
    {
        Pack = pack;
        DeclaredVersion = declaredVersion;
        MigrationSummary = migrationSummary;
        Error = error;
    }

    /// <summary>The pack document in the current schema, or null when it was refused.</summary>
    public JsonObject? Pack { get; }

    /// <summary>The version the file declared, or the assumed one when it declared none.</summary>
    public PatternSchemaVersion DeclaredVersion { get; }

    /// <summary>What each applied migration changed, or null when none were needed.</summary>
    public string? MigrationSummary { get; }

    /// <summary>The full, actionable diagnostic for a refused pack, or null when it was accepted.</summary>
    public string? Error { get; }

    public bool Succeeded => Error == null;

    /// <summary>True when the document was upgraded in memory rather than read as written.</summary>
    public bool WasMigrated => MigrationSummary != null;

    internal static PatternPackMigrationResult Ready(
        JsonObject pack, PatternSchemaVersion declaredVersion, string? migrationSummary) =>
        new(pack, declaredVersion, migrationSummary, null);

    internal static PatternPackMigrationResult Rejected(PatternSchemaVersion declaredVersion, string error) =>
        new(null, declaredVersion, null, error);
}

/// <summary>
/// The compatibility gate every pattern pack passes before it is deserialized: it reads the pack's
/// declared <see cref="PatternSchemaVersion"/>, upgrades an older-but-supported document to the
/// current schema, and refuses one this build cannot read with a diagnostic naming the file, the
/// version it asked for and the highest version available.
///
/// Migration is deliberately done on the parsed JSON document rather than on
/// <see cref="PatternFile"/>: a renamed or restructured field has to be moved before the
/// deserializer sees it, and working on the document means a new schema revision is one entry in
/// <see cref="Migrations"/> rather than a legacy alias welded onto the model forever.
///
/// Nothing here writes to disk. The upgraded document exists only for the load that asked for it -
/// the pack the user wrote stays exactly as they wrote it, and the next release can ship a different
/// migration for the same file without having to undo this one.
/// </summary>
public static class PatternPackMigrator
{
    private delegate void MigrationStep(JsonObject pack);

    /// <summary>One major-version hop, and the plain-language summary its log line carries.</summary>
    private sealed record Migration(int FromMajor, int ToMajor, string Summary, MigrationStep Apply);

    /// <summary>
    /// Every hop this build knows, one per major version. v1 spelled a layer's amplitude and start
    /// offset <c>intensity</c> and <c>delay</c>; v2 uses the names the engine and the editor both
    /// use, <c>amplitude</c> and <c>startTime</c>.
    /// </summary>
    private static readonly IReadOnlyList<Migration> Migrations = new[]
    {
        new Migration(1, 2, "layer 'intensity' -> 'amplitude', layer 'delay' -> 'startTime'", MigrateV1ToV2)
    };

    /// <summary>
    /// Brings <paramref name="pack"/> up to <see cref="PatternSchemaVersion.Current"/>, mutating the
    /// caller's document in place, or explains why it cannot.
    /// <paramref name="filePath"/> only ever appears in diagnostics.
    /// </summary>
    public static PatternPackMigrationResult Migrate(JsonObject pack, string filePath)
    {
        ArgumentNullException.ThrowIfNull(pack);

        var declared = ReadDeclaredVersion(pack);
        var compatibility = PatternSchemaVersion.Classify(declared, out var version);

        switch (compatibility)
        {
            case PatternSchemaCompatibility.TooNew:
                return PatternPackMigrationResult.Rejected(version,
                    $"Rejected pattern file {filePath}: it declares pattern pack schema v{declared}, but this " +
                    $"build reads at most v{PatternSchemaVersion.Current}. Update EDButtkicker, or re-save the " +
                    $"pack against schema v{PatternSchemaVersion.Current}.");

            case PatternSchemaCompatibility.TooOld:
                return PatternPackMigrationResult.Rejected(version,
                    $"Rejected pattern file {filePath}: it declares pattern pack schema v{declared}, which is " +
                    $"older than the oldest schema this build can migrate; this build reads " +
                    $"v{PatternSchemaVersion.OldestSupported} through v{PatternSchemaVersion.Current}.");

            case PatternSchemaCompatibility.Malformed:
                return PatternPackMigrationResult.Rejected(version,
                    $"Rejected pattern file {filePath}: schemaVersion '{declared}' is not a version number. This " +
                    $"build reads pattern pack schema v{PatternSchemaVersion.OldestSupported} through " +
                    $"v{PatternSchemaVersion.Current}, written as 'major' or 'major.minor'.");
        }

        var applied = new List<string>();
        var current = version;

        while (current.Major < PatternSchemaVersion.Current.Major)
        {
            var step = Migrations.FirstOrDefault(m => m.FromMajor == current.Major);

            if (step == null)
            {
                // Only reachable if a major is added to the supported span without its migration.
                return PatternPackMigrationResult.Rejected(version,
                    $"Rejected pattern file {filePath}: it declares pattern pack schema v{version}, and this " +
                    $"build has no migration from v{current} to v{PatternSchemaVersion.Current}.");
            }

            step.Apply(pack);
            applied.Add(step.Summary);
            current = new PatternSchemaVersion(step.ToMajor, 0);
        }

        // The document now is a current-schema document, whatever it said on the way in.
        SetProperty(pack, "schemaVersion", PatternSchemaVersion.Current.ToString());

        return PatternPackMigrationResult.Ready(
            pack, version, applied.Count == 0 ? null : string.Join("; ", applied));
    }

    /// <summary>v1 -> v2: the layer field rename, applied to every layer of every event of every ship.</summary>
    private static void MigrateV1ToV2(JsonObject pack)
    {
        foreach (var layer in EnumerateLayers(pack))
        {
            RenameProperty(layer, "intensity", "amplitude");
            RenameProperty(layer, "delay", "startTime");
        }
    }

    /// <summary>
    /// Every <c>ships.*.events.*.layers[]</c> object in the document. A pack whose shape does not
    /// match - a missing section, a string where an object belongs - yields nothing here rather than
    /// throwing: it is the schema validator's job to say what is wrong with it, not the migrator's.
    /// </summary>
    private static List<JsonObject> EnumerateLayers(JsonObject pack)
    {
        var layers = new List<JsonObject>();

        if (GetProperty(pack, "ships") is not JsonObject ships)
        {
            return layers;
        }

        foreach (var ship in ships)
        {
            if (ship.Value is not JsonObject shipObject ||
                GetProperty(shipObject, "events") is not JsonObject events)
            {
                continue;
            }

            foreach (var patternEvent in events)
            {
                if (patternEvent.Value is not JsonObject patternObject ||
                    GetProperty(patternObject, "layers") is not JsonArray layerArray)
                {
                    continue;
                }

                layers.AddRange(layerArray.OfType<JsonObject>());
            }
        }

        return layers;
    }

    /// <summary>
    /// The pack's declared version verbatim, or null when it declares none. Read case-insensitively
    /// because the loader deserializes case-insensitively - the two must agree on what a file says.
    /// </summary>
    private static string? ReadDeclaredVersion(JsonObject pack) =>
        GetProperty(pack, "schemaVersion") is JsonValue value && value.TryGetValue<string>(out var declared)
            ? declared
            : null;

    private static JsonNode? GetProperty(JsonObject target, string name)
    {
        if (target.TryGetPropertyValue(name, out var direct))
        {
            return direct;
        }

        foreach (var property in target)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    /// <summary>Sets <paramref name="name"/>, replacing any spelling of it the file happened to use.</summary>
    private static void SetProperty(JsonObject target, string name, string value)
    {
        var existing = FindKey(target, name);

        if (existing != null)
        {
            target.Remove(existing);
        }

        target[name] = value;
    }

    /// <summary>
    /// Moves <paramref name="from"/> to <paramref name="to"/>. A document that already carries the
    /// new name keeps that value - a hand-edited pack that declares both is taking the current
    /// spelling at its word - and the old name is dropped either way so the result is a clean
    /// current-schema document.
    /// </summary>
    private static void RenameProperty(JsonObject target, string from, string to)
    {
        var key = FindKey(target, from);

        if (key == null)
        {
            return;
        }

        // Detached from its parent first: a JsonNode may only ever have one.
        var value = target[key]?.DeepClone();
        target.Remove(key);

        if (FindKey(target, to) != null)
        {
            return;
        }

        target[to] = value;
    }

    private static string? FindKey(JsonObject target, string name)
    {
        foreach (var property in target)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Key;
            }
        }

        return null;
    }
}
