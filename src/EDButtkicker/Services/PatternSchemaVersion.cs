using System.Globalization;

namespace EDButtkicker.Services;

/// <summary>
/// What this build can do with a pattern pack that declares a given schema version.
/// </summary>
public enum PatternSchemaCompatibility
{
    /// <summary>The pack is already written against the schema this build implements.</summary>
    Current,

    /// <summary>Older, but this build still has a migration path to <see cref="PatternSchemaVersion.Current"/>.</summary>
    Migratable,

    /// <summary>Written against a schema this build does not know; reading it would be guesswork.</summary>
    TooNew,

    /// <summary>Older than the oldest revision this build still carries a migration for.</summary>
    TooOld,

    /// <summary>Not a version number at all.</summary>
    Malformed
}

/// <summary>
/// The revision of <c>patterns/schema.json</c> a pattern pack was written against, and the rule for
/// deciding whether this build can read it.
///
/// A pack declares it in the top-level <c>schemaVersion</c> field as <c>major[.minor]</c>. The major
/// number is the migration boundary: it is bumped only when the shape of a pack changes in a way an
/// older reader would misread, and every bump ships a migration in
/// <see cref="PatternPackMigrator"/>. The minor number marks additions within a major, which need no
/// migration - a build reads every minor at or below its own, and refuses a higher one rather than
/// silently dropping a field it has never heard of.
///
/// The rule, in full:
/// <list type="bullet">
///   <item>no <c>schemaVersion</c> at all - read as <see cref="AssumedWhenAbsent"/>, because the
///   field was added after the first packs shipped and every one of them is a v1 pack;</item>
///   <item>major below <see cref="Current"/>'s and at or above <see cref="OldestSupported"/>'s -
///   migrated in memory, never on disk;</item>
///   <item>major equal to <see cref="Current"/>'s, minor at or below it - read as written;</item>
///   <item>anything newer than <see cref="Current"/>, by either number - refused by name, so a pack
///   from a newer EDButtkicker is never half-read into playback;</item>
///   <item>anything older than <see cref="OldestSupported"/>, or not a version number at all -
///   refused the same way.</item>
/// </list>
///
/// The published contract this implements is <c>docs/pattern-pack-schema-versioning.md</c>.
/// </summary>
public readonly record struct PatternSchemaVersion(int Major, int Minor)
{
    /// <summary>The schema this build reads and writes; <c>patterns/schema.json</c> documents it.</summary>
    public static PatternSchemaVersion Current { get; } = new(2, 0);

    /// <summary>The oldest schema this build still carries a migration path from.</summary>
    public static PatternSchemaVersion OldestSupported { get; } = new(1, 0);

    /// <summary>
    /// What a pack that declares nothing is read as. The <c>schemaVersion</c> field postdates the
    /// first release, so silence means "the schema that existed before the field did", not "current".
    /// </summary>
    public static PatternSchemaVersion AssumedWhenAbsent { get; } = OldestSupported;

    /// <summary>
    /// Parses <c>major</c>, <c>major.minor</c> or any longer dotted form (trailing components are
    /// ignored - they cannot change compatibility). Anything else, including a signed or padded
    /// number, is not a schema version.
    /// </summary>
    public static bool TryParse(string? text, out PatternSchemaVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split('.');
        var numbers = new int[parts.Length];

        for (var index = 0; index < parts.Length; index++)
        {
            // NumberStyles.None rejects a sign, whitespace and thousands separators, so "-1", " 1"
            // and "1 000" are refused rather than being coerced into a version.
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]))
            {
                return false;
            }
        }

        version = new PatternSchemaVersion(numbers[0], parts.Length > 1 ? numbers[1] : 0);
        return true;
    }

    /// <summary>
    /// What this build can do with <paramref name="declared"/>, and the version it resolved to
    /// (<see cref="AssumedWhenAbsent"/> when the pack declared nothing).
    /// </summary>
    public static PatternSchemaCompatibility Classify(string? declared, out PatternSchemaVersion version)
    {
        if (string.IsNullOrWhiteSpace(declared))
        {
            version = AssumedWhenAbsent;
        }
        else if (!TryParse(declared, out version))
        {
            return PatternSchemaCompatibility.Malformed;
        }

        if (version.IsNewerThan(Current))
        {
            return PatternSchemaCompatibility.TooNew;
        }

        if (version.Major < OldestSupported.Major)
        {
            return PatternSchemaCompatibility.TooOld;
        }

        return version.Major < Current.Major
            ? PatternSchemaCompatibility.Migratable
            : PatternSchemaCompatibility.Current;
    }

    /// <summary>Major first, then minor - the ordering the compatibility rule is written in terms of.</summary>
    public bool IsNewerThan(PatternSchemaVersion other) =>
        Major != other.Major ? Major > other.Major : Minor > other.Minor;

    public override string ToString() => $"{Major}.{Minor}";
}
