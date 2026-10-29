using System;

namespace Quiche
{
    /// <summary>
    /// An error from the QUIC layer: a failed handshake, a connection
    /// closed with an error, a stream reset, or a local misuse (bad
    /// options, closed connection). <see cref="Code"/> carries quiche's
    /// error code when one exists (see Native.NativeErrors); it is 0
    /// for errors with no quiche code (e.g. cancellation, misuse).
    /// <see cref="FromPeer"/> tells whether the error arrived from the
    /// peer (true) or was raised locally (false).
    /// </summary>
    public class QuicException : Exception
    {
        public long Code { get; }

        public bool FromPeer { get; }

        public QuicException(string message)
            : base(message)
        {
        }

        public QuicException(string message, long code, bool fromPeer)
            : base(message)
        {
            Code = code;
            FromPeer = fromPeer;
        }
    }
}
