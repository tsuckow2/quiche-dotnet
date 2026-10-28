# Native build matrix

How Quiche.NET ships prebuilt quiche binaries, and why each choice was
made. The consumer promise drives everything here: one package
reference, no native setup, no Rust toolchain at consume time. That
means the build matrix is the project's problem, never the consumer's.

## Pinned version

- **quiche 0.30.0** (released 2026-09-17), pinned by tag *and* commit in
  [`native/QUICHE_VERSION`](../native/QUICHE_VERSION).
- quiche is 0.x: minor releases may change the C API. A bump is a
  deliberate change — update the pin, re-run the P/Invoke diff
  (roadmap step 2), add a CHANGELOG entry. Never float on "latest".
- Build requirements from upstream (0.30.0): Rust 1.88+, `cmake`
  (BoringSSL is built automatically by boring-sys), NASM on Windows,
  and **libclang on every platform** — boring-sys runs bindgen, and a
  missing libclang fails the build after BoringSSL itself has already
  compiled (found the hard way on the first local build, 2026-10-28:
  `Unable to find libclang`). CI installs it explicitly: `libclang-dev`
  via apt on Linux, LLVM via Chocolatey on Windows, Xcode's toolchain
  on macOS.
  The C API is enabled with `--features ffi`; the crate already builds
  `cdylib` + `staticlib` + `rlib`, so the shared library is a normal
  build artifact, not a custom build mode.

## RIDs and how each is built

| RID | CI runner | Rust target | Toolchain notes |
| --- | --- | --- | --- |
| `win-x64` | `windows-latest` | `x86_64-pc-windows-msvc` | MSVC env via `msvc-dev-cmd`; NASM via Chocolatey (BoringSSL requirement) |
| `win-arm64` | `windows-11-arm` | `aarch64-pc-windows-msvc` | Native ARM64 runner — no cross-linking |
| `linux-x64` | `ubuntu-24.04` | `x86_64-unknown-linux-gnu` | Default toolchain; **proven locally 2026-10-28** |
| `linux-arm64` | `ubuntu-24.04-arm` | `aarch64-unknown-linux-gnu` | Native ARM64 runner — no cross-linking |
| `linux-musl-x64` | `ubuntu-24.04` | `x86_64-unknown-linux-musl` | `musl-tools` from apt; `CC=musl-gcc` so BoringSSL's C code links musl, not glibc |
| `linux-musl-arm64` | `ubuntu-24.04-arm` | `aarch64-unknown-linux-musl` | `musl-tools` for the host arch on the ARM runner; same `CC=musl-gcc` rule |
| `osx-x64` | `macos-15` (arm64) | `x86_64-apple-darwin` | Cross-compiled from the ARM runner with the same macOS SDK — clang handles both archs |
| `osx-arm64` | `macos-15` (arm64) | `aarch64-apple-darwin` | Native |

Why native runners where they exist: cross-linking C dependencies
(BoringSSL) is where native builds go wrong, and GitHub now provides
ARM runners for all three OS families. The only cross compiles are
`osx-x64` (same SDK, different arch — the safe kind) and the musl
cells (same arch, different libc — contained by `CC=musl-gcc`).

Why not a container/cross image for musl: `musl-tools` is a standard
apt package on both x64 and ARM Ubuntu, so the musl cells need no
exotic toolchain. If a musl cell proves flaky in CI, the fallback is
`cargo-zigbuild` — not adopted pre-emptively, because it would add a
second toolchain for a problem that may not exist.

## What the build script does

[`native/build.sh`](../native/build.sh) takes one RID and:

1. Clones quiche at the pinned tag (or reuses `--quiche-dir`) and
   **fails if the commit SHA does not match the pin**.
2. Builds `cargo build --release -p quiche --features ffi
   --target <target>`.
3. Stages the shared library under `native/out/<rid>/` with quiche's
   `COPYING` as `LICENSE-quiche.txt`.
4. Runs a symbol smoke check: the staged library must export
   `quiche_config_new`, `quiche_conn_new_with_tls`, `quiche_version`,
   `quiche_conn_send`, `quiche_conn_recv` (checked via `nm -D` on
   Linux, `nm -gU` on macOS, `dumpbin /exports` on Windows). A library
   the P/Invoke layer cannot bind fails here, never in a package.

Artifacts are **not committed** to the repo. CI uploads each cell's
`native/out/<rid>/` as an artifact; the NuGet packaging step (roadmap
step 5) lays them out as `runtimes/<rid>/native/` in the runtime
packages, which is what makes .NET's default `NativeLibrary`
resolution find them with zero consumer configuration.

## The one case that matters: shared-socket demux

The native library is only half the story. The reason this project
exists is quiche's application-owned-socket model: the managed layer
binds one UDP socket and routes inbound packets to connections by
connection ID, so a listener and outbound connections share a port —
the shape msquic refuses in both its managed API (OS bind failure)
and its raw C API (transport aborts the connection before any packet
leaves the machine). The native build must therefore expose the
connection-ID parsing the managed demux needs (`quiche_header_info`
and friends) — covered by the P/Invoke surface in step 2 and proven by
the port-sharing test in step 4.

## License handling

- This repo's own code: MIT.
- quiche: BSD-2-Clause (`COPYING`, staged by the build script).
- BoringSSL: its LICENSE is staged by the build script from boring-sys's
  build output (`target/<triple>/release/build/boring-sys-*/out/boringssl/LICENSE`,
  verified genuine 2026-10-28 — it carries BoringSSL's own note about
  distributing code linked against it). If a build ever cannot find it,
  the script warns; the packaging step (roadmap step 5) has a hard
  check: no runtime package ships without both license texts.

## Bump procedure

1. Update `native/QUICHE_VERSION` (tag + commit).
2. Rebuild all 8 cells in CI; fix any breakage.
3. Diff `quiche/include/quiche.h` against the P/Invoke layer; update
   signatures.
4. CHANGELOG entry naming the quiche version and any API changes.
5. Release as a new package version (native + managed move in lockstep).

## Status

- `linux-x64`: built and verified locally 2026-10-28 (see below).
- Remaining 7 cells: defined here, exercised by CI on this branch;
  results recorded in the step-1 report before merge.

## Local proof (linux-x64, 2026-10-28)

Built on this VM with cargo 1.98.1: `cargo build --release -p quiche
--features ffi` at 0.30.0 produced `libquiche.so`, exports the five
core C API symbols, and answers `quiche_version()` — see the step-1
report for the exact output. The same command, parameterized by RID,
is what `native/build.sh` runs in CI.

Sandbox-only quirks of that local build (not CI concerns — GitHub
runners have full libc dev headers and distro libclang):

- No system libclang: `LIBCLANG_PATH` pointed at the Android NDK's
  `libclang.so` (`android-sdk/ndk/28.2.13676358/.../lib`).
- No libc dev headers (`/usr/include` is empty): bindgen needed
  `BINDGEN_EXTRA_CLANG_ARGS="-I/usr/lib/gcc/x86_64-linux-gnu/13/include"`
  so it could find gcc's freestanding `stddef.h`.
- Two failed attempts before success, both in boring-sys's bindgen
  step, after BoringSSL itself had already compiled: first "Unable to
  find libclang", then "stddef.h file not found".

Size note: the unstripped `libquiche.so` is ~40 MB (quiche's release
profile keeps debuginfo); `native/build.sh` strips it for shipping.
