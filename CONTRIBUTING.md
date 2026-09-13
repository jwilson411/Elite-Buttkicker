# Contributing to Elite-Buttkicker

Thanks for taking an interest in improving Elite-Buttkicker. This document covers how to build,
test, and submit changes.

## Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Node.js (for the jsdom-based UI tests under `tests/EDButtkicker.Tests/js`)
- Windows is required to actually run the app (WASAPI audio device access), but the solution
  builds and the full test suite runs on Linux/macOS too — CI verifies both.

## Building

```bash
dotnet restore EDButtkicker.sln
dotnet build EDButtkicker.sln
```

## Running

```bash
dotnet run --project src/EDButtkicker
```

The app starts immediately with the system default audio device and an auto-detected Elite
Dangerous journal folder, then serves its interface on `http://localhost:47811` (loopback only)
and opens that address in your browser.

## Testing

Install the Node dependencies used by the UI tests once, then run the full suite:

```bash
npm ci --prefix tests/EDButtkicker.Tests/js
dotnet test EDButtkicker.sln --configuration Release
```

Every pull request must pass:

- `dependency-audit` — no known-vulnerable NuGet packages (`dotnet list package --vulnerable`)
- `build-warnings` — a clean `Release` build with `-warnaserror` (see below)
- `test` — the full `dotnet test` suite, including the Node/jsdom UI tests
- `build-and-package` — publish succeeds for `win-x64`, `win-arm64`, and `win-x86`

See `.github/workflows/ci.yml` for the exact commands CI runs.

### Build warnings are treated as errors

`Directory.Build.props` sets `TreatWarningsAsErrors` for every project in this repository. If you
hit what you believe is a false-positive warning, do not silence it ad hoc — see "Build warnings
are errors" in `README.md` for the documented exception process
(`WarningsNotAsErrors` + a one-line justification comment, called out in your PR).

## Making changes

1. Fork the repository and create a branch off `main`:
   - `feat/description` for new features
   - `fix/description` for bug fixes
   - `chore/description` for maintenance/tooling
   - `docs/description` for documentation-only changes
2. Keep changes focused — one logical change per pull request.
3. Add or update tests for any behavior change. New event mappings, patterns, or API endpoints
   should have corresponding tests under `tests/EDButtkicker.Tests/`.
4. Update `README.md`/`CHANGELOG.md` if you change user-facing behavior, configuration, or
   supported events.
5. Run the full build + test suite locally before opening a PR (see above).

## Pull requests

- Use the pull request template (auto-populated when you open a PR).
- Reference any related issue (`Closes #123` / `Part of #123`).
- Describe what changed and why, and how you tested it (unit tests, manual verification, etc.).
- Note explicitly if any part of the change can only be verified on Windows with real audio
  hardware — that's expected for this project and is not a blocker on its own, but say so.

## Reporting bugs and requesting features

Use the issue templates under **New Issue** on GitHub. Please include:

- Your OS/architecture and the app version (from the release ZIP name or `EDButtkicker.exe --version`
  if available).
- Steps to reproduce, expected behavior, and actual behavior.
- Relevant journal event names/log snippets if the issue is about a specific in-game event
  (redact anything personally identifying from journal files before sharing).

### Attaching a diagnostics bundle

If a maintainer asks for more detail on a hardware, audio, or startup issue, run:

```
EDButtkicker --diagnostics-bundle
```

This builds a single JSON file describing the app version, runtime, sanitized configuration,
enumerated audio endpoints, audio backend state, journal watcher state (event names and counts
only), health indicators, and recent errors. It prints the **entire** file to the console first and
asks `Write this file? Type 'yes' to confirm, anything else to cancel:` — nothing is written until
you confirm. Read the preview before saying yes.

By design, the bundle never contains: raw journal file contents, commander-identifying values
(name, credits, ship IDs, visited systems/stations), full filesystem paths (redacted to
placeholders), your OS account or machine name, or the value of any configuration key the app
doesn't recognize (only the key name is listed, so a future secret in your settings file doesn't
leak). Nothing is uploaded automatically — attach the resulting `.json` file to your GitHub issue
yourself.

To write it somewhere specific instead of the default (your settings folder):

```
EDButtkicker --diagnostics-bundle --diagnostics-bundle-out=/path/to/file-or-folder.json
```

**For maintainers triaging a report:** the `configuration` section shows the audio device name/IDs,
sample rate, and journal path (redacted) actually in effect; `audioBackend` shows whether NAudio
initialized and its last error; `journalWatcher` shows the watcher's state machine and a count of
event types seen (useful for confirming the journal is being read at all without needing the raw
file); `health` mirrors the app's own health check components; `recentErrors` is the tail of logged
exceptions. The `excludedByDesign` array at the end of the bundle is the same list shown in the
preview — it documents what was deliberately left out, not a bug.

## Security issues

Do not open a public issue for a security vulnerability. See `SECURITY.md` for how to report it
privately.

## Code of conduct

Be respectful and constructive. This is a small hobby project maintained in spare time — please be
patient with review turnaround.
