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
