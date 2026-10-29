// One UDP socket that is simultaneously a listener and a dialer —
// the shape quiche enables and msquic refused. The application owns
// the socket; quiche owns the connections; this class routes packets
// between them by connection ID.
//
// Threading contract (read this before touching the pump):
//   * One pump thread per socket owns the receive loop. All quiche
//     calls happen with _gate held, whether from the pump or from
//     user API calls (ConnectAsync setup, SendAsync, Close).
//   * User-facing waits never hold _gate: they await a
//     TaskCompletionSource that the pump completes. Continuations
//     run on the caller's context, not inline on the pump thread.
//   * A connection's native handle is freed exactly once: either by
//     an explicit Close, or by the pump when it observes the
//     connection closed. Tables are updated under _gate at the same
//     time, so a freed handle is never looked up again.
//   * IPv4 only in this step (see SockaddrUtil). Binding or dialing
//     an IPv6 endpoint throws NotSupportedException up front.
//
// What this step does NOT do (future work, not oversights to paper
// over): connection migration, 0-RTT, datagrams, key updates,
// per-stream reset errors beyond closing the read side, and
// pacing (SendInfo.at is ignored; packets go out immediately).

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Quiche.Native;

namespace Quiche
{
    /// <summary>
    /// A UDP socket hosting any number of QUIC connections: it
    /// accepts inbound connections and dials outbound ones from the
    /// same local port. Create one, then <see cref="AcceptAsync"/>
    /// for inbound connections and <see cref="ConnectAsync"/> for
    /// outbound ones. Dispose the socket to stop the pump and close
    /// every connection on it.
    /// </summary>
    public sealed class QuicSocket : IDisposable
    {
        private const int CidLength = 16;
        private const int MaxDatagram = 65535;
        private const int PumpPollMs = 50;

        private readonly Socket _udp;
        private readonly byte[] _localSockaddr;
        private readonly object _gate = new object();
        private readonly Dictionary<string, QuicConnection> _byCid =
            new Dictionary<string, QuicConnection>();
        private readonly List<QuicConnection> _connections =
            new List<QuicConnection>();
        private readonly Queue<QuicConnection> _acceptQueue =
            new Queue<QuicConnection>();
        private readonly Queue<TaskCompletionSource<QuicConnection>> _acceptWaiters =
            new Queue<TaskCompletionSource<QuicConnection>>();
        private readonly NativeConfig _sharedClientConfig;
        private readonly NativeConfig? _serverConfig;
        private readonly Thread _pump;
        private readonly byte[] _recvBuffer = new byte[MaxDatagram];
        private readonly byte[] _sendBuffer = new byte[MaxDatagram];
        private volatile bool _disposed;

        internal object Gate => _gate;

        /// <summary>The local endpoint the socket is bound to.</summary>
        public IPEndPoint LocalEndPoint { get; }

        /// <summary>
        /// Binds a socket to <paramref name="localEndPoint"/> (port 0
        /// picks an ephemeral port). With <paramref name="serverOptions"/>
        /// the socket accepts inbound connections; without it the
        /// socket is dial-only and inbound Initials are dropped.
        /// </summary>
        public QuicSocket(IPEndPoint localEndPoint,
            QuicServerOptions? serverOptions = null)
        {
            if (localEndPoint.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new NotSupportedException(
                    $"Only IPv4 endpoints are supported in this step (got {localEndPoint.AddressFamily}).");
            }
            NativeLoader.EnsureLoaded();
            _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram,
                ProtocolType.Udp);
            _udp.Bind(localEndPoint);
            LocalEndPoint = (IPEndPoint)_udp.LocalEndPoint!;
            _localSockaddr = SockaddrUtil.BuildIpv4(LocalEndPoint);
            _udp.Blocking = false;

            _sharedClientConfig = NativeConfig.BuildClient(null);
            _serverConfig = serverOptions == null
                ? null
                : NativeConfig.BuildServer(serverOptions);

            _pump = new Thread(RunPump)
            {
                IsBackground = true,
                Name = "Quiche.NET pump",
            };
            _pump.Start();
        }

        /// <summary>
        /// Accepts the next inbound connection, once its handshake
        /// has completed. Only one waiter is required at a time in
        /// this step, but several may wait; they are served in
        /// order. On a dial-only socket (no server options) this
        /// never completes until <paramref name="cancellationToken"/>
        /// fires.
        /// </summary>
        public Task<QuicConnection> AcceptAsync(
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_acceptQueue.Count > 0)
                {
                    return Task.FromResult(_acceptQueue.Dequeue());
                }
                var waiter = new TaskCompletionSource<QuicConnection>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _acceptWaiters.Enqueue(waiter);
                if (cancellationToken.CanBeCanceled)
                {
                    cancellationToken.Register(() =>
                    {
                        lock (_gate)
                        {
                            // Removal is best-effort: if the pump
                            // already completed this waiter, the
                            // queue no longer holds it and
                            // TrySetCanceled is a harmless no-op.
                            RemoveWaiter(waiter);
                        }
                        waiter.TrySetCanceled(cancellationToken);
                    });
                }
                return waiter.Task;
            }
        }

        /// <summary>
        /// Dials <paramref name="peerEndPoint"/> from this socket's
        /// local port and returns the connection once its handshake
        /// has completed. <paramref name="serverName"/> is the TLS
        /// SNI (and, when verification is on, the name the peer's
        /// certificate must match). With <paramref name="options"/>
        /// null, the socket's default client options apply (ALPN
        /// "quiche-dotnet", verification on, 30 s idle timeout);
        /// explicit options build a one-off config for this
        /// connection.
        /// </summary>
        public Task<QuicConnection> ConnectAsync(IPEndPoint peerEndPoint,
            string serverName, QuicClientOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (peerEndPoint.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new NotSupportedException(
                    $"Only IPv4 endpoints are supported in this step (got {peerEndPoint.AddressFamily}).");
            }
            if (string.IsNullOrEmpty(serverName))
            {
                throw new QuicException("serverName is required.");
            }
            unsafe
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    NativeConfig config;
                    bool ownConfig = false;
                    QuicConnection? connection = null;
                    if (options == null)
                    {
                        config = _sharedClientConfig;
                    }
                    else
                    {
                        config = NativeConfig.BuildClient(options);
                        ownConfig = true;
                    }
                    try
                    {
                        byte[] scid = new byte[CidLength];
                        using (RandomNumberGenerator rng =
                            RandomNumberGenerator.Create())
                        {
                            rng.GetBytes(scid);
                        }
                        byte[] peerSockaddr =
                            SockaddrUtil.BuildIpv4(peerEndPoint);
                        // Server name as a null-terminated UTF-8
                        // string, pinned alongside the other call
                        // arguments. (Utf8Util.WithUtf8 cannot serve
                        // here: its generic type argument would be
                        // Connection*, and C# forbids pointer types
                        // as type arguments entirely.)
                        byte[] nameBytes =
                            Encoding.UTF8.GetBytes(serverName);
                        byte[] nameNul = new byte[nameBytes.Length + 1];
                        Array.Copy(nameBytes, 0, nameNul, 0,
                            nameBytes.Length);
                        Connection* handle;
                        fixed (byte* namePtr = nameNul)
                        fixed (byte* scidPtr = scid)
                        fixed (byte* localPtr = _localSockaddr)
                        fixed (byte* peerPtr = peerSockaddr)
                        {
                            handle = NativeMethods.quiche_connect(namePtr,
                                scidPtr, (nuint)scid.Length,
                                (sockaddr*)localPtr,
                                SockaddrUtil.SockaddrInLength,
                                (sockaddr*)peerPtr,
                                SockaddrUtil.SockaddrInLength,
                                config.Handle);
                        }
                        if (handle == null)
                        {
                            throw new QuicException(
                                "quiche_connect failed (check ALPN and options).");
                        }
                        connection = new QuicConnection(this, handle,
                            LocalEndPoint, peerEndPoint, isServer: false,
                            scid, ownConfig ? config : null);
                        Register(connection);
                        Drain(connection);
                        var handshake =
                            new TaskCompletionSource<QuicConnection>(
                                TaskCreationOptions.RunContinuationsAsynchronously);
                        connection.SetHandshakeWaiter(handshake);
                        if (cancellationToken.CanBeCanceled)
                        {
                            cancellationToken.Register(() =>
                            {
                                handshake.TrySetCanceled(cancellationToken);
                                connection.Close();
                            });
                        }
                        return handshake.Task;
                    }
                    catch
                    {
                        // Setup failed after registration (nearly
                        // impossible: Drain and waiter setup do not
                        // throw) — do not leak a registered, live
                        // connection or its one-off config.
                        connection?.Close();
                        if (ownConfig)
                        {
                            config.Dispose();
                        }
                        throw;
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                _udp.Close();
            }
            catch
            {
                // Already closed or mid-pump: disposal continues.
            }
            List<TaskCompletionSource<QuicConnection>> waiters;
            List<QuicConnection> connections;
            lock (_gate)
            {
                waiters = new List<TaskCompletionSource<QuicConnection>>(
                    _acceptWaiters);
                _acceptWaiters.Clear();
                _acceptQueue.Clear();
                connections = new List<QuicConnection>(_connections);
                _connections.Clear();
                _byCid.Clear();
            }
            foreach (var waiter in waiters)
            {
                waiter.TrySetException(new ObjectDisposedException(
                    nameof(QuicSocket)));
            }
            // Freeing happens after the pump is unblocked (the socket
            // close above ends its receive loop); any pump pass
            // already inside the gate finishes first because freeing
            // here also takes the gate per connection.
            foreach (var connection in connections)
            {
                connection.DisposeNative();
            }
            lock (_gate)
            {
                _sharedClientConfig.Dispose();
                _serverConfig?.Dispose();
            }
        }

        internal void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(QuicSocket));
            }
        }

        /// <summary>
        /// Sends everything quiche has queued for a connection.
        /// Called with _gate held.
        /// </summary>
        internal unsafe void Drain(QuicConnection connection)
        {
            if (connection.NativeHandle == IntPtr.Zero)
            {
                return;
            }
            Connection* handle =
                (Connection*)connection.NativeHandle.ToPointer();
            fixed (byte* outPtr = _sendBuffer)
            {
                while (true)
                {
                    SendInfo info = default;
                    nint n = NativeMethods.quiche_conn_send(handle, outPtr,
                        (nuint)_sendBuffer.Length, &info);
                    if (n == NativeErrors.Done)
                    {
                        return;
                    }
                    if (n < 0)
                    {
                        return;
                    }
                    // info is a stack local, so its address is
                    // stable without fixed; only heap arrays need
                    // pinning.
                    IPEndPoint? destination;
                    sockaddr_storage* toPtr = &info.to;
                    destination = SockaddrUtil.ParseIpv4(
                        (byte*)toPtr, info.to_len);
                    if (destination == null)
                    {
                        // Non-IPv4 destination: cannot happen on an
                        // IPv4-only socket; drop rather than crash
                        // the pump. QUIC retransmits what matters.
                        continue;
                    }
                    try
                    {
                        _udp.SendTo(_sendBuffer, 0, (int)n,
                            SocketFlags.None, destination);
                    }
                    catch (SocketException)
                    {
                        // Send buffer full or socket gone: drop this
                        // packet; loss recovery retransmits it.
                        return;
                    }
                }
            }
        }

        /// <summary>Removes a connection from the tables (_gate held).</summary>
        internal void Forget(QuicConnection connection)
        {
            _byCid.Remove(connection.CidKey);
            _connections.Remove(connection);
        }

        /// <summary>
        /// Enqueues an established server connection for AcceptAsync
        /// (_gate held).
        /// </summary>
        internal void EnqueueAccepted(QuicConnection connection)
        {
            if (_acceptWaiters.Count > 0)
            {
                _acceptWaiters.Dequeue().TrySetResult(connection);
            }
            else
            {
                _acceptQueue.Enqueue(connection);
            }
        }

        private void RemoveWaiter(
            TaskCompletionSource<QuicConnection> waiter)
        {
            // Queue<T> has no remove; rebuild without the waiter.
            // Waiter queues are tiny (usually 0 or 1), so this is fine.
            if (_acceptWaiters.Count == 0)
            {
                return;
            }
            var kept = new Queue<TaskCompletionSource<QuicConnection>>();
            while (_acceptWaiters.Count > 0)
            {
                var candidate = _acceptWaiters.Dequeue();
                if (!ReferenceEquals(candidate, waiter))
                {
                    kept.Enqueue(candidate);
                }
            }
            while (kept.Count > 0)
            {
                _acceptWaiters.Enqueue(kept.Dequeue());
            }
        }

        private void Register(QuicConnection connection)
        {
            _byCid[connection.CidKey] = connection;
            _connections.Add(connection);
        }

        private void RunPump()
        {
            while (!_disposed)
            {
                try
                {
                    // Poll blocks up to 50 ms; a timeout tick drives
                    // quiche timers (retransmit, idle) when the peer
                    // is silent. Datagram bursts skip the tick: loss
                    // timers matter precisely when nothing arrives.
                    // NOTE: Socket.Select mutates its list, so it is
                    // rebuilt on every iteration; reusing one list
                    // would busy-loop after the first timeout.
                    var pollList = new List<Socket> { _udp };
                    Socket.Select(pollList, null, null, PumpPollMs * 1000);
                    if (pollList.Count > 0)
                    {
                        DrainDatagrams();
                    }
                    else
                    {
                        TickTimers();
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    // Transient socket error (e.g. ICMP port
                    // unreachable on a connected-less UDP socket):
                    // the pump survives; quiche loss recovery
                    // handles the missing packets.
                }
            }
        }

        private void DrainDatagrams()
        {
            while (!_disposed)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try
                {
                    n = _udp.ReceiveFrom(_recvBuffer, 0, _recvBuffer.Length,
                        SocketFlags.None, ref remote);
                }
                catch (SocketException ex)
                    when (ex.SocketErrorCode == SocketError.WouldBlock)
                {
                    return;
                }
                if (n <= 0)
                {
                    return;
                }
                var remoteEndPoint = (IPEndPoint)remote;
                if (remoteEndPoint.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }
                try
                {
                    ProcessDatagram(_recvBuffer, n, remoteEndPoint);
                }
                catch
                {
                    // One bad datagram must never kill the pump.
                    // The connection it targeted fails closed through
                    // quiche's own state machine if it was fatal.
                }
            }
        }

        private void TickTimers()
        {
            QuicConnection[] snapshot;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                snapshot = _connections.ToArray();
            }
            foreach (var connection in snapshot)
            {
                lock (_gate)
                {
                    if (_disposed ||
                        connection.NativeHandle == IntPtr.Zero)
                    {
                        continue;
                    }
                    unsafe
                    {
                        NativeMethods.quiche_conn_on_timeout(
                            (Connection*)connection.NativeHandle.ToPointer());
                        Drain(connection);
                        PostProcess(connection);
                    }
                }
            }
        }

        private unsafe void ProcessDatagram(
            byte[] datagram, int length, IPEndPoint remote)
        {
            lock (_gate)
            {
                if (_disposed || length < 6)
                {
                    return;
                }
                QuicConnection? connection = null;
                if ((datagram[0] & 0x80) != 0)
                {
                    // Long header: [flags][version x4][dcid_len][dcid].
                    int dcidLength = datagram[5];
                    if (length < 6 + dcidLength || dcidLength == 0)
                    {
                        return;
                    }
                    byte[] dcid = new byte[dcidLength];
                    Array.Copy(datagram, 6, dcid, 0, dcidLength);
                    int type = (datagram[0] >> 4) & 0x3;
                    string key = ToHex(dcid);
                    _byCid.TryGetValue(key, out connection);
                    if (connection == null && type == 0)
                    {
                        // Initial for an unknown connection: a new
                        // inbound connection. The server connection
                        // ID is the DCID the client chose.
                        if (_serverConfig == null)
                        {
                            return; // Dial-only socket: drop.
                        }
                        byte[] peerSockaddr =
                            SockaddrUtil.BuildIpv4(remote);
                        Connection* handle;
                        fixed (byte* dcidPtr = dcid)
                        fixed (byte* localPtr = _localSockaddr)
                        fixed (byte* peerPtr = peerSockaddr)
                        {
                            handle = NativeMethods.quiche_accept(dcidPtr,
                                (nuint)dcid.Length, null, 0,
                                (sockaddr*)localPtr,
                                SockaddrUtil.SockaddrInLength,
                                (sockaddr*)peerPtr,
                                SockaddrUtil.SockaddrInLength,
                                _serverConfig.Handle);
                        }
                        if (handle == null)
                        {
                            return;
                        }
                        connection = new QuicConnection(this, handle,
                            LocalEndPoint, remote, isServer: true,
                            dcid, ownConfig: null);
                        Register(connection);
                    }
                }
                else
                {
                    // Short header: the DCID is the first keyLength
                    // bytes. All connection IDs are random, so at
                    // most one table entry matches.
                    foreach (var candidate in _connections)
                    {
                        byte[] key = candidate.CidBytes;
                        if (length >= 1 + key.Length &&
                            Matches(datagram, 1, key))
                        {
                            connection = candidate;
                            break;
                        }
                    }
                }
                if (connection == null ||
                    connection.NativeHandle == IntPtr.Zero)
                {
                    return;
                }
                byte[] peerSockaddrForRecv = SockaddrUtil.BuildIpv4(remote);
                nint processed;
                fixed (byte* datagramPtr = datagram)
                fixed (byte* localPtr = _localSockaddr)
                fixed (byte* peerPtr = peerSockaddrForRecv)
                {
                    RecvInfo info = new RecvInfo
                    {
                        from = (sockaddr*)peerPtr,
                        from_len = SockaddrUtil.SockaddrInLength,
                        to = (sockaddr*)localPtr,
                        to_len = SockaddrUtil.SockaddrInLength,
                    };
                    processed = NativeMethods.quiche_conn_recv(
                        (Connection*)connection.NativeHandle.ToPointer(),
                        datagramPtr, (nuint)length, &info);
                }
                if (processed < 0 && processed != NativeErrors.Done)
                {
                    // Invalid packet for a live connection: quiche
                    // keeps the connection state; a fatal error
                    // surfaces as the connection closing, which
                    // PostProcess below observes.
                }
                PostProcess(connection);
                Drain(connection);
            }
        }

        /// <summary>
        /// After feeding or ticking a connection: surface handshake
        /// completion, readable/writable streams, and closure.
        /// Called with _gate held.
        /// </summary>
        internal unsafe void PostProcess(QuicConnection connection)
        {
            if (connection.NativeHandle == IntPtr.Zero)
            {
                return;
            }
            Connection* handle =
                (Connection*)connection.NativeHandle.ToPointer();
            if (!connection.HandshakeComplete &&
                NativeMethods.quiche_conn_is_established(handle))
            {
                connection.MarkEstablished();
                if (connection.IsServer)
                {
                    EnqueueAccepted(connection);
                }
                else
                {
                    connection.CompleteHandshake();
                }
            }
            // Readable streams: new peer-initiated streams join the
            // accept queue; all readable streams deliver bytes.
            StreamIter* readable = NativeMethods.quiche_conn_readable(handle);
            if (readable != null)
            {
                try
                {
                    ulong streamId;
                    while (NativeMethods.quiche_stream_iter_next(
                        readable, &streamId))
                    {
                        QuicStream? stream = connection.FindStream(streamId);
                        if (stream == null)
                        {
                            stream = connection.CreateInboundStream(streamId);
                            connection.EnqueueAcceptedStream(stream);
                        }
                        ReadAvailable(connection, stream);
                    }
                }
                finally
                {
                    NativeMethods.quiche_stream_iter_free(readable);
                }
            }
            // Writable streams: wake senders waiting on flow control.
            StreamIter* writable = NativeMethods.quiche_conn_writable(handle);
            if (writable != null)
            {
                try
                {
                    ulong streamId;
                    while (NativeMethods.quiche_stream_iter_next(
                        writable, &streamId))
                    {
                        QuicStream? stream = connection.FindStream(streamId);
                        stream?.OnWritable();
                    }
                }
                finally
                {
                    NativeMethods.quiche_stream_iter_free(writable);
                }
            }
            if (NativeMethods.quiche_conn_is_closed(handle))
            {
                QuicException error = GetConnectionError(handle);
                connection.OnClosed(error);
                Forget(connection);
                connection.DisposeNative();
            }
        }

        private unsafe void ReadAvailable(
            QuicConnection connection, QuicStream stream)
        {
            if (connection.NativeHandle == IntPtr.Zero)
            {
                return;
            }
            Connection* handle =
                (Connection*)connection.NativeHandle.ToPointer();
            byte[] buffer = new byte[8192];
            fixed (byte* bufferPtr = buffer)
            {
                while (true)
                {
                    bool fin = false;
                    ulong errorCode = 0;
                    nint n = NativeMethods.quiche_conn_stream_recv(handle,
                        stream.Id, bufferPtr, (nuint)buffer.Length,
                        &fin, &errorCode);
                    if (n > 0)
                    {
                        byte[] chunk = new byte[(int)n];
                        Array.Copy(buffer, chunk, (int)n);
                        stream.OnData(chunk, fin);
                        if (fin)
                        {
                            return;
                        }
                    }
                    else if (n == 0 && fin)
                    {
                        stream.OnData(Array.Empty<byte>(), fin: true);
                        return;
                    }
                    else if (n == NativeErrors.StreamReset ||
                        n == NativeErrors.StreamStopped)
                    {
                        stream.OnReset(new QuicException(
                            $"Stream {stream.Id} reset by peer (quiche error {n}).",
                            n, fromPeer: true));
                        return;
                    }
                    else
                    {
                        // DONE (no more data right now) or another
                        // non-fatal condition: stop draining.
                        return;
                    }
                }
            }
        }

        internal static unsafe QuicException GetConnectionError(
            Connection* handle)
        {
            bool isApp = false;
            ulong code = 0;
            byte* reasonPtr = null;
            nuint reasonLen = 0;
            bool peer = NativeMethods.quiche_conn_peer_error(handle, &isApp,
                &code, &reasonPtr, &reasonLen);
            string side = "peer";
            if (!peer)
            {
                bool local = NativeMethods.quiche_conn_local_error(handle,
                    &isApp, &code, &reasonPtr, &reasonLen);
                if (!local)
                {
                    return new QuicException("Connection closed.");
                }
                side = "local";
            }
            string reason = string.Empty;
            if (reasonPtr != null && reasonLen > 0)
            {
                byte[] bytes = new byte[(int)reasonLen];
                Marshal.Copy((IntPtr)reasonPtr, bytes, 0, bytes.Length);
                reason = Encoding.UTF8.GetString(bytes);
            }
            string message = string.IsNullOrEmpty(reason)
                ? $"Connection closed by {side} (error {code})."
                : $"Connection closed by {side} (error {code}): {reason}";
            return new QuicException(message, (long)code, fromPeer: peer);
        }

        private static bool Matches(byte[] datagram, int offset, byte[] key)
        {
            for (int i = 0; i < key.Length; i++)
            {
                if (datagram[offset + i] != key[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static string ToHex(byte[] bytes)
        {
            char[] chars = new char[bytes.Length * 2];
            const string hex = "0123456789ABCDEF";
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = hex[bytes[i] >> 4];
                chars[i * 2 + 1] = hex[bytes[i] & 0xF];
            }
            return new string(chars);
        }
    }
}
