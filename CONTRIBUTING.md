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

## Security issues

Do not open a public issue for a security vulnerability. See `SECURITY.md` for how to report it
privately.

## Code of conduct

Be respectful and constructive. This is a small hobby project maintained in spare time — please be
patient with review turnaround.
