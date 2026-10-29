using System;

namespace Quiche
{
    /// <summary>
    /// Options for an outbound connection (<see
    /// cref="QuicSocket.ConnectAsync"/>). All properties are optional
    /// except <see cref="ServerName"/>, which is passed per call.
    /// </summary>
    public class QuicClientOptions
    {
        /// <summary>
        /// ALPN protocol negotiated in the TLS handshake. The peer
        /// must offer the same value or the handshake fails with
        /// NO_APPLICATION_PROTOCOL. Default: "quiche-dotnet".
        /// </summary>
        public string Alpn { get; set; } = QuicDefaults.Alpn;

        /// <summary>
        /// Verify the peer's certificate chain. Default: true (safe).
        /// Set false only for tests against self-signed certificates;
        /// never for production peers.
        /// </summary>
        public bool VerifyPeer { get; set; } = true;

        /// <summary>
        /// PEM file of trusted CA certificates. Null (the default)
        /// means BoringSSL's system defaults. Set this to trust a
        /// private CA without disabling verification entirely.
        /// </summary>
        public string? TrustedCaFile { get; set; }

        /// <summary>
        /// PEM file holding the certificate chain this side presents
        /// when the peer requests a client certificate. Must be set
        /// together with <see cref="PrivateKeyFile"/>; either one
        /// alone is a configuration error. Null (the default) means
        /// no client certificate is presented.
        /// Note: the certificate is only presented if the peer asks
        /// for one. A quiche server with verification off never
        /// asks, so the certificate is then unused; peers that do
        /// ask (libp2p implementations, verification-on servers)
        /// receive it.
        /// </summary>
        public string? CertificateFile { get; set; }

        /// <summary>PEM file holding the private key for <see cref="CertificateFile"/>.</summary>
        public string? PrivateKeyFile { get; set; }

        /// <summary>
        /// How long a connection may sit idle before quiche closes
        /// it. Default: 30 seconds. <see cref="TimeSpan.Zero"/>
        /// disables the idle timeout (connections then live until
        /// explicitly closed or the peer goes away uncleanly).
        /// </summary>
        public TimeSpan IdleTimeout { get; set; } = QuicDefaults.IdleTimeout;
    }

    /// <summary>
    /// Options for the listening side of a <see cref="QuicSocket"/>.
    /// A socket without server options is dial-only: inbound
    /// Initials are dropped and <see cref="QuicSocket.AcceptAsync"/>
    /// never completes.
    /// </summary>
    public class QuicServerOptions
    {
        /// <summary>PEM file holding the certificate chain.</summary>
        public string CertificateFile { get; set; } = string.Empty;

        /// <summary>PEM file holding the private key.</summary>
        public string PrivateKeyFile { get; set; } = string.Empty;

        /// <summary>
        /// ALPN protocol the listener offers. Must match what
        /// dialers use (see <see cref="QuicClientOptions.Alpn"/>).
        /// Default: "quiche-dotnet".
        /// </summary>
        public string Alpn { get; set; } = QuicDefaults.Alpn;

        /// <summary>
        /// Idle timeout, as in <see cref="QuicClientOptions.IdleTimeout"/>.
        /// </summary>
        public TimeSpan IdleTimeout { get; set; } = QuicDefaults.IdleTimeout;
    }

    /// <summary>Defaults shared by client and server options.</summary>
    public static class QuicDefaults
    {
        /// <summary>Default ALPN. Not a registered protocol; it only
        /// has to match on both ends of a connection.</summary>
        public const string Alpn = "quiche-dotnet";

        /// <summary>Default idle timeout.</summary>
        public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Transport parameters quiche advertises. quiche's own
        /// defaults are all zero, which handshakes fine but permits
        /// no stream data at all (the peer advertises no flow-control
        /// window); these values match quiche-apps and give streams
        /// room to work: 10 MB connection window, 1 MB per stream,
        /// 100 bidirectional and 100 unidirectional streams.
        /// </summary>
        public const ulong InitialMaxData = 10_000_000;

        public const ulong InitialMaxStreamDataBidiLocal = 1_000_000;

        public const ulong InitialMaxStreamDataBidiRemote = 1_000_000;

        public const ulong InitialMaxStreamDataUni = 1_000_000;

        public const ulong InitialMaxStreamsBidi = 100;

        public const ulong InitialMaxStreamsUni = 100;
    }
}
