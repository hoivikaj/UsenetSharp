# NNTP Protocol Compliance & Performance Audit — UsenetSharp

> **Status:** Report only. No code has been changed.
> **Audited:** 2026-07-15
> **Reference:** [protocols-wiki.md](protocols-wiki.md) (RFC 3977, RFC 4643, RFC 4642/8143, RFC 5536/5537, yEnc 1.3), [nntp-pipelining.md](nntp-pipelining.md), [AGENTS.md](AGENTS.md) invariants.
> **Scope:** `UsenetSharp/Clients/*` (connection, AUTHINFO, STAT, HEAD, BODY, ARTICLE, DATE, pipelined decoded bodies), `UsenetSharp/Streams/YencStream.cs`, `UsenetSharp/Models/*`.

---

## 1. Scope and method

Every implemented NNTP operation was traced from command write → status-line parse → payload framing → error/cancellation path, and compared with:

- RFC 3977 §3.1 (command/response line rules), §3.1.1 (multi-line blocks / dot-stuffing), §3.2 (response format & first-digit rule), §3.5 (pipelining, FIFO matching), §5.4 (QUIT), §5.6 / wiki §5.6 (framing fixed per response code), Appendix C (code catalog)
- RFC 4643 (AUTHINFO USER/PASS sequencing, grammar, security considerations)
- RFC 4642 / 8143 (TLS posture)
- yEnc draft 1.3 (keyword lines, CRC expectations) via wiki §14
- Wiki §17.1 audit checklist

**Commands implemented by the library:** greeting, `AUTHINFO USER/PASS`, `STAT`, `HEAD`, `BODY` (single + pipelined batch + decoded), `ARTICLE`, `DATE`, implicit TLS on connect.
**Not implemented (by design, read-only message-id client):** `CAPABILITIES`, `MODE READER`, `GROUP`/`LISTGROUP`/`LAST`/`NEXT`, `OVER`/`HDR`/`LIST`, `NEWNEWS`, `POST`/`IHAVE`, `STARTTLS`, `QUIT`. Only the absence of `QUIT` (N-08) and `CAPABILITIES` (N-10) rises to a finding.

### Verified compliant (no action needed)

| Behavior | Where | Wiki / RFC |
|---|---|---|
| Message-IDs always sent with `<>` added; stored bracket-less | `WriteMessageIdCommandAsync`, `SegmentId` | wiki §4.4, §17.1 |
| CR/LF & control chars rejected in all interpolated command values | `ValidateCommandValue`, `ValidateSegmentId` ([UsenetClient.Helpers.cs](UsenetSharp/Clients/UsenetClient.Helpers.cs#L127-L175)) | RFC 3977 §3.1 (command injection) |
| Dot-unstuffing (`..`→`.`) and lone-`.` terminator in raw BODY/ARTICLE payloads | `ReadBodyToPipeAsync` ([UsenetClient.BodyAsync.cs](UsenetSharp/Clients/UsenetClient.BodyAsync.cs#L625-L642)) | RFC 3977 §3.1.1, wiki §4.5 |
| Decoded path delegates dot-unstuffing to rapidyenc (`isRaw: true`, initial state `RYDEC_STATE_CRLF`, CRLF re-appended per line) | `ReadDecodedBodyToPipeAsync` | wiki §4.5, §14 |
| `430` mid-pipeline treated as per-article soft miss, batch continues | `ProcessDecodedBodyBatchAsync` ([UsenetClient.DecodedBodiesAsync.cs](UsenetSharp/Clients/UsenetClient.DecodedBodiesAsync.cs#L143-L158)) | wiki §12.2, §7.3 |
| Pipelined responses matched strictly FIFO on one connection | `ProcessDecodedBodyBatchAsync` sequential loop | RFC 3977 §3.5 |
| AUTHINFO never pipelined; PASS only after 381; immediate 281 accepted | `AuthenticateAsync` | RFC 4643 §2.3/2.4, wiki §9.1 |
| TLS 1.2+ floor, platform cert & hostname validation, implicit TLS supported | `ConnectAsync` | wiki §10, RFC 8143 |
| Multi-line framing decided from numeric code, not response text | all command handlers | RFC 3977 §3.2, wiki §5.6 |
| Latin1 (byte-transparent) body path; bytes 0–255 preserved | `NntpLineReader`, pipe copy | RFC 5537 §3.2 (octet transparency) |
| DATE reply `111 yyyymmddhhmmss` parsed as UTC | `ParseNntpDateTime` | RFC 3977 §7.1 |
| Bounded header size (256 KiB / 256 fields) and line length (64 KiB) | `ParseArticleHeadersAsync`, `NntpLineReader` | AGENTS invariant |
| Backpressure-bounded pipe (1 MiB pause / 512 KiB resume); truncated transfer fails the stream and reports `NotRetrieved` | body pumps | AGENTS invariant |

---

## 2. Findings summary

| ID | Severity | Type | Title |
|----|----------|------|-------|
| N-01 | **High** | Compliance/Robustness | Command-phase failures leave the session desynchronized but "healthy" |
| N-02 | Medium | Robustness | Truncated final line at EOF is surfaced as a complete line |
| N-03 | Medium | Compliance | Response-code parser accepts non-3-digit forms (`+22`, ` 22`, `-2`…) |
| N-04 | Medium | Compliance/Robustness | Unexpected multi-line success codes leave their payload unread |
| N-05 | Medium | Robustness | HEAD (221) parser exits on blank line without consuming the `.` terminator |
| N-06 | Medium | Compliance/Perf | Pipelined batch: unbounded depth + one TCP write per command |
| N-07 | Medium | Compliance | AUTHINFO arguments can exceed the 512-octet command line; SP allowed in username |
| N-08 | Medium | Compliance | No `QUIT` before closing the connection |
| N-09 | Low | Robustness | Greeting `400` vs `502` not differentiated (retryable vs permanent) |
| N-10 | Low | Compliance | No `CAPABILITIES` (or `MODE READER`) support |
| N-11 | Low | Security | Credentials sent over plaintext connections without guard option |
| N-12 | Low | Compliance | Duplicate article headers silently overwritten |
| N-13 | Low | Compliance | Message-id length limit (497) exceeds the 250-octet interoperability bound |
| N-14 | Low | Robustness | Unbounded skip of non-yEnc junk in decoded body path |
| N-15 | Low | Ergonomics | `UsenetResponseType` misses standard codes (483, 400, 205, 5xx…) |
| N-16 | Low | Robustness/Perf | DATE: malformed `111` payload silently yields `null`; allocating parse |
| N-17 | Low | Compliance | yEnc CRC32 validation off by default; when on, *missing* CRC is fatal |

Performance posture overall: **good** — `ArrayPool` buffers, span-based parsing, single coalesced timeout timer per body (`CoalescedReadTimeout`), pooled command buffers, and pipe backpressure are all in place. The only material perf wins found are in N-06 (write coalescing) and the strict code parser in N-03 (cheaper than `int.TryParse`).

---

## 3. Detailed findings

---

### N-01 (High) — Command-phase failures leave the session desynchronized but "healthy"

**Problem statement.**
RFC 3977 §3.5 requires responses to be matched to commands strictly in FIFO order. Any failure that occurs *after command bytes may have been written* and *before the response is fully consumed* means the connection is no longer at a command boundary: a stale response (or partial command) is still in flight. UsenetSharp only records this poisoned state for failures inside the *body pump* (`RecordBackgroundFailure`) and for batch command-write failures. For every single-line command phase — `STAT`, `HEAD`, `DATE`, `AUTHINFO`, and the command/status/header phase of `BODY`/`ARTICLE`/`DecodedBodyAsync` — a caller cancellation, `TimeoutException`, `UsenetProtocolException` (bad code line, header limits), or `IOException` propagates to the caller, the command lock is released, `_backgroundException` stays `null`, and `IsHealthy` remains `true`. The next command on the same client then reads the previous command's response → silent protocol desync, potentially returning the *wrong article's* status or body.

Concrete scenarios:

1. `StatAsync` write succeeds, `ReadLineAsync` hits the 10 s `ReadTimeout` → `TimeoutException`. Caller retries `StatAsync` on the same client → reads the first STAT's late `223` as the answer to the second (different) segment.
2. Caller cancels `HeadAsync` while headers are streaming → remaining header lines + `.` terminator stay buffered → next command parses a header line as a status line.
3. `ParseArticleHeadersAsync` throws its 256 KiB / 256-count limit mid-`ARTICLE` → same as (2) plus unread body.
4. A write timeout in `WriteMessageIdCommandAsync` may leave a *partial* command on an `SslStream` (cancelled TLS writes also corrupt the `SslStream` itself) → next write splices into the previous command.

**Evidence.**
- Timeout wrappers throw without any state poisoning: [UsenetClient.Helpers.cs](UsenetSharp/Clients/UsenetClient.Helpers.cs#L183-L276) (`WriteLineAsync`, `WritePooledCommandAsync`, `WriteCommandAsync`, `ReadLineAsync`).
- `StatAsync` finally block only releases the lock: [UsenetClient.StatAsync.cs](UsenetSharp/Clients/UsenetClient.StatAsync.cs#L30-L36).
- `HeadAsync` / `ArticleAsync` header parse failures propagate with no poisoning: [UsenetClient.HeadAsync.cs](UsenetSharp/Clients/UsenetClient.HeadAsync.cs#L27-L52), [UsenetClient.ArticleAsync.cs](UsenetSharp/Clients/UsenetClient.ArticleAsync.cs#L88-L97).
- Only the pumps and the batch write loop record failures: [UsenetClient.BodyAsync.cs](UsenetSharp/Clients/UsenetClient.BodyAsync.cs#L400-L416) and [UsenetClient.DecodedBodiesAsync.cs](UsenetSharp/Clients/UsenetClient.DecodedBodiesAsync.cs#L94-L101) — the latter is the correct pattern, applied in only one place.
- Gate that would protect reuse: `ThrowIfUnhealthy` ([UsenetClient.Helpers.cs](UsenetSharp/Clients/UsenetClient.Helpers.cs#L86-L102)) checks only `_backgroundException`.

**Resolution proposal.**

1. Introduce a single helper that owns the "poison on mid-command failure" rule (rename `RecordBackgroundFailure` → `RecordConnectionFailure` since it is no longer background-only).
2. Route every command through it: failure any time between first command byte and last response byte ⇒ record failure. Validation failures before writing remain non-poisoning.
3. Keep the existing `ThrowIfUnhealthy` gate — it already blocks reuse once the state is recorded. `IsHealthy` then correctly reports `false` so pool owners reconnect.

```csharp
// UsenetClient.Helpers.cs
private async ValueTask<(int Code, string Line)> ExchangeSingleLineAsync(
    Func<CancellationToken, ValueTask> writeCommand,
    CancellationToken token)
{
    try
    {
        await writeCommand(token).ConfigureAwait(false);
        var line = await ReadLineAsync(token).ConfigureAwait(false)
            ?? throw new UsenetProtocolException(
                "The NNTP connection closed before a response was received.");
        return (ParseResponseCode(line), line);
    }
    catch (Exception e)
    {
        // Once bytes may be on the wire the response FIFO cannot be trusted
        // (RFC 3977 §3.5). Poison the session so the next command is rejected
        // by ThrowIfUnhealthy instead of reading a stale response.
        RecordConnectionFailure(e);
        throw;
    }
}
```

Call sites become, e.g. in `StatAsync`:

```csharp
var (responseCode, response) = await ExchangeSingleLineAsync(
    ct => WriteMessageIdCommandAsync("STAT", segmentId, ct),
    operationCts.Token).ConfigureAwait(false);
```

For `HeadAsync`/`ArticleAsync`, additionally wrap the header-parse phase:

```csharp
try
{
    headers = await ParseArticleHeadersAsync(operationCts.Token, allowDotTerminator: true)
        .ConfigureAwait(false);
}
catch (Exception e)
{
    RecordConnectionFailure(e);   // unread header/body lines remain buffered
    throw;
}
```

`AuthenticateAsync` uses the same helper for both AUTHINFO exchanges. `BodyAsync`/`DecodedBodyAsync`/`DecodedBodiesAsync` use it for the command+status phase (pump paths already poison correctly).

Performance impact: zero on the happy path (one extra try/catch frame; no allocation).

**Test plan** (deterministic scripted server, `UsenetSharpTest/Support`):
- Script a server that delays the STAT response beyond `ReadTimeout`; assert `TimeoutException`, then `IsHealthy == false` and the next `StatAsync` throws `UsenetProtocolException` (unhealthy) rather than reading the stale line.
- Cancel `HeadAsync` mid-headers (server sends half the header block, then pauses); assert poisoning.
- Script an oversized header block (> 256 KiB) for `ARTICLE`; assert failure + poisoning.
- Regression: a *pre-write* validation failure (bad `SegmentId`) must NOT poison; connection stays healthy.
- Regression: successful commands after a clean `430` keep `IsHealthy == true`.

---

### N-02 (Medium) — Truncated final line at EOF is surfaced as a complete line

**Problem statement.**
RFC 3977 §3.1 terminates every response line with CRLF. When the TCP stream ends mid-line, `NntpLineReader.ReadLineBytesAsync` returns the buffered partial bytes *as if they were a complete line*, and only the *next* call reports EOF. A status line truncated at e.g. `"223 0 <id"` (or worse, at exactly `"223"`) parses as a valid success; a truncated body data line is passed downstream as complete before the terminator-missing error fires. The result is masked truncation and misleading success signals in the window before the follow-up read.

**Evidence.**
[NntpLineReader.cs](UsenetSharp/Clients/NntpLineReader.cs#L36-L47) — on `bytesRead == 0` with `_lineBufferLength > 0`, the partial line is returned via `TrimCarriageReturn` instead of being treated as a protocol error.

**Resolution proposal.**
A line without a terminator at EOF is a truncated transfer; fail fast:

```csharp
if (_length == 0)
{
    if (_lineBufferLength == 0)
    {
        return null;                       // clean EOF at a line boundary
    }

    _lineBufferLength = 0;
    throw new UsenetProtocolException(
        "The NNTP stream ended with an unterminated line.");
}
```

Notes:
- `YencStream` has its own line reader over the *decoded pipe*; `BodyAsync` always re-appends CRLF, so no legitimate partial-line case exists there — the change is confined to protocol reads.
- Combined with N-01, the thrown exception also poisons the session (it is already dead: EOF).

**Test plan.**
- Scripted server sends `"222 0 <id>\r\nBODY-DATA"` then closes without CRLF/terminator → the pump must fail the stream with `UsenetProtocolException` (already asserted today) *and* no partial "BODY-DATA" line may be emitted as complete before the failure.
- Server closes after sending `"22"` → `UsenetProtocolException`, not a parsed code.
- Clean close directly after a full line still yields `null` (existing behavior preserved).

---

### N-03 (Medium) — Response-code parser accepts non-3-digit forms

**Problem statement.**
RFC 3977 §3.2: a response line starts with a *three-digit* code, first digit 1–5, followed by space (or end). `ParseResponseCode` slices 3 chars and uses `int.TryParse`, whose default `NumberStyles.Integer` accepts leading/trailing whitespace and a leading sign. Malformed lines therefore parse "successfully" to wrong codes:

| Wire line | Parsed as | Correct behavior |
|---|---|---|
| `+22 hello` | `22` | protocol error |
| ` 22 hello` | `22` | protocol error |
| `22 hello` (2-digit code) | `22` (`"22 "`) | protocol error |
| `999 hello` | `999` | reject or first-digit-rule handling (§3.2 only defines 1xx–5xx) |

Combined with N-02 (truncated lines) this widens the window for misinterpreting garbage as a status.

**Evidence.**
[UsenetClient.Helpers.cs](UsenetSharp/Clients/UsenetClient.Helpers.cs#L57-L71).

**Resolution proposal.**
Strict, branch-cheap digit arithmetic (also faster than `int.TryParse` — no culture/style machinery on the hot path):

```csharp
private static int ParseResponseCode(ReadOnlySpan<char> response)
{
    if (response.Length < 3 ||
        response[0] is < '1' or > '5' ||
        !char.IsAsciiDigit(response[1]) ||
        !char.IsAsciiDigit(response[2]) ||
        (response.Length > 3 && response[3] != ' '))
    {
        throw new UsenetProtocolException($"Invalid NNTP response: {response}");
    }

    return (response[0] - '0') * 100 + (response[1] - '0') * 10 + (response[2] - '0');
}
```

(Overload keeping the current `string?` entry point; null/short handling unchanged. Per N-01, a throw here poisons the session — correct, because an unparseable line means unknown framing.)

**Test plan.**
- Unit tests for each malformed form above → `UsenetProtocolException`.
- `"430 no such article"` and bare `"205"` still parse.
- Property check: all `UsenetResponseType` values round-trip.

---

### N-04 (Medium) — Unexpected multi-line success codes leave their payload unread

**Problem statement.**
RFC 3977 §3.2 / wiki §5.6: whether a response is multi-line is *fixed per response code*. The handlers compare against exactly one expected code (`222` for BODY, `221` for HEAD, `220` for ARTICLE) and return every other code as a single-line failure result. If a server replies with a *different multi-line* code (e.g. a nonconforming server answers `BODY` with `220`, or emits `101`/`215` after a state hiccup), the client returns the status to the caller but leaves the entire multi-line payload buffered → the next command desyncs. The wiki's first-digit rule (§5.4) says unexpected `2xx` = success, but acting on that safely requires consuming the known-multi-line payload.

**Evidence.**
- [UsenetClient.BodyAsync.cs](UsenetSharp/Clients/UsenetClient.BodyAsync.cs#L57-L95) — any non-222 code returns immediately with `Stream = null`; nothing drains a multi-line payload.
- Same pattern in `HeadAsync`, `ArticleAsync`, `DecodedBodyAsync`, and per-response handling in `ProcessDecodedBodyBatchAsync`.

**Resolution proposal.**
On the unexpected-code path only (zero cost on happy path), consult the RFC 3977 Appendix C multi-line set and drain before returning:

```csharp
// Response codes that are always followed by a multi-line data block
// (RFC 3977 Appendix C; 211 is only multi-line for LISTGROUP, which this
// client never issues, so it is intentionally excluded).
private static bool IsMultiLineCode(int code) => code is
    100 or 101 or 215 or 220 or 221 or 222 or 224 or 225 or 230 or 231;

private async ValueTask DrainUnexpectedMultiLineAsync(int code, CancellationToken ct)
{
    if (!IsMultiLineCode(code))
    {
        return;
    }

    // Reuse the bounded drain machinery (AbandonedBodyDrainLimit) so a
    // hostile payload cannot pin the connection.
    var drainFailure = await TryDrainBodyAsync().ConfigureAwait(false);
    if (drainFailure != null)
    {
        RecordConnectionFailure(drainFailure);
    }
}
```

Call after `ParseResponseCode` whenever the code is not the expected one for the command (including `ProcessDecodedBodyBatchAsync` and `TryDrainPipelinedBodiesAsync`, which currently only drains on exactly `222`).

**Test plan.**
- Scripted server answers `BODY` with `220` + article + `.` → client returns failure response (or success-by-first-digit if you choose to surface it), and a follow-up `DATE` on the same connection still works (payload was drained).
- Scripted server answers `STAT` with `100` + help text + `.` → same assertion.
- Drain overflow (> `AbandonedBodyDrainLimit`) → poisoned connection, `IsHealthy == false`.

---

### N-05 (Medium) — HEAD (221) parser exits on blank line without consuming the `.` terminator

**Problem statement.**
A `221` payload is `headers … CRLF . CRLF` with **no blank line** (RFC 3977 §6.2.2). `ParseArticleHeadersAsync` is shared between ARTICLE (terminated by blank line) and HEAD (`allowDotTerminator: true`), and its blank-line branch runs for both modes. A malformed/hostile HEAD payload containing an empty line makes the parser `break` early: the remaining header lines and the `.` terminator stay buffered → guaranteed desync of the next command on this connection.

**Evidence.**
[UsenetClient.ArticleAsync.cs](UsenetSharp/Clients/UsenetClient.ArticleAsync.cs#L118-L145) — the `line == "."` branch honors `allowDotTerminator`, but the following `string.IsNullOrEmpty(line)` branch unconditionally breaks.

**Resolution proposal.**
In dot-terminated mode, a blank line is data corruption, not a terminator. Two options; prefer (a) for robustness:

```csharp
if (string.IsNullOrEmpty(line))
{
    if (allowDotTerminator)
    {
        // (a) Tolerant: malformed blank line inside a 221 block. Skip until
        // the real terminator so the connection stays at a command boundary.
        while (await ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } extra
               && extra != ".")
        {
            // bound with the same 256 KiB accounting as regular headers
        }
        break;
    }

    // ARTICLE mode: blank line legitimately ends the header block.
    if (currentHeaderName != null) { headers[currentHeaderName] = ...; }
    break;
}
```

(Option (b): throw `UsenetProtocolException` + poison per N-01. Simpler, but turns tolerable corruption into a lost connection.) Keep the byte-count guard while skipping so the drain is bounded.

**Test plan.**
- Scripted `221` payload: `Header-A: 1`, ``, `Header-B: 2`, `.` → HEAD returns parsed headers (A at minimum), and a following `STAT` succeeds (terminator consumed).
- Existing HEAD tests unchanged (no blank line case).
- Skip-bound exceeded → protocol exception + unhealthy.

---

### N-06 (Medium) — Pipelined batch: unbounded depth + one TCP write per command

**Problem statement.**
Two related issues in `DecodedBodiesAsync`:

1. **Deadlock exposure (compliance caution).** RFC 3977 §3.5 / wiki §12 warn that pipelining clients must respect the TCP window (~4 KiB caution): the client writes *all* commands before reading any response. With `NoDelay = true` a large batch whose command bytes exceed the window — while the server has already begun streaming responses that the client is not yet reading — can mutually block until `ReadTimeout`. A `BODY <msgid>` line is ~30–260 octets, so batches in the hundreds are unsafe. The library imposes no cap (the nzbdav consumer caps depth at 64, but the API contract does not).
2. **Performance.** Each command is a separate `Stream.WriteAsync` with `NoDelay`, i.e. one TCP segment (and for TLS, one record) per command — for a 64-deep batch that is 64 small packets plus 64 rent/return cycles where 1 coalesced write would do.

**Evidence.**
- Write loop: [UsenetClient.DecodedBodiesAsync.cs](UsenetSharp/Clients/UsenetClient.DecodedBodiesAsync.cs#L70-L78).
- `NoDelay = true`: [UsenetClient.ConnectAsync.cs](UsenetSharp/Clients/UsenetClient.ConnectAsync.cs#L26-L29).
- Response pump starts only after all writes: [UsenetClient.DecodedBodiesAsync.cs](UsenetSharp/Clients/UsenetClient.DecodedBodiesAsync.cs#L87-L94).

**Resolution proposal.**

1. Add `UsenetClientOptions.MaxPipelineDepth` (default 64, matching the proven consumer bound) and validate in `DecodedBodiesAsync`:

```csharp
if (segments.Length > _options.MaxPipelineDepth)
{
    throw new ArgumentException(
        $"Batch exceeds MaxPipelineDepth ({_options.MaxPipelineDepth}); " +
        "split into smaller batches to avoid TCP-window pipeline deadlock (RFC 3977 §3.5).",
        nameof(segmentIds));
}
```

2. Coalesce all command bytes into one pooled buffer and issue a single write:

```csharp
var totalLength = 0;
foreach (var segmentId in segments)
{
    totalLength += 4 /*BODY*/ + 1 + 1 + segmentId.Value.Length + 1 + 2;
}

var buffer = ArrayPool<byte>.Shared.Rent(totalLength);
try
{
    var written = 0;
    foreach (var segmentId in segments)
    {
        written += FormatBodyCommand(buffer.AsSpan(written), segmentId); // "BODY <id>\r\n"
    }

    await WriteCommandAsync(buffer.AsMemory(0, written), operationCts.Token)
        .ConfigureAwait(false);
}
finally
{
    ArrayPool<byte>.Shared.Return(buffer);
}
```

One syscall, one TLS record (chunked internally if large), no per-command CTS. 64 × ~60 B ≈ 3.8 KiB — conveniently inside the 4 KiB caution window, which reinforces 64 as the default cap.

**Test plan.**
- Deterministic test: batch of N segments produces exactly one write on the scripted server transport (expose write-call counting in the fake stream) and responses still resolve in order.
- `MaxPipelineDepth + 1` segments → `ArgumentException` before any bytes written; connection stays healthy.
- Benchmark (`UsenetSharp.Benchmarks/PipelineBenchmarks.cs`): compare per-command writes vs coalesced write at depth 8/32/64.

---

### N-07 (Medium) — AUTHINFO arguments can exceed the 512-octet command line; SP allowed in username

**Problem statement.**
- RFC 3977 §3.1: a command line is at most 512 octets *including CRLF* (497 applies to the *argument portion* of the shortest commands). `AuthenticateAsync` validates `user`/`pass` at ≤ 497 chars, but `"AUTHINFO USER "` is 14 octets, so a 497-char value yields a 513-octet line — off-by-one over the limit (a strict server replies `501`).
- RFC 4643 §2.4 grammar: `username = 1*P-CHAR`, `password = 1*P-CHAR`, where `P-CHAR` starts at `%x21` — **SP is not permitted** in either. `ValidateCommandValue` rejects control chars only, so a username containing a space produces `AUTHINFO USER john smith`, which a server parses as garbage trailing arguments.

**Evidence.**
[UsenetClient.AuthenticateAsync.cs](UsenetSharp/Clients/UsenetClient.AuthenticateAsync.cs#L10-L11) (497 limits), [UsenetClient.Helpers.cs](UsenetSharp/Clients/UsenetClient.Helpers.cs#L139-L155) (control-chars-only check).

**Resolution proposal.**

```csharp
// "AUTHINFO USER " / "AUTHINFO PASS " = 14 octets; 512 - 14 - 2 (CRLF) = 496.
private const int MaxAuthInfoArgumentLength = 496;

ValidateCommandValue(user, nameof(user), MaxAuthInfoArgumentLength);
ValidateCommandValue(pass, nameof(pass), MaxAuthInfoArgumentLength);

if (ContainsWhitespace(user))
{
    throw new ArgumentException(
        "Username must not contain whitespace (RFC 4643 §2.4).", nameof(user));
}
```

For `pass`: RFC-strict would reject SP too, but real-world passwords with spaces exist and many servers accept them (the password is the last argument). Recommended: allow SP in `pass`, document the deviation in XML docs, and optionally add a strict-mode option later. Rejecting SP in `user` has no interop downside — it can never work.

**Test plan.**
- 497-char user/pass → `ArgumentException`; 496-char accepted and wire line measured ≤ 512 octets in scripted-server test.
- `"john smith"` username → `ArgumentException`; password with space still succeeds against scripted server.

---

### N-08 (Medium) — No `QUIT` before closing the connection

**Problem statement.**
RFC 3977 §5.4: `QUIT` → `205` is the specified way to end an NNTP session ("the client SHOULD use QUIT"; abrupt closes are tolerated but non-preferred). `Dispose`/`DisposeAsync` cancel and close the TCP stream directly. Some providers log/rate abrupt disconnects, and TLS close alerts without QUIT can appear as errors server-side. There is also no public API for a caller to end the session politely.

**Evidence.**
[UsenetClient.cs](UsenetSharp/Clients/UsenetClient.cs#L76-L106) — both dispose paths go straight to `CleanupConnection`; no `QUIT` anywhere in the codebase (`grep QUIT` → no matches).

**Resolution proposal.**

1. Add `QuitAsync` to `IUsenetClient` (with a default interface implementation throwing `NotSupportedException` to avoid breaking external implementors, mirroring the `DecodedBodiesAsync` pattern):

```csharp
private static readonly byte[] QuitCommand = "QUIT\r\n"u8.ToArray();

public async Task<UsenetResponse> QuitAsync(CancellationToken cancellationToken)
{
    ThrowIfDisposed();
    await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        ThrowIfDisposed();
        ThrowIfUnhealthy();
        ThrowIfNotConnected();
        using var operationCts = CreateOperationTokenSource(cancellationToken);
        var (code, line) = await ExchangeSingleLineAsync(
            ct => WriteCommandAsync(QuitCommand, ct), operationCts.Token)
            .ConfigureAwait(false);          // expect 205
        CleanupConnection();                  // server closes after 205
        return new UsenetResponse { ResponseCode = code, ResponseMessage = line };
    }
    finally
    {
        _commandLock.Release();
    }
}
```

2. Optionally, in `DisposeAsync` attempt a best-effort QUIT with a short cap (e.g. 250 ms) *only* when the command lock is acquired without waiting and the connection is healthy; swallow all failures. Keep synchronous `Dispose` as-is (no sync-over-async network I/O).

**Test plan.**
- Scripted server: `QUIT` → `205` → close; assert response surfaced, `IsConnected == false`.
- `QuitAsync` while a body lease is held → waits for the lease (serialized by the command lock) — reuse the thread-safety test harness.
- Dispose-path QUIT (if implemented): scripted server asserts it received `QUIT` before FIN; a hung server must not delay disposal beyond the cap.

---

### N-09 (Low) — Greeting `400` vs `502` not differentiated

**Problem statement.**
Wiki §7.3 / RFC 3977 §5.1: greeting `400` = temporarily unavailable (client should back off and retry), `502` = permanently unavailable (do not hammer). `ConnectAsync` throws the same `UsenetConnectionException` for both; pool owners cannot implement compliant backoff policy without string-sniffing.

**Evidence.**
[UsenetClient.ConnectAsync.cs](UsenetSharp/Clients/UsenetClient.ConnectAsync.cs#L57-L65) — `ResponseCode` is set on the exception, but no retryability signal is defined.

**Resolution proposal.**
Additive property on `UsenetConnectionException`:

```csharp
/// <summary>True when the server indicated a temporary condition (greeting 400).</summary>
public bool IsTransient => ResponseCode == 400;
```

Document that `502` (and RFC 4643 `481` on auth) should not be retried aggressively.

**Test plan.** Scripted greetings `400` / `502` → exception with `IsTransient` `true`/`false` respectively; `200`/`201` connect fine (existing tests).

---

### N-10 (Low) — No `CAPABILITIES` (or `MODE READER`) support

**Problem statement.**
Wiki §17.1 checklist: "Client issues `CAPABILITIES` after connect and after TLS." RFC 3977 §5.2 makes `CAPABILITIES` the discovery mechanism (mandatory server-side); mode-switching servers additionally advertise `MODE-READER`, requiring `MODE READER` before reading commands. UsenetSharp sends reading commands blind. Commercial binary providers accept this, but against a transit-facing or mode-switching server the client will receive `401`/`480`/`500`-class responses it cannot anticipate or explain.

**Evidence.** No `CAPABILITIES`/`MODE` strings anywhere in `UsenetSharp/Clients/`.

**Resolution proposal (optional, additive).**
`CapabilitiesAsync` returning the raw label list (multi-line `101` payload, dot-terminated — the existing bounded line reader is sufficient):

```csharp
public async Task<UsenetCapabilitiesResponse> CapabilitiesAsync(CancellationToken ct)
{
    // write "CAPABILITIES\r\n"; expect 101; read dot-terminated lines
    // (bounded, e.g. 64 KiB total); first line MUST be "VERSION 2".
    // Return labels as IReadOnlyList<string>; callers do feature detection.
}
```

Keep it out of the connect path (no behavior change, no extra RTT for existing users); document that per RFC 8143 implicit TLS + this client's fixed command set makes CAPABILITIES optional in practice. `MODE READER` support can be a separate opt-in (`ModeReaderAsync`, expect `200`/`201`, MUST NOT pipeline — trivially satisfied by the command lock).

**Test plan.** Scripted `101` payload with `VERSION 2`, `READER`, `.`; assert parsing, dot-unstuffing of a stuffed capability line, and that an unknown label round-trips unfiltered (RFC 3977 §5.2: clients MUST ignore unknown labels — i.e., expose, don't reject).

---

### N-11 (Low) — Credentials sent over plaintext connections without a guard option

**Problem statement.**
RFC 4643 security considerations / wiki §9.3, §10.3: sending `AUTHINFO` before encryption fails a security audit; the compliant paths are implicit TLS (563) or STARTTLS-then-auth. `AuthenticateAsync` works identically on plaintext connections with no opt-in, warning, or documentation hook at the call site. [AGENTS.md](AGENTS.md) requires this to at least be documented.

**Evidence.**
[UsenetClient.AuthenticateAsync.cs](UsenetSharp/Clients/UsenetClient.AuthenticateAsync.cs#L8-L47) — no TLS check; `_stream is SslStream` is knowable at this point.

**Resolution proposal.**
Additive option, secure-by-explicit-choice without breaking plaintext test rigs:

```csharp
// UsenetClientOptions
/// <summary>
/// When true (default), AuthenticateAsync throws if the connection is not TLS,
/// preventing accidental plaintext credential disclosure (RFC 4643 §4).
/// Set to false only for test servers or networks where plaintext is accepted.
/// </summary>
public bool RequireTlsForAuthentication { get; init; } = false; // flip to true in next major

// AuthenticateAsync, after ThrowIfNotConnected():
if (_options.RequireTlsForAuthentication && _stream is not SslStream)
{
    throw new InvalidOperationException(
        "Refusing to send credentials over a plaintext connection. " +
        "Connect with useSsl: true or disable RequireTlsForAuthentication.");
}
```

Plus an explicit XML-doc warning on `AuthenticateAsync` (the AGENTS.md-mandated plaintext disclosure).

**Test plan.** Option on + plaintext scripted server → `InvalidOperationException` before any bytes written (server sees nothing); option off → current behavior; TLS path unaffected (integration category).

---

### N-12 (Low) — Duplicate article headers silently overwritten

**Problem statement.**
`ParseArticleHeadersAsync` stores headers in `Dictionary<string,string>` (ordinal-ignore-case) — a repeated field name overwrites the previous value, keeping only the *last*. RFC 5536 mandates exactly-one for the mandatory six, but other fields (e.g. `Received`, `Comments`, `X-*`) may legitimately repeat; silently dropping values corrupts header data for HEAD/ARTICLE consumers.

**Evidence.**
[UsenetClient.ArticleAsync.cs](UsenetSharp/Clients/UsenetClient.ArticleAsync.cs#L102) (`Dictionary`), overwriting assignments at [UsenetClient.ArticleAsync.cs](UsenetSharp/Clients/UsenetClient.ArticleAsync.cs#L124), [UsenetClient.ArticleAsync.cs](UsenetSharp/Clients/UsenetClient.ArticleAsync.cs#L139), and [UsenetClient.ArticleAsync.cs](UsenetSharp/Clients/UsenetClient.ArticleAsync.cs#L166).

**Resolution proposal.**
Additive, non-breaking: keep the dictionary (first-wins is less surprising than last-wins — flip assignment to `TryAdd`) and add an ordered multi-value view:

```csharp
public sealed record UsenetArticleHeader
{
    public required Dictionary<string, string> Headers { get; init; }

    /// <summary>All header fields in wire order, including duplicates.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> AllHeaders { get; init; } = [];
}
```

Populate both in one pass (one extra `List` per HEAD/ARTICLE call — cold path, negligible).

**Test plan.** Scripted HEAD payload with duplicate `X-Trace` headers → `AllHeaders` preserves both in order; `Headers["X-Trace"]` deterministic (documented first-wins); existing tests keep passing.

---

### N-13 (Low) — Message-id length limit exceeds the 250-octet interoperability bound

**Problem statement.**
RFC 3977 §3.6 / RFC 5536 §3.1.3: message-ids MUST be ≤ 250 octets *including* `<>`. `ValidateSegmentId` allows 497 chars, so the client will happily send a 499-octet msgid that no conformant peer generated and strict servers answer with `501`. Leniency on send is defensible (garbage in NZBs exists), but the current bound exceeds even what the command line can justify for `ARTICLE`.

**Evidence.**
[UsenetClient.Helpers.cs](UsenetSharp/Clients/UsenetClient.Helpers.cs#L127-L137) (`ValidateCommandValue(value, nameof(segmentId), 497)`).

**Resolution proposal.**
Tighten to the interop bound while staying NZB-tolerant: cap at 248 (250 minus the `<>` the client adds). This remains below every command-line limit (`ARTICLE ` + 250 + CRLF = 260 ≤ 512).

```csharp
ValidateCommandValue(value, nameof(segmentId), 248); // 250 incl. <> (RFC 5536 §3.1.3)
```

Document as a (technically) breaking validation change; schedule with a minor/major bump per repo convention.

**Test plan.** 248-char id accepted; 249 rejected; existing round-trip tests unaffected (real ids are ≪ 250).

---

### N-14 (Low) — Unbounded skip of non-yEnc junk in decoded body path

**Problem statement.**
In `ReadDecodedBodyToPipeAsync`, lines before `=ybegin` and lines after `=yend` (before the `.` terminator) are skipped with no cumulative byte cap — only the per-read `CoalescedReadTimeout` applies. A hostile or broken server can keep a connection (and its command lease) pinned indefinitely by streaming endless junk lines, each arriving within the timeout. All *abandoned*-body paths are capped by `AbandonedBodyDrainLimit`; the *active* decode path's skip states are not. (`BodyAsync`'s raw path streams everything to the consumer, so backpressure bounds it — only the decoded path silently discards.)

**Evidence.**
- Post-`=yend` skip: [UsenetClient.BodyAsync.cs](UsenetSharp/Clients/UsenetClient.BodyAsync.cs#L291-L294) (`if (dataEnded) continue;` — no accounting).
- Pre-`=ybegin` skip: [UsenetClient.BodyAsync.cs](UsenetSharp/Clients/UsenetClient.BodyAsync.cs#L296-L311) (`continue` without accounting).

**Resolution proposal.**
Reuse the existing option as the cap for discarded (not consumer-delivered) bytes:

```csharp
if (dataEnded || (!headersRead && ybeginBuffer == null))
{
    skippedBytes += lineBytes.Length + 2;
    if (skippedBytes > _options.AbandonedBodyDrainLimit)
    {
        throw new UsenetProtocolException(
            "The NNTP body contained more non-yEnc data than the configured drain limit.");
    }
    continue;
}
```

**Test plan.** Scripted body: 2 MiB of junk lines before `=ybegin` with default 1 MiB limit → stream fails with `UsenetProtocolException`, connection poisoned; small leading/trailing junk (e.g. blank line, signature) continues to decode fine (regression: existing yEnc tests).

---

### N-15 (Low) — `UsenetResponseType` misses standard codes

**Problem statement.**
The enum omits codes the wiki catalog marks as directly relevant to this client: `205` (QUIT ack — needed by N-08), `400` (service ending → backoff), `483` (TLS required — sibling of the present 480/481/482), `500`/`501`/`502-command`/`503` (generic failures), `582?` n/a, `380?` n/a. Callers comparing `ResponseCode` against the enum must hard-code ints for these.

**Evidence.** [UsenetResponseType.cs](UsenetSharp/Models/UsenetResponseType.cs) — 21 members; no 205/400/483/500/501/503.

**Resolution proposal.** Additive enum members (no behavior change):

```csharp
ConnectionClosing = 205,
ServiceDiscontinued = 400,
EncryptionRequired = 483,
CommandNotRecognized = 500,
CommandSyntaxError = 501,
FeatureNotSupported = 503,
```

**Test plan.** Compile-time only; add a doc test asserting enum values match RFC Appendix C numbers.

---

### N-16 (Low) — DATE: malformed `111` payload silently yields `null`; allocating parse

**Problem statement.**
`ParseNntpDateTime` returns `null` for a malformed `111` payload, so `UsenetDateResponse` can carry `ResponseCode = 111` (success) with `DateTime = null` — callers cannot distinguish "server violated §7.1" from "non-111 response" without re-parsing. The implementation also allocates (`string.Split`, six `int.Parse` with default styles accepting signs) and swallows all errors via `catch { }`.

**Evidence.** [UsenetClient.DateAsync.cs](UsenetSharp/Clients/UsenetClient.DateAsync.cs#L38-L82).

**Resolution proposal.**
Span-based strict parse; on `111` with an unparseable payload, throw `UsenetProtocolException` (server sent a malformed success — trustworthiness of the session is questionable, and per N-01 machinery the caller learns immediately):

```csharp
private static DateTimeOffset ParseNntpDateTime(ReadOnlySpan<char> response)
{
    // "111 yyyymmddhhmmss" — exactly 14 ASCII digits after the code.
    var payload = response.Length >= 18 ? response.Slice(4).Trim() : default;
    if (payload.Length != 14 || !AllAsciiDigits(payload))
    {
        throw new UsenetProtocolException($"Malformed DATE response: {response}");
    }

    return new DateTimeOffset(
        DigitsToInt(payload[..4]), DigitsToInt(payload[4..6]), DigitsToInt(payload[6..8]),
        DigitsToInt(payload[8..10]), DigitsToInt(payload[10..12]), DigitsToInt(payload[12..14]),
        TimeSpan.Zero); // ArgumentOutOfRangeException from impossible dates → wrap as protocol error
}
```

**Test plan.** `111 20231215143022` parses (existing); `111 2023121514302` (13 digits), `111 +2031215143022`, `111 20231315143022` (month 13) → `UsenetProtocolException`; non-111 codes still return `DateTime = null` without throwing.

---

### N-17 (Low) — yEnc CRC32 validation off by default; when enabled, missing CRC is fatal

**Problem statement.**
Wiki §14.3: CRC fields are optional, but "SHOULD be checked when present." UsenetSharp inverts this twice:

1. `ValidateDecodedBodyCrc32` defaults to `false` → CRCs present in virtually all real yEnc posts are ignored by default (silent corruption passes through).
2. When enabled, a trailer *without* a `pcrc32`/`crc32` value throws `InvalidDataException` — stricter than the draft, which makes the field optional; legitimate CRC-less posts become undownloadable in validating mode.

**Evidence.**
- Default: [UsenetClientOptions.cs](UsenetSharp/Clients/UsenetClientOptions.cs#L20-L28).
- Missing-CRC-fatal: [UsenetClient.BodyAsync.cs](UsenetSharp/Clients/UsenetClient.BodyAsync.cs#L489-L498) (`ValidateDecodedBodyCrc32` throws when `TryParseYencTrailerCrc32` fails) and the missing-trailer check at [UsenetClient.BodyAsync.cs](UsenetSharp/Clients/UsenetClient.BodyAsync.cs#L270-L274).

**Resolution proposal.**
Replace the bool with a tri-state (keep the bool as an obsolete shim for compat):

```csharp
public enum YencCrcValidationMode
{
    /// <summary>Never validate (legacy default).</summary>
    Off,
    /// <summary>Validate when the trailer carries a CRC; tolerate absent CRCs (yEnc 1.3 SHOULD).</summary>
    WhenPresent,
    /// <summary>Require and validate a CRC; absent CRC fails the stream.</summary>
    Require,
}

public YencCrcValidationMode CrcValidation { get; init; } = YencCrcValidationMode.Off;
// Flip default to WhenPresent in the next major (release-please: feat! + BREAKING CHANGE footer).
```

`WhenPresent` short-circuits `ValidateDecodedBodyCrc32` when `TryParseYencTrailerCrc32` finds no field. CRC computation cost is only paid when a mode ≠ `Off` (existing `computeCrc32` flag already gates the hot loop — keep that).

**Test plan.** Trailer with correct/incorrect `pcrc32` in each mode (existing tests cover mismatch); trailer without CRC: `WhenPresent` succeeds, `Require` fails; `Off` unchanged. Benchmark: confirm `Off` and `WhenPresent`-with-CRC costs match current enabled/disabled numbers.

---

## 4. Performance notes (no defects)

- Hot paths already use `ArrayPool`, span parsing, pooled command buffers, `ValueTask`, one shared timeout timer per body (`CoalescedReadTimeout`), and bounded `Pipe` backpressure — consistent with the "compliant but high-performance" goal.
- The material wins are bundled into findings: **N-06** (single coalesced write per pipeline batch — fewer syscalls, TCP segments, and TLS records) and **N-03** (digit-arithmetic code parse beats `int.TryParse`).
- `ParseArticleHeadersAsync` allocates per line (string + `Substring` + `Trim`); HEAD/ARTICLE are cold paths for a binary downloader, so optimization is optional — fold into N-12 if the header model is touched anyway.
- Response records always materialize `ResponseMessage` strings; they are public API, so the one `Latin1.GetString` per status line is unavoidable without an API change. Not worth breaking.

---

## 5. Suggested implementation order

Ordered by risk reduction per unit of effort; steps 1–4 form the "session integrity" package and share test infrastructure.

| Step | Finding(s) | Rationale | Est. size |
|------|-----------|-----------|-----------|
| 1 | **N-01** | Highest-impact correctness fix; the `ExchangeSingleLineAsync` helper becomes the foundation every later fix plugs into. Unlocks safe caller retries. | M |
| 2 | **N-03** | Tiny, self-contained; do in the same PR as N-01 (both touch `ParseResponseCode` call sites). | S |
| 3 | **N-02** | Small `NntpLineReader` change; pairs with N-01 poisoning semantics. | S |
| 4 | **N-04**, **N-05** | Complete the desync-prevention family using the N-01 machinery + existing drain helpers. | M |
| 5 | **N-06** | Perf + deadlock cap for the pipelining feature; benchmark before/after. | M |
| 6 | **N-07**, **N-16** | Input/output strictness; small validated changes. | S |
| 7 | **N-08**, **N-15** | QUIT + enum members (N-08 consumes the 205 enum member). | S–M |
| 8 | **N-09**, **N-11** | Connection-policy ergonomics (transient flag, TLS-auth guard) for pool owners. | S |
| 9 | **N-14**, **N-17** | Decoded-path hardening + CRC mode (N-17 default flip reserved for next major). | M |
| 10 | **N-10**, **N-12**, **N-13** | Additive API surface (CAPABILITIES, multi-value headers) and validation tightening; schedule with a minor (additive) / major (N-13 validation change) release. | M–L |

Release-note hygiene (per repo convention): steps 1–4 are `fix(nntp): …`; 5 is `feat(client): …` + `fix(nntp): …` split; 9–10 additive parts are `feat(client)`; the N-13/N-17 default changes need `!` + `BREAKING CHANGE:` footers.

## 6. Test infrastructure notes

- All non-TLS scenarios above are expressible with the existing deterministic scripted NNTP server under `UsenetSharpTest/Support` (used by `UsenetClientDeterministicTests`); no network or credentials required, keeping them out of the `Integration` category.
- Add a write-observing fake transport (count + capture writes) for N-06 and the N-07 wire-length assertions.
- Every desync fix (N-01/02/04/05) should assert the *follow-up command* behavior, not just the failing command — the regression being prevented is always on the next operation.
- Gate with the standard workflow: `dotnet test --configuration Release --no-build --filter "TestCategory!=Integration"`, plus `UsenetSharp.Benchmarks` runs for N-03/N-06/N-17.
