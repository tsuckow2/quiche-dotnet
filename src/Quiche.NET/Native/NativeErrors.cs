// Error codes returned by quiche's C API (quiche/include/quiche.h,
// enum quiche_error). csbindgen does not emit them: quiche's Rust
// Error enum is not a repr(C) type in ffi.rs, so the codes only exist
// as negative ssize_t returns. They are spelled out here so the
// managed layer can branch on them without magic numbers.
//
// Keep in sync with quiche.h on every quiche bump (see
// tools/bindgen/README.md). Only the codes the managed layer uses are
// listed; the full enum lives in quiche.h.

namespace Quiche.Native
{
    internal static class NativeErrors
    {
        /// <summary>No more work to do (not an error).</summary>
        public const long Done = -1;

        /// <summary>The provided buffer is too short.</summary>
        public const long BufferTooShort = -2;

        /// <summary>The provided packet cannot be parsed (unknown version).</summary>
        public const long UnknownVersion = -3;

        /// <summary>The provided packet cannot be parsed (invalid frame).</summary>
        public const long InvalidFrame = -4;

        /// <summary>The provided packet cannot be parsed.</summary>
        public const long InvalidPacket = -5;

        /// <summary>The operation cannot be completed (invalid state).</summary>
        public const long InvalidState = -6;

        /// <summary>The operation cannot be completed (invalid stream state).</summary>
        public const long InvalidStreamState = -7;

        /// <summary>The peer's transport parameters cannot be parsed.</summary>
        public const long InvalidTransportParam = -8;

        /// <summary>A cryptographic operation failed.</summary>
        public const long CryptoFail = -9;

        /// <summary>The TLS handshake failed.</summary>
        public const long TlsFail = -10;

        /// <summary>The peer violated the local flow control limits.</summary>
        public const long FlowControl = -11;

        /// <summary>The peer violated the local stream limits.</summary>
        public const long StreamLimit = -12;

        /// <summary>The specified stream was stopped by the peer.</summary>
        public const long StreamStopped = -15;

        /// <summary>The specified stream was reset by the peer.</summary>
        public const long StreamReset = -16;
    }
}
