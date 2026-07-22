# Security Policy

## Supported versions

Security fixes are provided for the latest released version. Please upgrade to
the newest package before reporting an issue that may already be resolved.

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability.

Use GitHub's
[private vulnerability reporting](https://github.com/nzbdav/UsenetSharp/security/advisories/new)
to send a description, reproduction steps, affected versions, and any proposed
mitigation. You should receive an acknowledgement within seven days. We will
coordinate validation, remediation, and disclosure through the private
advisory.

If private reporting is unavailable, open a discussion that asks a maintainer
for a private contact channel without including vulnerability details.

## Security expectations

UsenetSharp relies on platform certificate validation for TLS connections by
default. `UsenetClientOptions.SkipTlsVerification` is an explicit per-client
exception for a trusted server with a broken certificate; it disables
certificate-chain and hostname validation plus revocation lookup while
retaining TLS encryption. It applies to every host connected by that client
instance, can permit a man-in-the-middle attack, and should remain disabled
unless strict validation cannot be made to work.
NNTP credentials sent over a connection created with `useSsl: false` are
plaintext by protocol design. Do not use plaintext authentication on an
untrusted network.
