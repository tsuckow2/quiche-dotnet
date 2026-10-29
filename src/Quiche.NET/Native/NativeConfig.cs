// Owns a quiche_config* and builds it from the managed options.
// Lifetime rules (the native object must outlive every connection
// built from it):
//   * Socket-shared configs are freed when the socket is disposed,
//     after the pump has stopped and all connections are freed.
//   * One-off configs (a ConnectAsync with explicit options) are
//     freed when their connection closes.
// All methods on this class are called with the owning socket's gate
// held, and Dispose must likewise be called under that gate. There
// is deliberately no finalizer: freeing a config while a connection
// still references it would be a use-after-free, and the owner that
// knows the lifetime is the socket, not the GC.

using System;
using System.Text;
using Quiche;

namespace Quiche.Native
{
    internal sealed class NativeConfig : IDisposable
    {
        public unsafe Config* Handle { get; private set; }

        private bool _disposed;

        private NativeConfig()
        {
        }

        public static unsafe NativeConfig BuildClient(QuicClientOptions? options)
        {
            options ??= new QuicClientOptions();
            var config = new NativeConfig();
            config.Handle = NativeMethods.quiche_config_new(ProtocolVersion);
            if (config.Handle == null)
            {
                throw new QuicException("quiche_config_new failed");
            }
            try
            {
                ApplyCommon(config.Handle, options.Alpn, options.IdleTimeout);
                NativeMethods.quiche_config_verify_peer(
                    config.Handle, options.VerifyPeer);
                if (!string.IsNullOrEmpty(options.TrustedCaFile))
                {
                    // The null check above proves non-null, but the
                    // netstandard2.0 reference assemblies lack the
                    // annotation that would tell the compiler so.
                    string caFile = options.TrustedCaFile!;
                    int rc = Utf8Util.WithUtf8(caFile, ptr =>
                        NativeMethods.quiche_config_load_verify_locations_from_file(
                            config.Handle, ptr));
                    if (rc != 0)
                    {
                        throw new QuicException(
                            $"Failed to load trusted CA file '{caFile}' (quiche error {rc}).");
                    }
                }
                bool hasCert = !string.IsNullOrEmpty(options.CertificateFile);
                bool hasKey = !string.IsNullOrEmpty(options.PrivateKeyFile);
                if (hasCert != hasKey)
                {
                    throw new QuicException(
                        "QuicClientOptions.CertificateFile and PrivateKeyFile must be set together.");
                }
                if (hasCert)
                {
                    int rc = Utf8Util.WithUtf8(options.CertificateFile!, ptr =>
                        NativeMethods.quiche_config_load_cert_chain_from_pem_file(
                            config.Handle, ptr));
                    if (rc != 0)
                    {
                        throw new QuicException(
                            $"Failed to load certificate chain '{options.CertificateFile}' (quiche error {rc}).");
                    }
                    rc = Utf8Util.WithUtf8(options.PrivateKeyFile!, ptr =>
                        NativeMethods.quiche_config_load_priv_key_from_pem_file(
                            config.Handle, ptr));
                    if (rc != 0)
                    {
                        throw new QuicException(
                            $"Failed to load private key '{options.PrivateKeyFile}' (quiche error {rc}).");
                    }
                }
            }
            catch
            {
                config.Dispose();
                throw;
            }
            return config;
        }

        public static unsafe NativeConfig BuildServer(QuicServerOptions options)
        {
            if (string.IsNullOrEmpty(options.CertificateFile))
            {
                throw new QuicException(
                    "QuicServerOptions.CertificateFile is required.");
            }
            if (string.IsNullOrEmpty(options.PrivateKeyFile))
            {
                throw new QuicException(
                    "QuicServerOptions.PrivateKeyFile is required.");
            }
            var config = new NativeConfig();
            config.Handle = NativeMethods.quiche_config_new(ProtocolVersion);
            if (config.Handle == null)
            {
                throw new QuicException("quiche_config_new failed");
            }
            try
            {
                ApplyCommon(config.Handle, options.Alpn, options.IdleTimeout);
                int rc = Utf8Util.WithUtf8(options.CertificateFile, ptr =>
                    NativeMethods.quiche_config_load_cert_chain_from_pem_file(
                        config.Handle, ptr));
                if (rc != 0)
                {
                    throw new QuicException(
                        $"Failed to load certificate chain '{options.CertificateFile}' (quiche error {rc}).");
                }
                rc = Utf8Util.WithUtf8(options.PrivateKeyFile, ptr =>
                    NativeMethods.quiche_config_load_priv_key_from_pem_file(
                        config.Handle, ptr));
                if (rc != 0)
                {
                    throw new QuicException(
                        $"Failed to load private key '{options.PrivateKeyFile}' (quiche error {rc}).");
                }
            }
            catch
            {
                config.Dispose();
                throw;
            }
            return config;
        }

        public const uint ProtocolVersion = 0x00000001;

        private static unsafe void ApplyCommon(
            Config* handle, string alpn, TimeSpan idleTimeout)
        {
            // ALPN is a length-prefixed list on the wire; quiche
            // expects exactly that encoding here.
            byte[] alpnName = Encoding.ASCII.GetBytes(alpn);
            if (alpnName.Length == 0 || alpnName.Length > 255)
            {
                throw new QuicException(
                    $"ALPN protocol name must be 1-255 ASCII bytes, got {alpnName.Length}.");
            }
            byte[] alpnList = new byte[alpnName.Length + 1];
            alpnList[0] = (byte)alpnName.Length;
            Array.Copy(alpnName, 0, alpnList, 1, alpnName.Length);
            int rc;
            fixed (byte* alpnPtr = alpnList)
            {
                rc = NativeMethods.quiche_config_set_application_protos(
                    handle, alpnPtr, (nuint)alpnList.Length);
            }
            if (rc != 0)
            {
                throw new QuicException(
                    $"Failed to set ALPN '{alpn}' (quiche error {rc}).");
            }

            NativeMethods.quiche_config_set_initial_max_data(
                handle, QuicDefaults.InitialMaxData);
            NativeMethods.quiche_config_set_initial_max_stream_data_bidi_local(
                handle, QuicDefaults.InitialMaxStreamDataBidiLocal);
            NativeMethods.quiche_config_set_initial_max_stream_data_bidi_remote(
                handle, QuicDefaults.InitialMaxStreamDataBidiRemote);
            NativeMethods.quiche_config_set_initial_max_stream_data_uni(
                handle, QuicDefaults.InitialMaxStreamDataUni);
            NativeMethods.quiche_config_set_initial_max_streams_bidi(
                handle, QuicDefaults.InitialMaxStreamsBidi);
            NativeMethods.quiche_config_set_initial_max_streams_uni(
                handle, QuicDefaults.InitialMaxStreamsUni);

            // TimeSpan.Zero disables the timeout (quiche treats 0 as
            // "no timeout"); anything else is milliseconds.
            ulong idleMs = idleTimeout <= TimeSpan.Zero
                ? 0
                : (ulong)idleTimeout.TotalMilliseconds;
            NativeMethods.quiche_config_set_max_idle_timeout(handle, idleMs);
        }

        public unsafe void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                if (Handle != null)
                {
                    NativeMethods.quiche_config_free(Handle);
                    Handle = null;
                }
            }
        }
    }
}
