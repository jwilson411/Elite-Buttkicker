# Archived design notes

Historical implementation notes, kept for design history only. **Nothing in this folder describes
current behaviour**: the ports, file paths, console output and setup flows they mention have changed
since they were written, and they were never user-facing setup instructions.

The current first-run flow and the port the web interface listens on are documented in the
repository [README](../../README.md), and the port itself is the single constant
`WebUiConfiguration.Port` in `src/EDButtkicker/Hosting/WebUiConfiguration.cs`.

Because these files are historical, they are excluded from the documentation freshness checks in
`tests/EDButtkicker.Tests/DocumentationFreshnessTests.cs`.
