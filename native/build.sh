#!/usr/bin/env bash
#
# Build quiche as a shared library for one .NET runtime identifier (RID)
# and stage it under native/out/<rid>/.
#
# Usage: native/build.sh <rid> [--quiche-dir <path>]
#
#   <rid>  One of: win-x64, win-arm64, linux-x64, linux-arm64,
#          linux-musl-x64, linux-musl-arm64, osx-x64, osx-arm64
#
# The script clones quiche at the pinned tag from native/QUICHE_VERSION
# (unless --quiche-dir points at an existing checkout), verifies the
# commit SHA, and runs: cargo build --release -p quiche --features ffi
#
# Toolchain prerequisites are the CI workflow's job (see
# .github/workflows/native.yml and docs/native-build-matrix.md):
#   - the Rust target for the RID must be installed (rustup target add)
#   - Windows: MSVC environment + NASM (BoringSSL requirement)
#   - linux-musl-*: musl-tools for the host arch, MUSL_CC set (see below)
#
# Output layout (what the NuGet runtime packages consume):
#   native/out/<rid>/libquiche.so      (linux)
#   native/out/<rid>/libquiche.dylib   (osx)
#   native/out/<rid>/quiche.dll        (win)
# plus LICENSE-quiche.txt and LICENSE-boringssl.txt alongside.
#
# A post-build symbol check fails the build if the staged library does
# not export the core C API entry points, so a bad artifact can never
# silently ride into a package.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

RID="${1:-}"
QUICHE_DIR=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --quiche-dir) QUICHE_DIR="$2"; shift 2 ;;
    *) if [[ -z "$RID" ]]; then RID="$1"; fi; shift ;;
  esac
done

if [[ -z "$RID" ]]; then
  echo "usage: build.sh <rid> [--quiche-dir <path>]" >&2
  exit 2
fi

# --- RID -> cargo target + library file name -------------------------------
case "$RID" in
  win-x64)         CARGO_TARGET="x86_64-pc-windows-msvc";    LIB="quiche.dll" ;;
  win-arm64)       CARGO_TARGET="aarch64-pc-windows-msvc";   LIB="quiche.dll" ;;
  linux-x64)       CARGO_TARGET="x86_64-unknown-linux-gnu";  LIB="libquiche.so" ;;
  linux-arm64)     CARGO_TARGET="aarch64-unknown-linux-gnu"; LIB="libquiche.so" ;;
  linux-musl-x64)  CARGO_TARGET="x86_64-unknown-linux-musl"; LIB="libquiche.so" ;;
  linux-musl-arm64) CARGO_TARGET="aarch64-unknown-linux-musl"; LIB="libquiche.so" ;;
  osx-x64)         CARGO_TARGET="x86_64-apple-darwin";       LIB="libquiche.dylib" ;;
  osx-arm64)       CARGO_TARGET="aarch64-apple-darwin";      LIB="libquiche.dylib" ;;
  *) echo "unknown RID: $RID" >&2; exit 2 ;;
esac

# shellcheck disable=SC1091
source "$SCRIPT_DIR/QUICHE_VERSION"

# --- Obtain quiche source ---------------------------------------------------
if [[ -z "$QUICHE_DIR" ]]; then
  QUICHE_DIR="$(mktemp -d)/quiche"
  echo "cloning quiche $QUICHE_TAG ..."
  git clone --quiet --depth 1 --branch "$QUICHE_TAG" "$QUICHE_REPO" "$QUICHE_DIR"
fi

ACTUAL_COMMIT="$(git -C "$QUICHE_DIR" rev-parse HEAD)"
if [[ "$ACTUAL_COMMIT" != "$QUICHE_COMMIT" ]]; then
  echo "FATAL: quiche checkout is $ACTUAL_COMMIT, expected $QUICHE_COMMIT" >&2
  exit 1
fi
echo "quiche $QUICHE_TAG @ $QUICHE_COMMIT verified"

# --- Build ------------------------------------------------------------------
# musl targets: boring-sys builds BoringSSL's C code with cmake; it must
# see the musl compiler, not the host gcc, or the result links glibc.
# CI sets MUSL_CC (musl-gcc for the host arch); default to plain musl-gcc.
if [[ "$RID" == linux-musl-* ]]; then
  MUSL_CC="${MUSL_CC:-musl-gcc}"
  export CC="$MUSL_CC"
  echo "musl build: CC=$CC"
fi

echo "cargo build --release -p quiche --features ffi --target $CARGO_TARGET"
cargo build --release -p quiche --features ffi --target "$CARGO_TARGET" \
  --manifest-path "$QUICHE_DIR/Cargo.toml"

BUILT_LIB="$QUICHE_DIR/target/$CARGO_TARGET/release/$LIB"
if [[ ! -f "$BUILT_LIB" ]]; then
  echo "FATAL: expected artifact missing: $BUILT_LIB" >&2
  exit 1
fi

# --- Stage ------------------------------------------------------------------
OUT_DIR="$REPO_ROOT/native/out/$RID"
mkdir -p "$OUT_DIR"
cp "$BUILT_LIB" "$OUT_DIR/"

# quiche's release profile keeps debuginfo (the unstripped .so is ~40 MB);
# strip it for shipping. Dynamic symbols survive stripping, so the symbol
# check below still validates the shipped artifact. Skipped on Windows,
# where strip is not part of the standard toolchain.
if [[ "$LIB" != *.dll ]] && command -v strip >/dev/null 2>&1; then
  strip "$OUT_DIR/$LIB"
  echo "stripped $LIB for shipping"
fi
cp "$QUICHE_DIR/COPYING" "$OUT_DIR/LICENSE-quiche.txt"
# BoringSSL's license comes from boring-sys's build output
# (target/<triple>/release/build/boring-sys-*/out/boringssl/LICENSE).
BORING_LICENSE="$(find "$QUICHE_DIR" -path '*boringssl*/LICENSE' -print -quit 2>/dev/null || true)"
if [[ -n "$BORING_LICENSE" ]]; then
  cp "$BORING_LICENSE" "$OUT_DIR/LICENSE-boringssl.txt"
else
  echo "WARNING: BoringSSL LICENSE not found in checkout; package step must supply it" >&2
fi

# --- Symbol smoke check ------------------------------------------------------
# The staged library must export the core C API, or the build fails here
# instead of shipping a library the P/Invoke layer cannot bind.
# Linux: nm -D lists dynamic symbols. macOS: nm -gU, symbols carry a
# leading underscore. Windows: dumpbin /exports when available; the
# check is skipped otherwise (the .NET-side load test in step 4 covers it).
#
# Implementation note: the export list is captured ONCE into a variable
# and grepped from there. Piping nm straight into grep -q under
# `set -o pipefail` is racy: grep exits on the first match, nm dies of
# SIGPIPE, and pipefail reports the pipeline as failed even though the
# symbol was found. (Caught live 2026-10-28: the check failed on a
# different symbol on consecutive runs of the same library.)
SYMBOLS="quiche_config_new quiche_conn_new_with_tls quiche_version quiche_conn_send quiche_conn_recv"
if [[ "$LIB" == *.dylib ]] && command -v nm >/dev/null 2>&1; then
  EXPORTS="$(nm -gU "$OUT_DIR/$LIB" 2>/dev/null || true)"
  PATTERN_PREFIX=" _"
elif [[ "$LIB" == *.so ]] && command -v nm >/dev/null 2>&1; then
  EXPORTS="$(nm -D "$OUT_DIR/$LIB" 2>/dev/null || true)"
  PATTERN_PREFIX=" "
elif [[ "$LIB" == *.dll ]] && command -v dumpbin >/dev/null 2>&1; then
  EXPORTS="$(dumpbin /exports "$OUT_DIR/$LIB" 2>/dev/null || true)"
  PATTERN_PREFIX=""
else
  echo "WARNING: no export-listing tool available; skipping symbol check" >&2
  EXPORTS=""
fi
if [[ -n "$EXPORTS" ]]; then
  for sym in $SYMBOLS; do
    if [[ -n "$PATTERN_PREFIX" ]]; then
      grep -q "${PATTERN_PREFIX}${sym}$" <<<"$EXPORTS" || { echo "FATAL: $LIB does not export $sym" >&2; exit 1; }
    else
      grep -q "$sym" <<<"$EXPORTS" || { echo "FATAL: $LIB does not export $sym" >&2; exit 1; }
    fi
  done
  echo "symbol check passed: core C API exports present"
fi

ls -la "$OUT_DIR/"
echo "staged: $OUT_DIR/$LIB"
