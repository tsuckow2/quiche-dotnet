# Quiche.NET

A C# wrapper for [Cloudflare's quiche](https://github.com/cloudflare/quiche)
(QUIC + HTTP/3), shipped as a NuGet package with prebuilt native binaries
for every major runtime identifier.

## The consumer promise

Using Quiche.NET should require as little as practical from the consumer:

- **One package reference.** `dotnet add package Quiche.NET` — that's the
  whole install.
- **No native setup.** The right quiche binary for your platform
  (Windows/macOS/Linux, x64/arm64) rides inside the package and is
  resolved automatically. No manual downloads, no `DllImport` path
  wrangling, no native library placement.
- **No build-time toolchain.** Consumers never need Rust, cargo, or a C
  compiler. Native binaries are built in CI and published with the
  package.
- **Managed API, sane defaults.** Connections, streams, and the packet
  pump are exposed as idiomatic .NET types. The UDP socket pump,
  timers, and connection-ID demultiplexing are handled inside the
  library — including the case other stacks refuse: **one UDP socket
  shared between a listener and outbound connections**, which is what
  makes NAT hole punching possible.

For raw QUIC streams (the libp2p use case), the HTTP/3 layer is
optional and off the critical path — the wrapper exposes the QUIC
connection/stream API directly.

## Status

Early scaffold. The managed wrapper, native build matrix, and NuGet
packaging are under active development. See the roadmap below.

## Roadmap

1. **Native build matrix (CI):** quiche built as a shared library for
   `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`,
   `linux-musl-x64`, `linux-musl-arm64`, `osx-x64`, `osx-arm64`,
   published as runtime packages consumed via `runtimes/<rid>/native/`.
   Design: [docs/native-build-matrix.md](docs/native-build-matrix.md);
   build script: `native/build.sh`; CI: `.github/workflows/native.yml`.
   `linux-x64` is proven locally; the other cells are exercised by CI.
2. **P/Invoke layer:** thin, generated-or-handwritten signatures over
   quiche's C API, pinned to a specific quiche version.
3. **Managed API:** `QuicConnection`, `QuicStream`, listener + dialer
   over one shared UDP socket, connection-ID demultiplexing, TLS
   configuration (certificates, ALPN) with safe defaults.
4. **Tests:** unit tests for the managed layer, interop tests against a
   real quiche peer, and a port-sharing test proving listener + dialer
   coexist on one socket.
5. **NuGet publishing:** versioned releases, release notes, and
   symbol packages.

## Why quiche, and why not msquic

Microsoft's msquic — the engine behind `System.Net.Quic` — refuses to
share one UDP port between a listener and an outbound connection in the
same process, in both its managed API (OS bind failure) and its raw C
API (the transport aborts the connection internally before any packet
leaves the machine). That limitation is in msquic's binding model, not
in QUIC itself: QUIC demultiplexes connections by connection ID, and a
single socket is the protocol's natural shape.

Quiche inverts the model: the *application* owns the UDP socket and
feeds packets in and out. Port sharing then falls out by construction —
the library binds one socket and routes inbound packets to connections
by connection ID. This project exists to bring that model to .NET.

## Licensing

- This project's own code (the C# wrapper, build scripts, and docs) is
  MIT-licensed — see [LICENSE](LICENSE).
- quiche itself is BSD-2-Clause; its TLS stack is BoringSSL. Their
  license texts ship inside the native runtime packages.
