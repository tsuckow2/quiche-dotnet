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

1. **Native build matrix (CI):** ✅ quiche 0.30.0 built as a shared
   library for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`,
   `linux-musl-x64`, `osx-x64`, `osx-arm64` -- all 7 cells green in
   CI (2026-10-28), each smoke-checked for the core C API symbols
   and staged with quiche + BoringSSL license texts. `linux-musl-arm64`
   is deferred: rustc drops `cdylib` for that target (see the design
   doc). Design: [docs/native-build-matrix.md](docs/native-build-matrix.md);
   build scripts: `native/build.sh` (Unix) and `native/build.ps1`
   (Windows); CI: `.github/workflows/native.yml`. Known
   per-cell notes: `win-arm64` builds with `OPENSSL_NO_ASM` (C crypto,
   same API) because BoringSSL's Windows ARM64 assembly path is broken
   in this toolchain combination -- see the design doc.
2. **P/Invoke layer:** ✅ 146 `DllImport` signatures over quiche
   0.30.0's C API, generated from quiche's own Rust FFI source with
   csbindgen (regenerable: `tools/bindgen`, see its README), plus
   hand-written companions for types csbindgen cannot see (opaque
   `Config`/`Connection`/iterators, libc `sockaddr` structs, error
   codes) and a native library resolver (NuGet `runtimes/<rid>/native/`
   layout, `$QUICHE_NATIVE_PATH` override). A loader smoke test
   asserts the pinned version string. CI: `.github/workflows/tests.yml`
   builds the linux-x64 library and runs the suite.
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
