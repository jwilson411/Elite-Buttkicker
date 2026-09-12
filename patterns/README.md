# Ship Pattern Files

This directory contains JSON files that define haptic patterns for specific ships in Elite Dangerous. The folder structure is for organization only - the system will scan all JSON files regardless of their location in subdirectories.

## File Format

Each JSON file can contain patterns for one or more ships. Files are named `{ShipType}_{PatternSetName}.json` or `{CustomName}.json`.

## Validation

`schema.json` is the canonical description of a pattern pack: it documents the exact shape the
runtime deserializes and the bounds the runtime enforces, and it is the same rule set
`PatternSchemaValidator` applies before a file is indexed or played.

A file that breaks a rule is refused with a diagnostic naming the field and the value
(`ship 'anaconda' event 'HullDamage': frequency is 500Hz, outside the supported 10-100Hz range`),
and the previously loaded version of that file - and every other pack - stays in the catalog. Fix
the file and save it again; the watcher picks it up.

## Example Structure

```
patterns/
├── README.md
├── Small_Ships/
│   ├── Sidewinder_Agile.json
│   ├── Eagle_Combat.json
│   └── Hauler_Transport.json
├── Medium_Ships/
│   ├── Python_Multipurpose.json
│   ├── AspExplorer_LongRange.json
│   └── FerDeLance_Combat.json
├── Large_Ships/
│   ├── Anaconda_Explorer.json
│   ├── Corvette_Combat.json
│   └── Cutter_Luxury.json
├── Community/
│   ├── CreatedBy_CMDR_Example/
│   │   ├── MyCustomPatterns.json
│   │   └── CombatPack.json
│   └── Popular/
│       ├── CinematicExperience.json
│       └── SubtleImmersion.json
└── Custom/
    └── MyPersonalPatterns.json
```

## File Naming Convention

- `{ShipType}_{Style}.json` - Ship-specific patterns (e.g., `Anaconda_Heavy.json`)
- `{Theme}_{Category}.json` - Thematic patterns (e.g., `Cinematic_Combat.json`) 
- `{Author}_{PackName}.json` - Community patterns (e.g., `CMDR_Smith_Explorer.json`)

## Sharing Patterns

1. **Create**: Define your patterns in JSON files
2. **Export**: Use the web interface to package selected patterns
3. **Share**: Upload JSON files to community repositories
4. **Import**: Download and place JSON files in the patterns directory

The system automatically detects new files and makes patterns available for selection.