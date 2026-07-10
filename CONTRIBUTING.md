# Contributing to UsenetSharp

Thank you for contributing. Please keep changes focused and include regression
tests for protocol, cancellation, lifecycle, concurrency, and streaming
behavior.

## Prerequisites

- The .NET SDK selected by `global.json`
- Git

Restore, build, run deterministic tests, and validate the package before
opening a pull request:

```bash
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build --filter "TestCategory!=Integration"
dotnet pack UsenetSharp/UsenetSharp.csproj --configuration Release --no-build
```

Run `dotnet format --verify-no-changes` when changing C# or project files.
Deterministic tests must not require network access or credentials.

Live-server tests belong in the `Integration` category and are not run in CI.
Set `USENETSHARP_TEST_HOST`, `USENETSHARP_TEST_USERNAME`, and
`USENETSHARP_TEST_PASSWORD` when running them. Never commit credentials, local
credential files, access tokens, or captured private data.

## Pull requests

1. Open an issue first for large API or architectural changes.
2. Preserve public API compatibility unless a breaking change is intentional
   and documented.
3. Update documentation when behavior or public APIs change.
4. Add or update tests that fail without the change.
5. Complete the pull request template and ensure all required checks pass.

Use Conventional Commit-style subjects where practical, such as `fix:`,
`feat:`, `docs:`, or `chore:`. Release Please uses commit history to prepare
release notes and determine semantic versions.

## Releases

Release Please maintains a release pull request. Merging that pull request
updates `.release-please-manifest.json` and the `<Version>` in
`UsenetSharp/UsenetSharp.csproj`, then creates an immutable `vX.Y.Z` tag and a
GitHub release. The release event builds, tests, validates, and publishes the
package to NuGet.org. Maintainers should not manually move release tags.
