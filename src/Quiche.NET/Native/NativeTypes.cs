// Hand-written companion to the generated NativeMethods.g.cs.
//
// csbindgen emits definitions only for types declared in the parsed
// file (quiche/src/ffi.rs). The types below are referenced by the
// generated signatures but declared elsewhere, so they would otherwise
// be missing and the project would not compile:
//
//   * Config, Connection, PathEvent, SocketAddrIter, StreamIter:
//     opaque quiche types. They are never used by value from C# (only
//     as pointers), so empty structs are correct.
//   * sockaddr, sockaddr_storage, timespec: libc types. Layouts below
//     match the C ABI on 64-bit Linux, macOS, and Windows:
//     sockaddr is 16 bytes, sockaddr_storage is 128 bytes, timespec is
//     16 bytes with tv_nsec at offset 8. (On Windows tv_nsec is a
//     32-bit long, so the high half of the C# field is padding there;
//     step 3 ignores SendInfo.at and sends immediately, so this is
//     immaterial. If a future step honors pacing, revisit per-OS.)
//
// This file is NOT generated. If a quiche bump adds a new referenced
// type that csbindgen does not emit, add it here and note the bump in
// tools/bindgen/README.md.

// Generated signatures use lowercase libc type names; keep them spelled
// exactly as generated (renaming would break the references).
#pragma warning disable CS8981

using System.Runtime.InteropServices;

namespace Quiche.Native
{
    /// <summary>
    /// Congestion control algorithm selector (quiche's repr(C) enum in
    /// recovery/mod.rs). Values are ABI-significant: note the gap
    /// (2 and 3 were removed variants) — do not renumber.
    /// </summary>
    internal enum CongestionControlAlgorithm
    {
        Reno = 0,
        CUBIC = 1,
        Bbr2Gcongestion = 4,
    }

    /// <summary>
    /// Stream shutdown direction (quiche's repr(C) enum in lib.rs).
    /// </summary>
    internal enum Shutdown
    {
        Read = 0,
        Write = 1,
    }

    /// <summary>Opaque quiche configuration object (quiche_config).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct Config
    {
    }

    /// <summary>Opaque quiche connection object (quiche_conn).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct Connection
    {
    }

    /// <summary>Opaque quiche path-event object.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct PathEvent
    {
    }

    /// <summary>Opaque quiche socket-address iterator object.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct SocketAddrIter
    {
    }

    /// <summary>Opaque quiche stream iterator object.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct StreamIter
    {
    }

    /// <summary>Generic socket address (16 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct sockaddr
    {
        public ushort sa_family;
        public fixed byte sa_data[14];
    }

    /// <summary>Storage large enough for any socket address (128 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct sockaddr_storage
    {
        public ushort ss_family;
        public fixed byte __padding[126];
    }

    /// <summary>Time value used by SendInfo.at (send pacing hint).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct timespec
    {
        public long tv_sec;
        public long tv_nsec;
    }
}
