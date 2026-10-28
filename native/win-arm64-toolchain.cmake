# Toolchain file for Windows ARM64 builds of BoringSSL (via boring-sys).
#
# Why this exists: boring-sys's native Windows cmake invocation does not
# set CMAKE_GENERATOR_PLATFORM, so CMake's Visual Studio generator
# defaults to x64 and BoringSSL picks x86_64 assembly for an aarch64
# target. The link then fails with LNK1181 on the missing x86_64 .obj
# (seen in CI 2026-10-28, even with msvc-dev-cmd correctly configured
# for arch: arm64 and LIB/INCLUDE pointing at ARM64 dirs).
#
# boring-sys honors CMAKE_TOOLCHAIN_FILE from the environment (and the
# `cmake` crate forwards it as a -D define). When one is set, boring-sys
# skips its own cmake defines, so the MSVC runtime library -- which
# boring-sys normally sets explicitly for Windows -- is set here too.
#
# Applied automatically by native/build.ps1 for win-arm64 only.

set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR ARM64)
set(CMAKE_GENERATOR_PLATFORM ARM64)
set(CMAKE_MSVC_RUNTIME_LIBRARY MultiThreadedDLL)
