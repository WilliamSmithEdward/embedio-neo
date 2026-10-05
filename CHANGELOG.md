# Changelog

## [3.5.0] - Unreleased development baseline

This inherited version is a development placeholder. It must not be tagged or
published as a compatible upstream 3.x release. William will select a release
version before publication. The approved NuGet package family is `EmbedIO-Neo`.

- Consolidated EmbedIO, JsonServer and the CLI into one repository.
- Added .NET 10 while retaining .NET Standard 2.0 library targets.
- Removed SWAN with approval; see MIGRATION.md for source and binary changes.
- Ported the CLI with approved plugin migration; see CLI.md.
- Added locked builds and the repository-standard CI, security, malware scan,
  update and release-preview workflows.
- Escaped carriage returns and newlines in trace output to prevent request data
  from forging additional log records; diagnostic observers retain the original data.

The detailed initial baseline is recorded in docs/embed_io_neo.md.
