# UsenetSharp Agent Guide

## Purpose and stack
- UsenetSharp is a .NET 10 library for asynchronous, read-only NNTP access and streaming yEnc decoding.
- The solution contains `UsenetSharp/` (the package) and `UsenetSharpTest/` (NUnit tests).
- `RapidYencSharp` provides yEnc decoding. Release Please versions the package; GitHub Actions builds and publishes releases.

## Architecture
- `Clients/UsenetClient.*.cs` is one partial class split by NNTP command and implements `IUsenetClient`.
- One client owns one TCP/TLS connection. Commands are serialized by `AsyncSemaphore(1)`; use multiple clients for parallel downloads.
- NNTP text uses Latin1 so bytes 0-255 survive body transfer. Preserve NNTP dot-unstuffing and CRLF behavior.
- Successful `BODY` and `ARTICLE` calls return a pipe-backed stream. The command lease remains held until the body reaches its terminator or fails.
- `Streams/YencStream.cs` wraps a body stream and decodes incrementally. Keep hot paths allocation-conscious (`Span`, `Memory`, `ArrayPool`, `ValueTask`).
- Models are immutable response records; exceptions under `Exceptions/` represent connection and protocol failures.

## Required invariants
- Reject CR/LF and control characters in every value interpolated into an NNTP command.
- Respect caller cancellation during lock waits, network I/O, header parsing, and body streaming.
- Never replace or dispose the command semaphore while an operation owns it.
- Reconnect and disposal must not race an active body reader.
- Keep pipe backpressure bounded. A truncated transfer must fail the returned stream and report `NotRetrieved`.
- Bound protocol line and header sizes; treat malformed or prematurely closed responses as protocol/connection failures.
- TLS must use platform certificate validation. Document that credentials sent without TLS are plaintext.

## Development workflow
```bash
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build --filter "TestCategory!=Integration"
dotnet pack UsenetSharp/UsenetSharp.csproj --configuration Release --no-build
dotnet run --configuration Release --project UsenetSharp.Benchmarks
```
- Deterministic tests must use the local scripted NNTP server and require no network or credentials.
- Live-server tests are category `Integration` and read credentials from environment variables; never commit credentials.
- yEnc tests require RapidYencSharp's native library; its package supplies Linux and Windows binaries, while macOS needs a locally built `rapidyenc`.
- Add regression tests for protocol, lifecycle, cancellation, concurrency, and streaming changes.
- Keep nullable analysis enabled and resolve warnings rather than suppressing them without justification.

## Public API and compatibility
- Primary API: `ConnectAsync`, `AuthenticateAsync`, `StatAsync`, `HeadAsync`, `BodyAsync`, `ArticleAsync`, `DateAsync`, and `WaitForReadyAsync`.
- `SegmentId` represents an NNTP message-id. BODY/ARTICLE streams are read-only and non-seekable.
- Avoid silent public API breaks. Isolate compatibility changes, document them, and use semantic versioning.

## Repository and release
- `README.md` is user-facing documentation and is packed into the NuGet package.
- `.github/workflows/ci.yml` is the required pull-request quality gate.
- `release-please-config.json` updates the version in `UsenetSharp/UsenetSharp.csproj`.
- Release artifacts must be built from the release tag only after restore, build, deterministic tests, and package validation succeed.

## Commit convention
- Use scoped Conventional Commits: `feat(scope):`, `fix(scope):`, or `chore(scope):`.
- Choose a concise scope such as `client`, `nntp`, `yenc`, `ci`, `deps`, or `docs`.
- Release Please uses commit types for release notes and versions: `feat` triggers a minor release, `fix` triggers a patch release, and `chore` does not trigger a release.
- Mark breaking changes with `!` (for example, `feat(client)!:`) and include a `BREAKING CHANGE:` footer.
- Keep unrelated changes in separate commits so each release-note entry describes one coherent change.

## Start here
Read `README.md`, `Clients/IUsenetClient.cs`, `Clients/UsenetClient.BodyAsync.cs`,
`Clients/UsenetClient.Helpers.cs`, `Streams/YencStream.cs`, and both project files before changing behavior.
