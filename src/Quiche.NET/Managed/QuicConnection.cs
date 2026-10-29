// A single QUIC connection living on a QuicSocket. All quiche calls
// for this connection happen with the socket's gate held (see
// QuicSocket's threading contract); the methods marked "gate held"
// assume the caller already holds it and must not be called
// otherwise.

using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Quiche.Native;

namespace Quiche
{
    /// <summary>
    /// One QUIC connection on a <see cref="QuicSocket"/>. Open
    /// streams with <see cref="OpenStream"/> (outbound) or accept
    /// peer-initiated streams with <see cref="AcceptStreamAsync"/>.
    /// Close with <see cref="Close"/> (sends CONNECTION_CLOSE) or
    /// <see cref="Dispose"/>; the socket's disposal closes every
    /// connection on it.
    /// </summary>
    public sealed class QuicConnection : IDisposable
    {
        private readonly QuicSocket _socket;
        private readonly Dictionary<ulong, QuicStream> _streams =
            new Dictionary<ulong, QuicStream>();
        private readonly Queue<QuicStream> _acceptQueue =
            new Queue<QuicStream>();
        private readonly Queue<TaskCompletionSource<QuicStream>> _acceptWaiters =
            new Queue<TaskCompletionSource<QuicStream>>();
        private readonly NativeConfig? _ownConfig;

        // The native handle as an IntPtr, not a Connection*: the
        // handle flows through safe code (tables, null checks,
        // disposal), and only the P/Invoke call sites — all already
        // in unsafe contexts — convert it back. This keeps every
        // public method callable from safe code.
        private IntPtr _handlePtr;
        private TaskCompletionSource<QuicConnection>? _handshakeWaiter;
        private ulong _nextBidiId;
        private bool _closed;
        private bool _nativeFreed;

        internal unsafe QuicConnection(QuicSocket socket, Connection* handle,
            IPEndPoint localEndPoint, IPEndPoint remoteEndPoint,
            bool isServer, byte[] cidBytes, NativeConfig? ownConfig)
        {
            _socket = socket;
            _handlePtr = (IntPtr)handle;
            LocalEndPoint = localEndPoint;
            RemoteEndPoint = remoteEndPoint;
            IsServer = isServer;
            CidBytes = cidBytes;
            CidKey = ToHex(cidBytes);
            _ownConfig = ownConfig;
            // Client-initiated bidirectional streams: 0, 4, 8, ...
            // Server-initiated: 1, 5, 9, ... (RFC 9000 §2.1).
            _nextBidiId = isServer ? 1UL : 0UL;
        }

        /// <summary>The local endpoint of the connection.</summary>
        public IPEndPoint LocalEndPoint { get; }

        /// <summary>The peer's endpoint.</summary>
        public IPEndPoint RemoteEndPoint { get; }

        /// <summary>True when the peer dialed this connection.</summary>
        public bool IsServer { get; }

        /// <summary>True once the handshake has completed.</summary>
        public bool IsEstablished => HandshakeComplete;

        /// <summary>True once the connection has closed.</summary>
        public bool IsClosed => _closed;

        /// <summary>
        /// The peer's leaf certificate as DER bytes, or null when
        /// the peer presented none (or the connection is closed and
        /// its native handle is freed). This is the raw certificate
        /// quiche received in the TLS handshake; callers parse it
        /// (e.g., <c>new X509Certificate2(der)</c>) and validate it
        /// for their own identity story — Quiche.NET itself performs
        /// no certificate-to-identity mapping.
        /// Two behaviors callers must know, both measured against
        /// quiche 0.30.0: with verification off, the peer's
        /// certificate is still readable here on the dial side
        /// (the server always presents its certificate); but a
        /// quiche server with verification off never requests a
        /// client certificate, so the server side of a connection
        /// reads null here even when the dialer loaded one.
        /// </summary>
        public byte[]? PeerCertificate
        {
            get
            {
                lock (_socket.Gate)
                {
                    if (_handlePtr == IntPtr.Zero)
                    {
                        return null;
                    }
                    unsafe
                    {
                        Connection* handle =
                            (Connection*)_handlePtr.ToPointer();
                        byte* derPtr = null;
                        nuint derLen = 0;
                        NativeMethods.quiche_conn_peer_cert(
                            handle, &derPtr, &derLen);
                        if (derPtr == null || derLen == 0)
                        {
                            return null;
                        }
                        // The pointer is borrowed from the
                        // connection; copy before returning.
                        byte[] der = new byte[(int)derLen];
                        Marshal.Copy((IntPtr)derPtr, der, 0, (int)derLen);
                        return der;
                    }
                }
            }
        }

        internal IntPtr NativeHandle => _handlePtr;

        internal object SocketGate() => _socket.Gate;

        /// <summary>Drains this connection's queued packets (gate held).</summary>
        internal void SocketDrainLocked() => _socket.Drain(this);

        internal byte[] CidBytes { get; }

        internal string CidKey { get; }

        internal bool HandshakeComplete { get; private set; }

        /// <summary>
        /// Opens a new bidirectional stream initiated by this side.
        /// The stream does not exist on the wire until the first
        /// send; quiche assigns the wire behavior from the ID.
        /// </summary>
        public QuicStream OpenStream()
        {
            lock (_socket.Gate)
            {
                ThrowIfClosed();
                ulong id = _nextBidiId;
                _nextBidiId += 4;
                var stream = new QuicStream(this, id);
                _streams[id] = stream;
                return stream;
            }
        }

        /// <summary>
        /// Accepts the next stream the peer opened. Streams are
        /// served in the order quiche reports them readable.
        /// </summary>
        public Task<QuicStream> AcceptStreamAsync(
            CancellationToken cancellationToken = default)
        {
            lock (_socket.Gate)
            {
                ThrowIfClosed();
                if (_acceptQueue.Count > 0)
                {
                    return Task.FromResult(_acceptQueue.Dequeue());
                }
                var waiter = new TaskCompletionSource<QuicStream>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _acceptWaiters.Enqueue(waiter);
                if (cancellationToken.CanBeCanceled)
                {
                    cancellationToken.Register(() =>
                    {
                        lock (_socket.Gate)
                        {
                            RemoveWaiter(waiter);
                        }
                        waiter.TrySetCanceled(cancellationToken);
                    });
                }
                return waiter.Task;
            }
        }

        /// <summary>
        /// Sends CONNECTION_CLOSE to the peer and frees the
        /// connection. Idempotent. Pending stream operations fail
        /// with a "closed" QuicException.
        /// </summary>
        public void Close()
        {
            QuicException closedError =
                new QuicException("Connection closed.");
            lock (_socket.Gate)
            {
                if (_closed)
                {
                    return;
                }
                _closed = true;
                if (_handlePtr != IntPtr.Zero)
                {
                    unsafe
                    {
                        NativeMethods.quiche_conn_close(
                            (Connection*)_handlePtr.ToPointer(),
                            true, 0, null, 0);
                        _socket.Drain(this);
                    }
                }
                TearDown(closedError);
            }
        }

        public void Dispose()
        {
            Close();
        }

        // -- Internal pump/socket surface (gate held unless noted) --

        internal void SetHandshakeWaiter(
            TaskCompletionSource<QuicConnection> waiter)
        {
            _handshakeWaiter = waiter;
        }

        internal void MarkEstablished()
        {
            HandshakeComplete = true;
        }

        internal void CompleteHandshake()
        {
            _handshakeWaiter?.TrySetResult(this);
            _handshakeWaiter = null;
        }

        internal QuicStream? FindStream(ulong id)
        {
            _streams.TryGetValue(id, out QuicStream? stream);
            return stream;
        }

        internal QuicStream CreateInboundStream(ulong id)
        {
            var stream = new QuicStream(this, id);
            _streams[id] = stream;
            return stream;
        }

        internal void EnqueueAcceptedStream(QuicStream stream)
        {
            if (_acceptWaiters.Count > 0)
            {
                _acceptWaiters.Dequeue().TrySetResult(stream);
            }
            else
            {
                _acceptQueue.Enqueue(stream);
            }
        }

        /// <summary>
        /// Peer or local close observed by the pump (no close packet
        /// to send; the packet that closed us was already processed).
        /// </summary>
        internal void OnClosed(QuicException error)
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
            TearDown(error);
        }

        /// <summary>
        /// Frees the native connection and its one-off config, if
        /// any. Idempotent; takes the gate (safe from both the pump
        /// path, which already holds it — Monitor is reentrant —
        /// and the socket-dispose path, which does not).
        /// </summary>
        internal void DisposeNative()
        {
            lock (_socket.Gate)
            {
                FreeNativeLocked();
            }
        }

        internal void FreeNativeLocked()
        {
            if (_nativeFreed)
            {
                return;
            }
            _nativeFreed = true;
            if (_handlePtr != IntPtr.Zero)
            {
                unsafe
                {
                    NativeMethods.quiche_conn_free(
                        (Connection*)_handlePtr.ToPointer());
                }
                _handlePtr = IntPtr.Zero;
            }
            _ownConfig?.Dispose();
        }

        private void TearDown(QuicException error)
        {
            // Fail pending operations first, while the handle is
            // still valid for any final reads (none needed here).
            _handshakeWaiter?.TrySetException(error);
            _handshakeWaiter = null;
            while (_acceptWaiters.Count > 0)
            {
                _acceptWaiters.Dequeue().TrySetException(error);
            }
            _acceptQueue.Clear();
            foreach (var stream in _streams.Values)
            {
                stream.OnConnectionClosed(error);
            }
            _socket.Forget(this);
            FreeNativeLocked();
        }

        private void ThrowIfClosed()
        {
            if (_closed)
            {
                throw new QuicException("Connection is closed.");
            }
        }

        private void RemoveWaiter(TaskCompletionSource<QuicStream> waiter)
        {
            if (_acceptWaiters.Count == 0)
            {
                return;
            }
            var kept = new Queue<TaskCompletionSource<QuicStream>>();
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
