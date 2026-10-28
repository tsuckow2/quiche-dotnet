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
| `win-x64` | `windows-latest` | `x86_64-pc-windows-msvc` | MSVC env via `msvc-dev-cmd`; NASM via Chocolatey (BoringSSL requirement); built by `native/build.ps1` under PowerShell (see below) |
| `win-arm64` | `windows-11-arm` | `aarch64-pc-windows-msvc` | Native ARM64 runner — no cross-linking; `msvc-dev-cmd` MUST get `arch: arm64` (see below); built by `native/build.ps1` under PowerShell |
| `linux-x64` | `ubuntu-24.04` | `x86_64-unknown-linux-gnu` | Default toolchain; **proven locally and in CI 2026-10-28** |
| `linux-arm64` | `ubuntu-24.04-arm` | `aarch64-unknown-linux-gnu` | Native ARM64 runner — no cross-linking; **proven in CI 2026-10-28** |
| `linux-musl-x64` | `ubuntu-24.04` | `x86_64-unknown-linux-musl` | `cargo-zigbuild` + Zig 0.15.1, plus `-C target-feature=-crt-static` to re-enable cdylib (see below) |
| `osx-x64` | `macos-15` (arm64) | `x86_64-apple-darwin` | Cross-compiled from the ARM runner with the same macOS SDK — clang handles both archs; dylib is NOT stripped (see below) |
| `osx-arm64` | `macos-15` (arm64) | `aarch64-apple-darwin` | Native; dylib is NOT stripped (see below) |

`linux-musl-arm64` is **deferred, not forgotten**: rustc 1.99 drops
`cdylib` for `aarch64-unknown-linux-musl` (`warning: dropping
unsupported crate type 'cdylib'`), so no shared library is produced at
all. It returns if rustc gains cdylib support for that target, or if
a hand-link recipe (staticlib + manual `.so` link) is proven. The
matrix is 7 cells until then.

Why native runners where they exist: cross-linking C dependencies
(BoringSSL) is where native builds go wrong, and GitHub now provides
ARM runners for all three OS families. The only cross compile is
`osx-x64` (same SDK, different arch — the safe kind).

## First CI run: what broke, and the fix for each (2026-10-28)

The first matrix run went 2/8: `linux-x64` and `linux-arm64` passed;
the other six failed in setup/toolchain, never in quiche itself. Each
failure changed the design above:

- **Windows under Git Bash: two failures, one decision (both win
  cells).** First, Git for Windows' Unix `link` shadows MSVC's
  linker in PATH (`/usr/bin/link: extra operand` — a caveat
  `msvc-dev-cmd`'s own README documents). Pinning cargo's linker to
  MSVC's `link.exe` fixed that, and exposed the second: MSVC's linker
  then failed with LNK1181 `cannot open input file 'kernel32.lib'`,
  because MSYS2's environment conversion mangles vcvarsall's
  semicolon-separated LIB/INCLUDE on the way to the toolchain.
  Rather than fight both from bash, Windows builds moved to
  `native/build.ps1` under PowerShell, where `msvc-dev-cmd`'s
  environment reaches cargo unchanged and `link.exe` resolves to
  MSVC's (the script still pins the linker via `VCToolsInstallDir`
  as belt-and-braces, and points `LIBCLANG_PATH` at the Chocolatey
  LLVM install when present). `build.sh` refuses Windows RIDs and
  points at `build.ps1`, so the two paths cannot diverge silently.
- **Windows ARM64 picked x86_64 assembly.** `msvc-dev-cmd` defaults
  to `arch: x64` even on ARM runners, so the first suspicion was the
  MSVC environment. Fixing that (`arch: arm64` for the win-arm64
  cell, verified in the log: LIB/INCLUDE point at ARM64 dirs) did NOT
  fix it: BoringSSL's cmake still built `aes-gcm-avx2-x86_64-apple.obj`
  for an aarch64 target. Root cause is one layer deeper: boring-sys's
  native Windows cmake invocation never sets CMAKE_GENERATOR_PLATFORM,
  so the Visual Studio generator defaults to x64 regardless of the
  vcvars environment. Attempted fix: `native/win-arm64-toolchain.cmake`
  (CMAKE_SYSTEM_PROCESSOR + CMAKE_GENERATOR_PLATFORM = ARM64, plus the
  MSVC runtime library boring-sys skips when a toolchain file is set),
  applied automatically by `build.ps1` for win-arm64 via the
  CMAKE_TOOLCHAIN_FILE environment variable that boring-sys and the
  `cmake` crate both honor. Decided by the next CI run; if it does not
  work, win-arm64 joins musl-arm64 as deferred, with the remaining
  options being an upstream boring-sys fix or OPENSSL_NO_ASM (correct
  but slower: no assembly crypto).
- **musl C++ compiler does not exist (musl-x64).** With `CC=musl-gcc`,
  boring-sys's cmake derives `CMAKE_CXX_COMPILER=x86_64-linux-musl-g++`,
  which musl-tools does not ship, so cmake configure fails. Fix: musl
  cells now build with `cargo-zigbuild` + Zig 0.15.1, which supplies
  musl C and C++ toolchains. There is no fallback to musl-gcc: a
  "musl" binary secretly linked against glibc's libstdc++ would be
  worse than a loud failure.
- **macOS `strip` refuses these dylibs (both osx cells).** Apple's
  strip errors on indirect symbol table entries (`___error`,
  `_strerror`, …) in the quiche dylib. Fix: `build.sh` strips Linux
  `.so` files only; macOS dylibs ship unstripped.
- **musl-arm64: rustc drops cdylib** (see deferred note above). No
  toolchain fix exists at this layer; the cell is out until rustc
  supports it.
- **musl-x64, second failure: rustc drops cdylib here too.** The
  zigbuild run (Zig 0.15.1 resolved, cargo-zigbuild 0.24.0 installed,
  BoringSSL compiled under zig) ended with the same warning as
  musl-arm64: `dropping unsupported crate type 'cdylib' for target
  'x86_64-unknown-linux-musl'`, and cargo "finished" with no `.so`.
  Root cause is the same layer, not the toolchain: musl targets
  default to a static CRT, and cdylib requires dynamic linking.
  Attempted fix: `build.sh` passes `-C target-feature=-crt-static`
  for musl targets only (via `CARGO_TARGET_<TRIPLE>_RUSTFLAGS`, so
  host build scripts are untouched). Whether that restores cdylib is
  decided by the next CI run; if it does not, musl-x64 joins
  musl-arm64 as deferred, and the matrix ships 6 cells.

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

- `linux-x64`: built and verified locally and in CI 2026-10-28.
- `linux-arm64`: built and verified in CI 2026-10-28.
- `win-x64`: green in CI 2026-10-28 via `build.ps1`.
- `win-arm64`: `build.ps1` works, but BoringSSL's cmake selects
  x86_64 assembly; the toolchain-file fix is awaiting the CI re-run.
- `linux-musl-x64`: zigbuild works, but rustc drops cdylib; the
  `-crt-static` re-enable is awaiting the CI re-run.
- `linux-musl-arm64`: deferred (rustc cdylib limitation, above).

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
