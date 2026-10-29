// IPv4 socket-address encoding for the quiche C API, which takes
// sockaddr pointers. quiche itself is address-family agnostic, but
// this step supports IPv4 only: IPv6 parsing differs (16-byte
// address at a different offset), and the managed socket validates
// endpoints up front with a clear NotSupportedException rather
// than mis-parsing an address. IPv6 support is a follow-up step.
//
// Wire layout of a 16-byte sockaddr_in (network byte order where
// noted): [family: u16 LE][port: u16 BE][addr: 4 bytes][zeroes].

using System;
using System.Net;

namespace Quiche.Native
{
    internal static class SockaddrUtil
    {
        public const ushort AfInet = 2;

        public const int SockaddrInLength = 16;

        public static byte[] BuildIpv4(IPEndPoint endPoint)
        {
            if (endPoint.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                throw new NotSupportedException(
                    $"Only IPv4 endpoints are supported in this step (got {endPoint.AddressFamily}).");
            }
            byte[] addr = endPoint.Address.GetAddressBytes();
            byte[] sockaddr = new byte[SockaddrInLength];
            sockaddr[0] = (byte)(AfInet & 0xFF);
            sockaddr[1] = (byte)((AfInet >> 8) & 0xFF);
            sockaddr[2] = (byte)((endPoint.Port >> 8) & 0xFF);
            sockaddr[3] = (byte)(endPoint.Port & 0xFF);
            Array.Copy(addr, 0, sockaddr, 4, 4);
            return sockaddr;
        }

        /// <summary>
        /// Reads an IPv4 endpoint out of the first 16 bytes at a
        /// pointer (used for SendInfo.to, which quiche fills in).
        /// Returns null when the family is not IPv4.
        /// </summary>
        public static unsafe IPEndPoint? ParseIpv4(byte* ptr, int length)
        {
            if (length < SockaddrInLength)
            {
                return null;
            }
            ushort family = (ushort)(ptr[0] | (ptr[1] << 8));
            if (family != AfInet)
            {
                return null;
            }
            int port = (ptr[2] << 8) | ptr[3];
            byte[] addr = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                addr[i] = ptr[4 + i];
            }
            return new IPEndPoint(new IPAddress(addr), port);
        }
    }
}
