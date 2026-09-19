# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.3.0] - 2026-09-19

### Added

- Unknown condition keys in pattern schemas now trigger a warning at schema-load time and again at
  runtime evaluation, making misconfigured patterns visible in the log without failing silently. (#126)
- Unit tests for `health_below`, `health_above`, `ship_type`, and `hull_damage_above` pattern
  conditions, covering boundary values and type-mismatch handling. (#127)
- Audio intensity slider value is now loaded from persisted user settings on page load and saved
  back on change, so volume preferences survive restarts. (#128)
- Per-pattern enable/disable toggle wired end-to-end: toggling a pattern in the UI immediately
  writes through to the runtime registry and is persisted. (#134)
- Test Haptic button on the Dashboard fires a configurable one-shot pattern so users can verify
  buttkicker hardware without needing a live journal event. (#133)
- `BuildVersion.Current` is now surfaced in the `/api/health` response and populated in the
  settings panel, giving the UI a machine-readable build identifier. (#129)

### Changed

- Advanced Features list in the settings panel is now bound to live runtime feature-flag state
  rather than a static HTML list, so enabling or disabling a feature is immediately reflected. (#132)

### Fixed

- `ShieldsDown` event name normalised to `ShieldDown` (matching the Elite Dangerous journal
  spelling) so shield-loss conditions fire correctly. (#131)
- `EventRateLimiter` lock contention resolved; the limiter is now thread-safe under concurrent
  event ingestion. (#131)
- Reset to Defaults button wired to `POST /api/usersettings/reset`; previously the button was
  present in the UI but made no API call. (#130)
- `time_of_day` pattern conditions now support overnight ranges. A window whose end is before its
  start (e.g. `"22:00-06:00"`) wraps past midnight instead of never matching, and a zero-width
  window (e.g. `"06:00-06:00"`) matches all day. (#125)

## [1.2.0] - 2026-09-16

### Added

- `CONTRIBUTING.md` with build/test/PR instructions.
- `SECURITY.md` with private vulnerability-reporting guidance and a supported-versions policy.
- Issue templates (bug report, feature request, chore) and a pull request template using the
  repository's RTP-style taxonomy.
- Dependabot configuration for grouped NuGet and GitHub Actions dependency updates.
- `--version` CLI flag wired to the assembly version, fulfilling the promise documented in
  `CONTRIBUTING.md`. (#88)
- Privacy-safe diagnostics/support bundle: a single command collects logs, settings, and redacted
  journal snippets without exposing personal data. (#91)
- Community pattern pack versioning and migration: packs now carry a schema version and are
  automatically upgraded on load. (#90)
- `VoiceFeedbackService` wired into the event pipeline so journal events trigger configured voice
  announcements. (#95, #98)
- Voice volume and rate exposed as persisted user settings, configurable from the web UI. (#101)
- `VoiceFeedbackServiceTests` covering `AnnounceAsync`, rate-limiting, and
  `ProcessPatternVoiceFeedback`. (#103, #106)
- `UserSettings` routes added to the DI integration test suite. (#107, #110)
- Keyboard-accessible timeline point and layer editing in the pattern editor. (#72)
- Sample-accurate pattern envelopes and cancellation ramps for smooth effect transitions. (#77)
- Mapped ASP.NET endpoints replacing the manual path-dispatch table. (#75)
- Abstract audio and filesystem boundaries enabling deterministic unit tests without hardware. (#74)

### Changed

- `VoiceFeedbackService` now reads the configured `VoiceMessage` template instead of a hardcoded
  string; stale announcement-table entries removed. (#116)
- README Supported Events table expanded to cover all 51 wired journal events. (#109, #112)
- `EvaluateInCombat` now delegates to `ContextualIntelligenceService` rather than duplicating
  combat logic. (#97, #99)
- Pattern create/update/delete API endpoints now perform and verify their state change before
  responding. (#79)
- Active-effect tracking unified into a single registry entry, eliminating double-counting. (#78)
- Build warnings promoted to errors; all pre-existing warnings resolved. (#83)
- Setup/port information kept consistent across README, web UI, and internal documentation. (#82)
- Pattern files validated against one canonical JSON schema before indexing; malformed packs are
  rejected with a clear error. (#49, #81)
- A request that names a file but matches nothing in `wwwroot` answers 404 rather than the
  single-page dashboard. Deep links without a file name still load the page.
- `PatternFiles/import` endpoint no longer carries the `allowNotImplemented` test carve-out;
  the route is fully covered by the integration test suite. (#111)
- `UserSettings` reset now persists correctly; the reset endpoint returns the right status code. (#107, #110)

### Fixed

- `PlayAudioCue` rewired to use `TaskCompletionSource`/`PlaybackStopped` and honours the
  configured audio device instead of defaulting to the system device. (#104)
- Steady-state audio callback made allocation-free and gain staging corrected to prevent clipping
  at high intensities. (#80)
- `Status.json` `Flags2` edge transitions honoured correctly; monitor loop cancellation is now
  reliable. (#76)
- Pattern file watcher reloads debounced and serialized to prevent duplicate reload events on
  rapid saves. (#69)
- Journal replay now cancels off the request thread and honours event timestamps. (#70)
- Journal watcher rebinds on path change and reports the current read offset. (#64)
- `jsdom` detection in CI checks `lib/api.js` to avoid false-positives on partial installs. (#94)
- Dead `ShouldAnnounceEvent()` method removed from `EventMappingService`. (#100)

### Security

- The web interface only ever serves the packaged `wwwroot` directory. Resolution now requires
  concrete evidence of the web root (`index.html`, `css/`, `js/`) and fails startup with a clear
  error if it finds none, instead of falling back to the application's own program directory — which
  would have published the binaries and configuration next to the executable over HTTP.
- Font Awesome 6.4.0 is vendored under `wwwroot/vendor/fontawesome` rather than loaded from
  cdnjs.cloudflare.com without an integrity hash, and the Content-Security-Policy no longer
  allowlists any third-party origin. (#89)
- API bodies are bounded for size and JSON depth; journal replay memory is capped. (#67)
- Cross-origin mutations on the localhost API are rejected. (#62)
- Exception text and filesystem paths are no longer leaked from API error responses. (#68)

## [1.1.0] - 2026-09-05

### Added

- Windows `win-x64`, `win-arm64`, and `win-x86` self-contained release ZIPs with matching
  `.sha256` checksum files, published via the `release.yml` workflow.

## [1.0.0]

### Added

- Initial stable release: journal monitoring, NAudio-based bass haptic feedback, configurable
  event-to-pattern mapping, and a local web interface for audio device and journal path setup.

## [0.1.0]

### Added

- Initial proof-of-concept release.

[Unreleased]: https://github.com/jwilson411/Elite-Buttkicker/compare/v1.3.0...HEAD
[1.3.0]: https://github.com/jwilson411/Elite-Buttkicker/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/jwilson411/Elite-Buttkicker/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/jwilson411/Elite-Buttkicker/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/jwilson411/Elite-Buttkicker/compare/v0.1.0...v1.0.0
[0.1.0]: https://github.com/jwilson411/Elite-Buttkicker/releases/tag/v0.1.0
