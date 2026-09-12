# Security Policy

## Supported Versions

Elite-Buttkicker is a single-maintainer hobby project. Only the latest published release receives
security fixes; older tags are not backported.

| Version        | Supported          |
| -------------- | ------------------- |
| Latest release (see [Releases](https://github.com/jwilson411/Elite-Buttkicker/releases/latest)) | :white_check_mark: |
| Older releases | :x: |
| `main` (unreleased) | Best effort |

## Reporting a Vulnerability

**Please do not open a public GitHub issue for security vulnerabilities.**

Report privately using one of these channels, in order of preference:

1. **GitHub private vulnerability reporting**: open the
   [Security tab](https://github.com/jwilson411/Elite-Buttkicker/security) on this repository and
   use "Report a vulnerability". This creates a private advisory only the maintainer can see.
2. If private reporting is unavailable to you, open a regular issue that says only "security issue
   — please contact me for details" with no technical specifics, and wait for a response before
   sharing anything further.

When reporting, please include:

- A description of the vulnerability and its potential impact.
- Steps to reproduce, or a proof-of-concept if you have one.
- The affected version/commit.
- Whether the issue is remotely exploitable (relevant here: the app's local web UI binds to
  `localhost` only — see "Scope" below).

## Scope

Elite-Buttkicker's local web interface is intended to bind to loopback (`localhost`) only and is
not designed to be exposed to a network. Reports about:

- Path traversal, XSS, or CSRF against the local web UI/API
- Unsafe deserialization of journal or pattern JSON
- Anything that would let the loopback listener be reached from outside the local machine, or
  broaden it beyond loopback

are all in scope and welcome. Reports asking us to add remote/cloud accounts, expose the listener
beyond loopback, or otherwise widen the network surface are out of scope — see `claude.md` and
open issue acceptance criteria for the loopback-only design decision.

## Response Expectations

This is maintained in spare time. Please allow up to two weeks for an initial response. Confirmed
vulnerabilities will be fixed and disclosed via a GitHub Security Advisory and noted in
`CHANGELOG.md`.
