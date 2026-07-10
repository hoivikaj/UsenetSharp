# UsenetSharp

UsenetSharp is a .NET 9 library for asynchronous, read-only NNTP access and
streaming yEnc decoding.

## Features

- Asynchronous NNTP connection, authentication, STAT, HEAD, BODY, ARTICLE, and DATE commands
- TLS with platform certificate validation
- Incremental, allocation-conscious yEnc decoding through RapidYencSharp
- Read-only, non-seekable response streams with cancellation support
- Serialized commands per connection; use multiple clients for parallel downloads

## Installation

Install from [NuGet.org](https://www.nuget.org/packages/NzbDav.UsenetSharp):

```bash
dotnet add package NzbDav.UsenetSharp
```

Or add a package reference in your project file:

```xml
<PackageReference Include="NzbDav.UsenetSharp" Version="1.2.3" />
```

## Usage

### Connect and authenticate

```csharp
using UsenetSharp.Clients;

using var client = new UsenetClient();

await client.ConnectAsync(
    "news.example.com",
    port: 563,
    useSsl: true,
    cancellationToken);

var authentication = await client.AuthenticateAsync(
    "username",
    "password",
    cancellationToken);

if (!authentication.Success)
{
    throw new InvalidOperationException(authentication.ResponseMessage);
}
```

TLS always uses the platform's normal certificate chain and hostname validation.
Certificate revocation checking defaults to `X509RevocationMode.NoCheck` to avoid
revocation lookup latency during frequent streaming reconnects. Applications
that require revocation checking can enable it when constructing the client:

```csharp
using System.Security.Cryptography.X509Certificates;

using var client = new UsenetClient(new UsenetClientOptions
{
    CertificateRevocationCheckMode = X509RevocationMode.Online
});
```

`Online` provides current revocation information when the platform can obtain
it, but can add connection latency and make reconnects depend on revocation
responder availability. `Offline` uses locally cached revocation information.
The selected mode changes only revocation checking; normal platform certificate
and hostname validation remains enabled. Credentials sent with `useSsl: false`
travel in plaintext; only use an unencrypted connection on a network you trust.

### Retrieve an article body

`SegmentId` accepts a message ID with or without angle brackets:

```csharp
using UsenetSharp.Models;

SegmentId segmentId = "article-id@example.com";
var response = await client.BodyAsync(segmentId, cancellationToken);

if (!response.Success || response.Stream is null)
{
    Console.WriteLine(response.ResponseMessage);
    return;
}

await using var body = response.Stream;
await body.CopyToAsync(destination, cancellationToken);
```

`ARTICLE` also exposes parsed headers:

```csharp
var response = await client.ArticleAsync(segmentId, cancellationToken);

if (response.ArticleHeaders is not null)
{
    Console.WriteLine(response.ArticleHeaders.Subject);
}

if (response.Stream is not null)
{
    await using var article = response.Stream;
    await article.CopyToAsync(destination, cancellationToken);
}
```

### Check article availability

```csharp
var response = await client.StatAsync(segmentId, cancellationToken);

if (response.ArticleExists)
{
    Console.WriteLine($"Article is available ({response.ResponseCode}).");
}
```

Calling `BODY` or `ARTICLE` directly is usually preferable to issuing a separate
`STAT` request first.

### Decode yEnc content

`YencStream` owns and disposes the body stream passed to it by default. Pass
`leaveOpen: true` when the caller must retain ownership:

```csharp
using UsenetSharp.Streams;

var response = await client.BodyAsync(segmentId, cancellationToken);
if (response.Stream is null)
{
    return;
}

await using var yenc = new YencStream(response.Stream);
var header = await yenc.GetYencHeadersAsync(cancellationToken);

if (header is not null)
{
    Console.WriteLine($"{header.FileName}: {header.FileSize} bytes");
}

await yenc.CopyToAsync(destination, cancellationToken);
```

`DecodedBodyAsync` decodes yEnc data directly as raw chunks. CRC32 validation is
optional and disabled by default for backward compatibility. Enable it when
constructing the client to require a valid `crc32` trailer for single-part
articles or `pcrc32` for multipart articles:

```csharp
var client = new UsenetClient(new UsenetClientOptions
{
    ValidateDecodedBodyCrc32 = true
});

var response = await client.DecodedBodyAsync(segmentId, cancellationToken);
if (response.Stream is not null)
{
    await response.Stream.CopyToAsync(destination, cancellationToken);
}
```

When validation is enabled, a missing, malformed, or mismatched CRC32 value
fails the decoded response stream with `InvalidDataException`.

### Connection and stream lifecycle

One `UsenetClient` owns one TCP/TLS connection. Commands on that connection are
serialized. After a successful `BODY` or `ARTICLE`, the connection remains
reserved until the NNTP body terminator is consumed or the transfer fails.
Dispose response streams promptly, and call `WaitForReadyAsync` when you need to
know that the connection can accept another command. Use a separate client per
parallel download.

The body streams are readable but not writable or seekable. Dispose the client
after all active response streams have finished.

## Requirements

- .NET 9 SDK to build the repository
- .NET 9 runtime to consume the current package
- RapidYencSharp includes native binaries for Windows x64, Linux x64, and Linux
  ARM64. Other platforms, including macOS, must build `rapidyenc` and place its
  native library beside the application before using `YencStream`.

## Development and testing

Deterministic tests use local scripted NNTP servers and need no network access
or credentials:

```bash
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build --filter "TestCategory!=Integration"
dotnet pack UsenetSharp/UsenetSharp.csproj --configuration Release --no-build
```

Live-server tests are marked `Integration` and are excluded from CI. Set
`USENETSHARP_TEST_HOST`, `USENETSHARP_TEST_USERNAME`, and
`USENETSHARP_TEST_PASSWORD` to run them locally; never commit credentials.

See [CONTRIBUTING.md](CONTRIBUTING.md) for the development workflow and
[SECURITY.md](SECURITY.md) for private vulnerability reporting.

## License

Licensed under the [MIT License](LICENSE).
