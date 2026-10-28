# Windows ARM64 toolchain file -- assembler selection only.
#
# History (see docs/native-build-matrix.md for the full chain):
# - The VS generator defaults to the x64 platform for boring-sys's
#   cmake invocation, so BoringSSL picked x86_64 assembly. A toolchain
#   file pinning GENERATOR_PLATFORM to ARM64 did NOT change that: the
#   platform is locked before the toolchain file is read.
# - The Ninja generator fixed the platform problem (ARM64 cl.exe
#   detected), but configure then failed with:
#     MSVC_RUNTIME_LIBRARY value 'MultiThreadedDLL' not known for
#     this ASM compiler.
#   because CMake 4.x treats MSVC's cl as the ASM compiler (CMP0194
#   OLD) and cl-as-assembler has no runtime-library concept.
# - Setting CMAKE_POLICY_DEFAULT_CMP0194=NEW in this file did NOT
#   help either: BoringSSL's CMakeLists pins
#   cmake_minimum_required(VERSION 3.22), which forces every policy
#   introduced after 3.22 -- including CMP0194 (4.1) -- to OLD,
#   overriding any default. Policy defaults cannot fix this.
#
# What does work: select the real ARM64 assembler explicitly. An
# explicitly set CMAKE_ASM_COMPILER bypasses candidate selection
# entirely, so CMP0194 is irrelevant, and MSVC_RUNTIME_LIBRARY no
# longer applies to the ASM compiler. armasm64.exe ships beside
# cl.exe in the MSVC install and is first on PATH in the arch: arm64
# vcvars environment, so find_program locates it without hardcoding
# a VS version directory. BoringSSL's Windows ARM64 assembly is
# written for armasm64 -- this is the upstream-correct tool, not a
# workaround.
#
# CMAKE_ASM_FLAGS is forced empty: the cmake crate passes cl-style
# flags (-nologo -MD -Brepro -W0) that armasm64 does not accept.
# BoringSSL appends what it needs itself in its CMakeLists.
find_program(QUICHE_ARMASM64 NAMES armasm64.exe
  PATHS
    "$ENV{VCToolsInstallDir}bin/HostARM64/ARM64"
    "$ENV{VCToolsInstallDir}bin/HostX64/ARM64")
if(QUICHE_ARMASM64)
  set(CMAKE_ASM_COMPILER "${QUICHE_ARMASM64}"
    CACHE FILEPATH "ARM64 assembler" FORCE)
  set(CMAKE_ASM_FLAGS "" CACHE STRING "ASM flags" FORCE)
  message(STATUS "Quiche.NET: using ARM64 assembler ${QUICHE_ARMASM64}")
endif()
