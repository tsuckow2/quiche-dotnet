# Windows ARM64 toolchain file -- disables BoringSSL assembly.
#
# History (see docs/native-build-matrix.md for the full chain):
# - The VS generator defaults to the x64 platform for boring-sys's
#   cmake invocation, so BoringSSL picked x86_64 assembly. A toolchain
#   file pinning GENERATOR_PLATFORM to ARM64 did NOT change that: the
#   platform is locked before the toolchain file is read. (Note: the
#   cmake crate itself passes -AARM64 for aarch64 targets, so the
#   generator platform was ARM64 all along -- BoringSSL's own
#   perlasm flavor selection still chose x86_64. The generator was
#   never the problem.)
# - The Ninja generator fixed the platform question but configure
#   then failed at the generate step with:
#     MSVC_RUNTIME_LIBRARY value 'MultiThreadedDLL' not known for
#     this ASM compiler.
#   A policy default (CMAKE_POLICY_DEFAULT_CMP0194=NEW) could not fix
#   it: BoringSSL pins cmake_minimum_required(VERSION 3.22), forcing
#   CMP0194 (CMake 4.1) to OLD.
# - Selecting armasm64.exe explicitly as CMAKE_ASM_COMPILER also did
#   NOT fix it: the error is about the ASM compiler's MSVC-family
#   identification, not which assembler binary runs. armasm64 is
#   identified as MSVC, and CMake still validates the runtime
#   library value against it.
#
# The remaining principled fix is the one boring-sys itself uses for
# Windows targets it considers cross-compiled: OPENSSL_NO_ASM.
# boring-sys only applies that define when host != target, and this
# build is native (host == target == aarch64-pc-windows-msvc), so it
# takes the native path and attempts assembly -- which is broken for
# Windows ARM64 in this toolchain combination. This toolchain file
# supplies the define boring-sys skips. The cost is scoped to this
# one RID: BoringSSL falls back to its C crypto implementations, so
# quiche.dll is correct and complete on win-arm64, just without
# assembly acceleration. quiche's C API and wire behavior are
# unchanged.
set(OPENSSL_NO_ASM YES CACHE BOOL "Disable BoringSSL assembly" FORCE)
