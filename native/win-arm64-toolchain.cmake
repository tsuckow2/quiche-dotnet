# Windows ARM64 toolchain file -- CMake policy defaults only.
#
# This file does NOT set the generator platform (that was tried and
# failed: the VS generator's platform is locked before the toolchain
# file is read -- see docs/native-build-matrix.md). Its single job is
# CMP0194.
#
# CMP0194 NEW: MSVC is not an assembler for language ASM. Under the
# Ninja generator, the cmake crate passes
# -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreadedDLL unconditionally, and
# with CMP0194 OLD CMake validates that value against the MSVC
# "assembler" and the configure step dies with:
#   MSVC_RUNTIME_LIBRARY value 'MultiThreadedDLL' not known for this
#   ASM compiler.
# (The VS generator never validates it, which is why win-x64 and the
# earlier win-arm64 VS runs got past configure.) With CMP0194 NEW,
# CMake finds the real ARM64 assembler (armasm64.exe, shipped beside
# cl.exe) instead, the property no longer applies to the ASM
# compiler, and BoringSSL's ARM64 assembly is assembled by the tool
# built for it. CMAKE_POLICY_DEFAULT_CMP* variables are documented
# for exactly this use: setting them in a toolchain file.
set(CMAKE_POLICY_DEFAULT_CMP0194 NEW)
