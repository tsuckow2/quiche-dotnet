#!/usr/bin/env python3
"""Smoke-test a staged quiche shared library.

Loads the library from native/out/<rid>/, calls quiche_version() and
quiche_config_new() / quiche_config_free(), and prints the results.
This is a build-verification aid, not a test-suite replacement: the
real interop tests land in roadmap step 4.

Usage: python3 native/smoke-test.py <path-to-libquiche>
"""

import ctypes
import sys

lib = ctypes.CDLL(sys.argv[1])

lib.quiche_version.restype = ctypes.c_char_p
lib.quiche_version.argtypes = []
print("quiche_version:", lib.quiche_version().decode())

lib.quiche_config_new.restype = ctypes.c_void_p
lib.quiche_config_new.argtypes = [ctypes.c_uint32]
lib.quiche_config_free.restype = None
lib.quiche_config_free.argtypes = [ctypes.c_void_p]

# QUICHE_PROTOCOL_VERSION (0x00000001) from quiche/include/quiche.h.
config = lib.quiche_config_new(0x00000001)
assert config, "quiche_config_new returned NULL"
print("quiche_config_new: ok (non-NULL)")
lib.quiche_config_free(config)
print("quiche_config_free: ok")
print("SMOKE TEST PASSED")
