// Resolves the "quiche" native library for the generated P/Invoke
// layer, so a NuGet consumer needs no native setup: the package ships
// the matrix-built binaries under runtimes/<rid>/native/, and this
// loader finds the one matching the current runtime.
//
// Search order for the library file:
//   1. $QUICHE_NATIVE_PATH (a file, or a directory holding the file).
//      Developer escape hatch: point tests or a local build at a
//      specific binary without repacking anything.
//   2. runtimes/<current-RID>/native/ next to this assembly (the
//      NuGet layout).
//   3. Any runtimes/*/native/ next to this assembly (first loadable
//      match wins; normally there is exactly one RID in a publish).
//   4. The assembly directory itself (lets a test stage the library
//      flat next to the test binary).
//
// Notes on targets:
//   * net8.0: registers a DllImport resolver, so plain
//     [DllImport("quiche")] calls resolve through the search above.
//   * netstandard2.0: NativeLibrary does not exist there, so
//     EnsureLoaded is a no-op and resolution is left to the host:
//     a real NuGet consumer's host resolves runtimes/<rid>/native/
//     via AssemblyDependencyResolver. The test project targets net8.0
//     and therefore always gets the resolver path.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Quiche.Native
{
    internal static class NativeLoader
    {
        private const string LibraryName = "quiche";

        /// <summary>Override the search: a file path or a directory.</summary>
        public const string EnvOverride = "QUICHE_NATIVE_PATH";

#if NET8_0_OR_GREATER
        private static readonly object Sync = new object();
        private static bool _resolverRegistered;
#endif

        /// <summary>Idempotent: make sure "quiche" resolves, where possible.</summary>
        public static void EnsureLoaded()
        {
#if NET8_0_OR_GREATER
            lock (Sync)
            {
                if (_resolverRegistered)
                {
                    return;
                }
                NativeLibrary.SetDllImportResolver(
                    typeof(NativeLoader).Assembly, Resolve);
                _resolverRegistered = true;
            }
#else
            // netstandard2.0: no NativeLibrary API; the host resolves
            // runtimes/<rid>/native/ for real consumers (see header).
#endif
        }

#if NET8_0_OR_GREATER
        private static IntPtr Resolve(string libraryName, Assembly assembly,
            DllImportSearchPath? searchPath)
        {
            if (!string.Equals(libraryName, LibraryName,
                    StringComparison.Ordinal))
            {
                return IntPtr.Zero;
            }

            string? fileName = LibraryFileName();
            if (fileName == null)
            {
                return IntPtr.Zero;
            }

            // 1. Explicit override.
            string? overridePath = Environment.GetEnvironmentVariable(EnvOverride);
            if (!string.IsNullOrEmpty(overridePath))
            {
                string candidate = Directory.Exists(overridePath)
                    ? Path.Combine(overridePath, fileName)
                    : overridePath;
                if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
                {
                    return handle;
                }
            }

            string? assemblyDir = null;
            try
            {
                assemblyDir = Path.GetDirectoryName(assembly.Location);
            }
            catch
            {
                // Single-file or otherwise location-less: fall through
                // to default probing by returning Zero.
            }
            if (string.IsNullOrEmpty(assemblyDir))
            {
                return IntPtr.Zero;
            }

            // 2. Exact current RID.
            string rid = RuntimeInformation.RuntimeIdentifier;
            string exact = Path.Combine(assemblyDir, "runtimes", rid,
                "native", fileName);
            if (NativeLibrary.TryLoad(exact, out IntPtr exactHandle))
            {
                return exactHandle;
            }

            // 3. Any RID present (normally exactly one).
            string runtimesDir = Path.Combine(assemblyDir, "runtimes");
            if (Directory.Exists(runtimesDir))
            {
                foreach (string ridDir in Directory.GetDirectories(runtimesDir))
                {
                    string candidate = Path.Combine(ridDir, "native", fileName);
                    if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
                    {
                        return handle;
                    }
                }
            }

            // 4. Flat next to the assembly.
            string flat = Path.Combine(assemblyDir, fileName);
            if (NativeLibrary.TryLoad(flat, out IntPtr flatHandle))
            {
                return flatHandle;
            }

            return IntPtr.Zero;
        }

        private static string? LibraryFileName()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "quiche.dll";
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "libquiche.dylib";
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return "libquiche.so";
            }
            return null;
        }
#endif
    }
}
