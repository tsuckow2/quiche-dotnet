// Layout pins for the FFI structs. C# Sequential layout is computed
// from field types, not from Rust, so a hand-written nested type
// with the wrong alignment silently shifts every field after it —
// which is exactly what happened with sockaddr_storage (alignment 2
// instead of 8): quiche_conn_send then wrote past the end of the
// stack-allocated SendInfo and crashed the test host. These tests
// fail on layout drift long before any packet is sent, and they
// need no UDP, so they run everywhere including the sandbox VM.

using System.Runtime.InteropServices;
using Quiche.Native;
using Xunit;

namespace Quiche.Tests;

public class NativeLayoutTests
{
    [Fact]
    public void SockaddrStorageIs128BytesAligned8()
    {
        Assert.Equal(128, Marshal.SizeOf<sockaddr_storage>());
        // The alignment member is what forces 8; without it the
        // struct is 128 bytes but only 2-aligned, and SendInfo
        // below shifts.
        Assert.Equal(0, Marshal.OffsetOf<sockaddr_storage>("ss_family").ToInt32());
    }

    [Fact]
    public void SendInfoLayoutMatchesRust()
    {
        // Rust repr(C) layout on 64-bit: from(128) @0, from_len @128,
        // to(128, align 8) @136, to_len @264, at(timespec, align 8)
        // @272, total 288.
        Assert.Equal(288, Marshal.SizeOf<SendInfo>());
        Assert.Equal(0, Marshal.OffsetOf<SendInfo>("from").ToInt32());
        Assert.Equal(128, Marshal.OffsetOf<SendInfo>("from_len").ToInt32());
        Assert.Equal(136, Marshal.OffsetOf<SendInfo>("to").ToInt32());
        Assert.Equal(264, Marshal.OffsetOf<SendInfo>("to_len").ToInt32());
        Assert.Equal(272, Marshal.OffsetOf<SendInfo>("at").ToInt32());
    }

    [Fact]
    public void RecvInfoLayoutMatchesRust()
    {
        // Pointers + socklen_t only: from @0, from_len @8, to @16,
        // to_len @24, total 32.
        Assert.Equal(32, Marshal.SizeOf<RecvInfo>());
        Assert.Equal(0, Marshal.OffsetOf<RecvInfo>("from").ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<RecvInfo>("from_len").ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<RecvInfo>("to").ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<RecvInfo>("to_len").ToInt32());
    }
}
