// Step 3 tests: the managed mirror of the C shared-socket harness
// (see ~/workspace/quiche-libp2p-test/shared-socket-test.c).
//
// Two QuicSockets on loopback each dial the other AND accept the
// other's connection on the same UDP port, then exchange echo
// traffic in both directions. This is the DCUtR prerequisite —
// the exact shape msquic could not deliver — now exercised through
// the public managed API instead of raw C calls.
//
// These tests NEED a working UDP socket, so they do NOT run on the
// sandbox VM (UDP is blocked there); they run on Forge and in CI
// (ubuntu). The step-2 smoke tests, which need no socket, are the
// ones that run everywhere.

using System.Net;
using System.Text;
using Quiche;
using Xunit;

namespace Quiche.Tests;

public class SharedSocketTests
{
    private static QuicServerOptions ServerOptions()
    {
        string baseDir = AppContext.BaseDirectory;
        return new QuicServerOptions
        {
            CertificateFile = Path.Combine(baseDir, "test-cert.pem"),
            PrivateKeyFile = Path.Combine(baseDir, "test-key.pem"),
        };
    }

    private static QuicClientOptions TestClientOptions() => new()
    {
        // Test-only self-signed certificate; nothing trusts it.
        VerifyPeer = false,
    };

    [Fact]
    public async Task TwoSocketsDialAndAcceptWithEchoBothWays()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken ct = timeout.Token;
        using var socketA = new QuicSocket(
            new IPEndPoint(IPAddress.Loopback, 0), ServerOptions());
        using var socketB = new QuicSocket(
            new IPEndPoint(IPAddress.Loopback, 0), ServerOptions());

        // Each socket dials the other and accepts the other's
        // connection, all on the same two UDP ports.
        Task<QuicConnection> aConnectsToB = socketA.ConnectAsync(
            socketB.LocalEndPoint, "localhost", TestClientOptions(), ct);
        Task<QuicConnection> bConnectsToA = socketB.ConnectAsync(
            socketA.LocalEndPoint, "localhost", TestClientOptions(), ct);
        Task<QuicConnection> aAcceptsB = socketA.AcceptAsync(ct);
        Task<QuicConnection> bAcceptsA = socketB.AcceptAsync(ct);

        QuicConnection[] connections = await Task.WhenAll(
            aConnectsToB, bConnectsToA, aAcceptsB, bAcceptsA);
        foreach (var connection in connections)
        {
            Assert.True(connection.IsEstablished);
        }
        QuicConnection aToB = await aConnectsToB;
        QuicConnection bToA = await bConnectsToA;
        QuicConnection aFromB = await aAcceptsB;
        QuicConnection bFromA = await bAcceptsA;

        // Direction A -> B, with echo.
        await EchoOnce(aToB, bFromA, "hello from A-client", ct);
        // Direction B -> A, with echo.
        await EchoOnce(bToA, aFromB, "hello from B-client", ct);
    }

    [Fact]
    public async Task ConnectToDialOnlySocketCancels()
    {
        // A socket without server options drops inbound Initials, so
        // a dial to it can never establish; cancellation must unwind
        // the pending connect instead of hanging.
        using var dialOnly = new QuicSocket(
            new IPEndPoint(IPAddress.Loopback, 0));
        using var dialer = new QuicSocket(
            new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        // ThrowsAnyAsync, not ThrowsAsync: cancellation surfaces as
        // TaskCanceledException, which derives from (but is not
        // exactly) OperationCanceledException, and the exact-match
        // assertion rejects it.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await dialer.ConnectAsync(dialOnly.LocalEndPoint, "localhost",
                TestClientOptions(), timeout.Token));
    }

    private static async Task EchoOnce(QuicConnection dialerSide,
        QuicConnection listenerSide, string message, CancellationToken ct)
    {
        byte[] payload = Encoding.ASCII.GetBytes(message);
        QuicStream outbound = dialerSide.OpenStream();
        await outbound.SendAsync(payload, fin: true, ct);

        QuicStream inbound = await listenerSide.AcceptStreamAsync(ct);
        // ReadAsync is a streaming read: a message can arrive as
        // data without FIN followed by an empty FIN read (two
        // packets), so accumulate until FIN rather than assuming
        // one read returns the whole message.
        byte[] received = await ReadUntilFinAsync(inbound, ct);
        Assert.Equal(payload, received);

        await inbound.SendAsync(received, fin: true, ct);
        byte[] echo = await ReadUntilFinAsync(outbound, ct);
        Assert.Equal(payload, echo);
    }

    private static async Task<byte[]> ReadUntilFinAsync(
        QuicStream stream, CancellationToken ct)
    {
        var all = new List<byte>();
        while (true)
        {
            QuicReadResult chunk = await stream.ReadAsync(ct);
            all.AddRange(chunk.Data);
            if (chunk.Fin)
            {
                return all.ToArray();
            }
        }
    }
}
