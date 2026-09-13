# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `CONTRIBUTING.md` with build/test/PR instructions.
- `SECURITY.md` with private vulnerability-reporting guidance and a supported-versions policy.
- Issue templates (bug report, feature request, chore) and a pull request template using the
  repository's RTP-style taxonomy.
- Dependabot configuration for grouped NuGet and GitHub Actions dependency updates.

### Security

- The web interface only ever serves the packaged `wwwroot` directory. Resolution now requires
  concrete evidence of the web root (`index.html`, `css/`, `js/`) and fails startup with a clear
  error if it finds none, instead of falling back to the application's own program directory - which
  would have published the binaries and configuration next to the executable over HTTP.
- Font Awesome 6.4.0 is vendored under `wwwroot/vendor/fontawesome` rather than loaded from
  cdnjs.cloudflare.com without an integrity hash, and the Content-Security-Policy no longer
  allowlists any third-party origin.

### Changed

- A request that names a file but matches nothing in `wwwroot` answers 404 rather than the
  single-page dashboard. Deep links without a file name still load the page.

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

[Unreleased]: https://github.com/jwilson411/Elite-Buttkicker/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/jwilson411/Elite-Buttkicker/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/jwilson411/Elite-Buttkicker/compare/v0.1.0...v1.0.0
[0.1.0]: https://github.com/jwilson411/Elite-Buttkicker/releases/tag/v0.1.0
