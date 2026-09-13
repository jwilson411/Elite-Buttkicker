# Pattern pack schema versioning

A pattern pack is a JSON file under `patterns/`. This document is the contract between a pack and
the app that reads it: what a pack says about which schema it targets, which of those the app will
load, and what happens to a pack written against an older one.

The rules here are implemented in
[`PatternSchemaVersion`](../src/EDButtkicker/Services/PatternSchemaVersion.cs) and
[`PatternPackMigrator`](../src/EDButtkicker/Services/PatternPackMigrator.cs), enforced at load time
by `PatternFileService`, and described for authors in
[`patterns/schema.json`](../patterns/schema.json).

## Declaring a version

A pack declares its schema in a top-level `schemaVersion` field:

```json
{
  "schemaVersion": "2.0",
  "metadata": { "name": "My Pack", "version": "1.0.0", "author": "CMDR Someone" },
  "ships": { "sidewinder": { "events": {} } }
}
```

`schemaVersion` is written as `major` or `major.minor`. It is **not** the pack's own version -
that is `metadata.version`, which the author bumps when they change their patterns, and which the
app never interprets. Nor is it `metadata.compatibility`, which records the EDButtkicker build the
pack was written with.

| Field                    | Owned by | Means                                                |
|--------------------------|----------|------------------------------------------------------|
| `schemaVersion`          | the app  | which file format the pack is written in             |
| `metadata.version`       | the author | which revision of *their* patterns this is         |
| `metadata.compatibility` | the app  | the EDButtkicker build that wrote the pack           |

**A pack that omits `schemaVersion` is read as v1.** The field was added after the first packs
shipped, so silence means "the format that existed before the field did" rather than "whatever is
current". Every pack in this repository predates the field and is therefore a v1 pack.

## What loads

The **major** number is the migration boundary. It is bumped only when the shape of a pack changes
in a way an older reader would misread, and every bump ships a migration. The **minor** number
marks additions within a major - new optional fields, wider bounds - which need no migration: a
build reads every minor at or below its own as written. It does not read a *higher* minor, because
a field this build has never heard of is a field it would silently drop rather than honour.

This build reads pattern pack schema **v1 through v2**, and writes **v2**:

| Declared          | Outcome                                                              |
|-------------------|----------------------------------------------------------------------|
| absent            | read as v1, then migrated to v2 in memory                            |
| `1`, `1.0`, `1.4` | migrated to v2 in memory                                             |
| `2`, `2.0`        | read as written                                                      |
| `2.1` and up      | **rejected** - newer than this build reads                           |
| `3.0` and up      | **rejected** - newer than this build reads                           |
| `0.9`             | **rejected** - older than the oldest schema this build can migrate   |
| `latest`, `x.y`   | **rejected** - not a version number                                  |

A rejected pack is refused whole, with a log message naming the file, the version it declared, and
the highest version this build supports. It never reaches playback half-read, and rejecting it
never costs the catalog any other pack: the other files in the directory load normally, and a pack
that was already loaded keeps its last known-good version if a rejected edit lands on top of it.

## Migration never touches your file

When a pack declares an older-but-supported version, the app upgrades the parsed document in
memory, on the way to building the catalog. **The file on disk is not rewritten, moved, or backed
up** - it stays byte for byte as its author wrote it. Migration happens again on every load, which
is what makes it safe to ship a corrected migration in a later release.

Anything the app writes deliberately - an export from `ExportPatternPackAsync`, a save from the
pattern editor - is a separate file at a location the user chose, and is stamped with the current
schema version. Nothing in the load path writes.

## Version history

### v2 (current)

Layer fields use their canonical names: `amplitude` and `startTime`.

### v1

Layer fields were spelled `intensity` and `delay`. The v1 -> v2 migration renames them; a pack that
somehow declares both keeps the v2 spelling's value and drops the v1 one.

Nothing else differs between the two, so a v1 pack that uses no layers migrates to a byte-identical
document apart from the stamped `schemaVersion`.

## Adding a v3

1. Add the migration to `PatternPackMigrator.Migrations` as one `2 -> 3` entry, operating on the
   `JsonObject` before it is deserialized.
2. Bump `PatternSchemaVersion.Current`. Leave `OldestSupported` alone unless a migration is being
   dropped, which is a decision to make deliberately - it stops older community packs from loading.
3. Update `patterns/schema.json` (`$id`, `title`, the `schemaVersion` pattern and description) and
   the tables above.
4. Add a migration test alongside the v1 -> v2 one in `PatternPackVersioningTests`, asserting both
   the migrated in-memory result and that the file on disk is untouched.
