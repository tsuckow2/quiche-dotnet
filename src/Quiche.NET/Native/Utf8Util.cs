// Managed string -> native C string interop. quiche's C API takes
// paths and server names as byte* (C strings); csbindgen faithfully
// emits those as byte*, so callers must convert. This helper pins a
// null-terminated UTF-8 copy for the duration of one call.

using System;
using System.Text;

namespace Quiche.Native
{
    internal static class Utf8Util
    {
        internal unsafe delegate T Utf8Call<T>(byte* ptr);

        public static unsafe T WithUtf8<T>(string value, Utf8Call<T> call)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            byte[] nulTerminated = new byte[bytes.Length + 1];
            Array.Copy(bytes, 0, nulTerminated, 0, bytes.Length);
            fixed (byte* ptr = nulTerminated)
            {
                return call(ptr);
            }
        }
    }
}
