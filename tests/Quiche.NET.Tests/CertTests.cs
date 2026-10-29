// Phase 0 tests: peer-certificate surfacing and client-cert options.
//
// The roundtrip test needs UDP, so it runs on Forge and in CI, not
// on the sandbox VM (same split as SharedSocketTests). The option
// validation test needs no socket and runs everywhere.
//
// Fixtures: test-cert.pem/test-key.pem (CN=localhost, the server
// certificate) and client-cert.pem/client-key.pem (CN=client, the
// dialer's certificate). Both self-signed, trusted by nothing;
// the tests dial with VerifyPeer=false.

using System;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Quiche.Native;
using Xunit;

namespace Quiche.Tests;

public class CertTests
{
    [Fact]
    public async Task PeerCertificateRoundtrip()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken ct = timeout.Token;
        using var listener = new QuicSocket(
            new IPEndPoint(IPAddress.Loopback, 0), ServerOptions());
        using var dialer = new QuicSocket(
            new IPEndPoint(IPAddress.Loopback, 0));
        Task<QuicConnection> connect = dialer.ConnectAsync(
            listener.LocalEndPoint, "localhost", ClientOptions(), ct);
        Task<QuicConnection> accept = listener.AcceptAsync(ct);
        QuicConnection clientConn = await connect;
        QuicConnection serverConn = await accept;
        Assert.True(clientConn.IsEstablished);
        Assert.True(serverConn.IsEstablished);

        // Dial side: the server's certificate, byte-identical DER.
        byte[]? clientPeer = clientConn.PeerCertificate;
        Assert.NotNull(clientPeer);
        // CreateFromPemFile(path) expects the file to hold a
        // certificate AND its private key, and throws when only
        // the certificate is present; the cert-only ReadOnlySpan
        // overload is the right one for a bare certificate file.
        byte[] serverDer =
            X509Certificate2.CreateFromPem(File.ReadAllText("test-cert.pem")).RawData;
        Assert.Equal(serverDer, clientPeer!);

        // Server side: null, by quiche's design, not by omission.
        // A quiche server with verification off never requests a
        // client certificate, so the dialer presents none even
        // though it loaded one (measured in the 2026-10-28 C
        // cert experiment against quiche 0.30.0). A transport
        // that needs inbound identity must get it another way.
        Assert.Null(serverConn.PeerCertificate);
    }

    [Fact]
    public void ClientCertOptionsValidation()
    {
        // Cert without key, and key without cert, are config errors.
        Assert.Throws<QuicException>(() => NativeConfig.BuildClient(
            new QuicClientOptions { CertificateFile = "client-cert.pem" }));
        Assert.Throws<QuicException>(() => NativeConfig.BuildClient(
            new QuicClientOptions { PrivateKeyFile = "client-key.pem" }));
        // Both set but files missing: the load fails naming the file.
        Assert.Throws<QuicException>(() => NativeConfig.BuildClient(
            new QuicClientOptions
            {
                CertificateFile = "nope.pem",
                PrivateKeyFile = "nope-key.pem",
            }));
        // Both set and valid: loads fine (positive control).
        using NativeConfig config = NativeConfig.BuildClient(ClientOptions());
        Assert.NotNull(config);
    }

    private static QuicServerOptions ServerOptions() => new()
    {
        CertificateFile = "test-cert.pem",
        PrivateKeyFile = "test-key.pem",
    };

    private static QuicClientOptions ClientOptions() => new()
    {
        VerifyPeer = false,
        CertificateFile = "client-cert.pem",
        PrivateKeyFile = "client-key.pem",
    };
}
