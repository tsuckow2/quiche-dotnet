// Step 2 smoke tests: the generated P/Invoke layer loads the real
// native library and round-trips the cheapest calls. These tests do
// NOT need UDP (they run anywhere, including the sandbox VM); the
// tests that need a socket live in step 3 and run on Forge + CI.
//
// Prerequisite: a built native library staged under native/out/
// (see the StageQuicheNative target in the test project; CI builds
// it first via native/build.sh). Without it, these tests fail with
// DllNotFoundException naming 'quiche'.

// Pointers to quiche's opaque Config type trip CS8500 (same pragma
// the generated bindings carry); the pointers are never dereferenced
// from C#, only passed back to quiche.
#pragma warning disable CS8500

using System.Runtime.InteropServices;
using Quiche.Native;
using Xunit;

namespace Quiche.Tests;

public class NativeSmokeTests
{
    public NativeSmokeTests()
    {
        NativeLoader.EnsureLoaded();
    }

    [Fact]
    public unsafe void VersionIsPinnedRelease()
    {
        byte* versionPtr = NativeMethods.quiche_version();
        Assert.True((IntPtr)versionPtr != IntPtr.Zero);
        string version = Marshal.PtrToStringAnsi((IntPtr)versionPtr)!;
        // Pinned by native/QUICHE_VERSION; bump the pin and this
        // expectation together, never one without the other.
        Assert.Equal("0.30.0", version);
    }

    [Fact]
    public unsafe void ConfigRoundTrip()
    {
        // QUICHE_PROTOCOL_VERSION from quiche/include/quiche.h. It is a
        // C #define, not a Rust const, so csbindgen does not emit it.
        const uint quicheProtocolVersion = 0x00000001;
        Config* config = NativeMethods.quiche_config_new(quicheProtocolVersion);
        Assert.True((IntPtr)config != IntPtr.Zero);
        NativeMethods.quiche_config_free(config);
    }
}
