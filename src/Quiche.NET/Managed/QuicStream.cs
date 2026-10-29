// A bidirectional QUIC stream on a QuicConnection. Sends go through
// quiche's flow control (a send that cannot proceed waits for the
// pump to report the stream writable); receives are delivered by
// the pump into a buffer that ReadAsync drains. FIN handling is
// explicit: SendAsync takes a fin flag, ReadAsync reports it.
//
// Gate discipline: all quiche calls and all buffer/TCS state changes
// happen with the socket's gate held; waits happen outside it (see
// QuicSocket's threading contract).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Quiche.Native;

namespace Quiche
{
    /// <summary>
    /// Data returned by <see cref="QuicStream.ReadAsync"/>: the next
    /// available bytes, and whether they end the stream (FIN).
    /// A result with an empty <see cref="Data"/> and
    /// <see cref="Fin"/> true means the stream ended with no more
    /// data; further reads keep returning that.
    /// </summary>
    public readonly struct QuicReadResult
    {
        public QuicReadResult(byte[] data, bool fin)
        {
            Data = data;
            Fin = fin;
        }

        public byte[] Data { get; }

        public bool Fin { get; }
    }

    /// <summary>
    /// One bidirectional stream on a <see cref="QuicConnection"/>.
    /// Created by <see cref="QuicConnection.OpenStream"/> (outbound)
    /// or <see cref="QuicConnection.AcceptStreamAsync"/> (inbound).
    /// </summary>
    public sealed class QuicStream
    {
        private readonly QuicConnection _connection;
        private readonly List<byte> _receiveBuffer = new List<byte>();
        private TaskCompletionSource<object?>? _dataWaiter;
        private TaskCompletionSource<object?>? _writableWaiter;
        private bool _readEof;
        private bool _writeFinished;
        private QuicException? _closedError;

        internal QuicStream(QuicConnection connection, ulong id)
        {
            _connection = connection;
            Id = id;
        }

        /// <summary>Stream ID (see RFC 9000 §2.1 for the numbering).</summary>
        public ulong Id { get; }

        /// <summary>The connection this stream belongs to.</summary>
        public QuicConnection Connection => _connection;

        /// <summary>
        /// Sends <paramref name="data"/>, looping over flow-control
        /// windows as needed (a send that cannot proceed waits for
        /// the pump to report the stream writable). When
        /// <paramref name="fin"/> is true, a zero-length FIN frame
        /// ends the stream's write side; sending after FIN throws.
        /// Each chunk is drained to the socket immediately, except
        /// the final chunk when a FIN follows: the FIN send's own
        /// drain carries both, so a small message goes out as one
        /// packet instead of two. Without the intermediate drains,
        /// the peer would never receive data, never acknowledge it,
        /// and the flow-control window would never reopen.
        /// Receivers must still handle split delivery (QUIC may
        /// split a message at any boundary): ReadAsync is a
        /// streaming read, not a message read.
        /// </summary>
        // CS9123: &errorCode/&dummy below take addresses of locals
        // in an async method. The addresses never survive an await:
        // each is passed to a synchronous quiche call and used only
        // for its duration, before any await point. The warning is
        // therefore suppressed for this method only.
#pragma warning disable CS9123
        public async Task SendAsync(byte[] data, bool fin = false,
            CancellationToken cancellationToken = default)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            int offset = 0;
            while (offset < data.Length)
            {
                Task? waitTask = null;
                lock (_connection.SocketGate())
                {
                    ThrowIfUnusableLocked();
                    if (_writeFinished)
                    {
                        throw new QuicException(
                            "Stream write side is already finished.");
                    }
                    unsafe
                    {
                        if (_connection.NativeHandle == IntPtr.Zero)
                        {
                            throw new QuicException("Connection is closed.");
                        }
                        Connection* handle =
                            (Connection*)_connection.NativeHandle.ToPointer();
                        nint written;
                        fixed (byte* dataPtr = data)
                        {
                            ulong errorCode = 0;
                            written = NativeMethods.quiche_conn_stream_send(
                                handle, Id, dataPtr + offset,
                                (nuint)(data.Length - offset),
                                false, &errorCode);
                        }
                        if (written > 0)
                        {
                            offset += (int)written;
                            // Skip the drain on the final chunk when
                            // a FIN follows: the FIN send below
                            // drains once for both, so the message
                            // goes out as one packet. Draining here
                            // instead would put data and FIN in
                            // separate packets, and a receiver that
                            // reads between them legitimately sees
                            // data without FIN yet.
                            bool finalChunkWithFin =
                                fin && offset == data.Length;
                            if (!finalChunkWithFin)
                            {
                                _connection.SocketDrainLocked();
                            }
                            continue;
                        }
                        if (written < 0)
                        {
                            throw new QuicException(
                                $"Stream send failed (quiche error {written}).",
                                (long)written, fromPeer: false);
                        }
                        // written == 0: no flow-control window.
                        // Wait outside the gate for writability.
                        _writableWaiter ??=
                            new TaskCompletionSource<object?>(
                                TaskCreationOptions.RunContinuationsAsynchronously);
                        waitTask = _writableWaiter.Task;
                    }
                }
                // Outside the gate: wait for the pump to report the
                // stream writable, or for cancellation.
                if (waitTask != null)
                {
                    using (cancellationToken.Register(() =>
                        _writableWaiter?.TrySetCanceled(cancellationToken)))
                    {
                        await waitTask.ConfigureAwait(false);
                    }
                }
            }
            if (fin)
            {
                lock (_connection.SocketGate())
                {
                    ThrowIfUnusableLocked();
                    if (_writeFinished)
                    {
                        throw new QuicException(
                            "Stream write side is already finished.");
                    }
                    unsafe
                    {
                        if (_connection.NativeHandle == IntPtr.Zero)
                        {
                            throw new QuicException("Connection is closed.");
                        }
                        Connection* handle =
                            (Connection*)_connection.NativeHandle.ToPointer();
                        // Zero-length FIN: the pointer must be valid
                        // even though no bytes are read, so use a
                        // dummy byte rather than an empty array.
                        // This drain also carries any final data
                        // chunk whose drain was skipped above.
                        byte dummy = 0;
                        ulong errorCode = 0;
                        nint sent = NativeMethods.quiche_conn_stream_send(
                            handle, Id, &dummy, 0, true, &errorCode);
                        try
                        {
                            if (sent < 0)
                            {
                                throw new QuicException(
                                    $"Stream FIN failed (quiche error {sent}).",
                                    (long)sent, fromPeer: false);
                            }
                            _writeFinished = true;
                        }
                        finally
                        {
                            _connection.SocketDrainLocked();
                        }
                    }
                }
            }
        }

#pragma warning restore CS9123

        /// <summary>
        /// Reads the next available bytes. Returns immediately when
        /// data is already buffered; otherwise waits for the pump
        /// to deliver some. After FIN is reported, further reads
        /// return empty data with <see cref="QuicReadResult.Fin"/>
        /// true. A connection close with buffered data still
        /// undelivered returns that data first, then throws.
        /// </summary>
        public async Task<QuicReadResult> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            while (true)
            {
                Task? waitTask = null;
                lock (_connection.SocketGate())
                {
                    if (_receiveBuffer.Count > 0)
                    {
                        byte[] data = _receiveBuffer.ToArray();
                        _receiveBuffer.Clear();
                        // FIN is reported with the final bytes; a
                        // later read reports empty + FIN.
                        return new QuicReadResult(data, _readEof);
                    }
                    if (_closedError != null)
                    {
                        throw _closedError;
                    }
                    if (_readEof)
                    {
                        return new QuicReadResult(
                            Array.Empty<byte>(), fin: true);
                    }
                    _dataWaiter ??=
                        new TaskCompletionSource<object?>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                    waitTask = _dataWaiter.Task;
                }
                // Outside the gate: wait for data, FIN, reset, or
                // close, then loop and take whatever is available.
                using (cancellationToken.Register(() =>
                    _dataWaiter?.TrySetCanceled(cancellationToken)))
                {
                    await waitTask!.ConfigureAwait(false);
                }
            }
        }

        // -- Internal pump surface (gate held) --

        internal void OnData(byte[] chunk, bool fin)
        {
            _receiveBuffer.AddRange(chunk);
            if (fin)
            {
                _readEof = true;
            }
            CompleteDataWaiter();
        }

        internal void OnWritable()
        {
            if (_writableWaiter != null)
            {
                var waiter = _writableWaiter;
                _writableWaiter = null;
                waiter.TrySetResult(null);
            }
        }

        internal void OnReset(QuicException error)
        {
            // A reset is an error, not a clean FIN: buffered data is
            // still delivered first (ReadAsync drains the buffer
            // before throwing), then the error surfaces.
            _closedError = error;
            CompleteDataWaiter();
            if (_writableWaiter != null)
            {
                var waiter = _writableWaiter;
                _writableWaiter = null;
                waiter.TrySetException(error);
            }
        }

        internal void OnConnectionClosed(QuicException error)
        {
            _closedError ??= error;
            CompleteDataWaiter();
            if (_writableWaiter != null)
            {
                var waiter = _writableWaiter;
                _writableWaiter = null;
                waiter.TrySetException(error);
            }
        }

        private void CompleteDataWaiter()
        {
            if (_dataWaiter != null)
            {
                var waiter = _dataWaiter;
                _dataWaiter = null;
                waiter.TrySetResult(null);
            }
        }

        private void ThrowIfUnusableLocked()
        {
            if (_closedError != null)
            {
                throw _closedError;
            }
        }
    }
}
