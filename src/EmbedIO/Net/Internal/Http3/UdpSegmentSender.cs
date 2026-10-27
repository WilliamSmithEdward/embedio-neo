#if NET10_0_OR_GREATER
using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EmbedIO.Net.Internal.Http3
{
    // Sends consecutive datagrams of one length to one peer in one system call, which the
    // kernel or the network adapter splits back into datagrams: UDP_SEGMENT through
    // sendmsg on Linux and UDP_SEND_MSG_SIZE through WSASendMsg on Windows, as MsQuic
    // sends. .NET has no API for either.
    internal sealed unsafe partial class UdpSegmentSender(SafeSocketHandle socket)
    {
        // Linux takes at most 64 segments in one send, and an IPv4 datagram carries at most
        // 65,507 bytes; a little less leaves room on both platforms.
        internal const int MaximumSegments = 64;
        internal const int MaximumBytes = 65000;

        // IPPROTO_UDP on Windows and SOL_UDP on Linux, with each platform's option.
        private const int UdpLevel = 17;
        private const int LinuxSegmentOption = 103;
        private const int WindowsSegmentOption = 2;

        // A sender for the socket, or null where the platform cannot segment its sends.
        // The socket becomes non-blocking, so a send that would wait fails instead, and the
        // caller sends those datagrams through the socket's own asynchronous sends.
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        internal static UdpSegmentSender? Create(Socket socket)
        {
            ArgumentNullException.ThrowIfNull(socket);
            Span<byte> value = stackalloc byte[sizeof(int)];

            try
            {
                // Only a platform that can segment knows the option.
                socket.GetRawSocketOption(
                    UdpLevel,
                    OperatingSystem.IsWindows() ? WindowsSegmentOption : LinuxSegmentOption,
                    value
                );
            }

            catch (SocketException)
            {
                return null;
            }

            socket.Blocking = false;
            return new UdpSegmentSender(socket.SafeHandle);
        }

        // Sends the datagrams in one call: each segmentLength bytes long but the last,
        // which may be shorter. Returns the platform's error, as a SocketError; Linux's EIO,
        // which reports a device that cannot checksum segments, is OperationNotSupported.
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        internal SocketError Send(SocketAddress address, ReadOnlySpan<byte> datagrams, int segmentLength)
        {
            ArgumentNullException.ThrowIfNull(address);
            ArgumentOutOfRangeException.ThrowIfLessThan(segmentLength, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(datagrams.Length, MaximumBytes);
            var added = false;

            try
            {
                socket.DangerousAddRef(ref added);
                var handle = socket.DangerousGetHandle();

                fixed (byte* name = address.Buffer.Span)
                fixed (byte* data = datagrams)
                {
                    var (error, sent) = OperatingSystem.IsWindows()
                        ? SendWindows(handle, name, address.Size, data, datagrams.Length, segmentLength)
                        : SendLinux((int)handle, name, address.Size, data, datagrams.Length, segmentLength);

                    if (error == SocketError.Success && sent != datagrams.Length)
                        throw new InvalidOperationException("Segmented UDP send did not consume its datagrams.");

                    return error;
                }
            }

            finally
            {
                if (added) socket.DangerousRelease();
            }
        }

        private static (SocketError Error, long Sent) SendWindows(
            nint socket,
            byte* name,
            int nameLength,
            byte* data,
            int length,
            int segmentLength
        )
        {
            var buffer = new WindowsBuffer { Length = (uint)length, Pointer = (nint)data };
            var control = new WindowsSegmentControl
            {
                Level = UdpLevel,
                Type = WindowsSegmentOption,
                SegmentLength = (uint)segmentLength
            };

            // WSA_CMSG_LEN(sizeof(DWORD)): the header and the value, without trailing padding.
            control.Length = (nuint)((byte*)&control.SegmentLength - (byte*)&control + sizeof(uint));
            var message = new WindowsMessage
            {
                Name = (nint)name,
                NameLength = nameLength,
                Buffers = (nint)(&buffer),
                BufferCount = 1,
                Control = new WindowsBuffer { Length = (uint)sizeof(WindowsSegmentControl), Pointer = (nint)(&control) }
            };
            uint sent;

            return NativeMethods.WindowsSendMessage(socket, &message, 0, &sent, 0, 0) == 0
                ? (SocketError.Success, sent)
                : ((SocketError)Marshal.GetLastPInvokeError(), 0);
        }

        private static (SocketError Error, long Sent) SendLinux(
            int socket,
            byte* name,
            int nameLength,
            byte* data,
            int length,
            int segmentLength
        )
        {
            var vector = new LinuxVector { Base = (nint)data, Length = (nuint)length };
            var control = new LinuxSegmentControl
            {
                Level = UdpLevel,
                Type = LinuxSegmentOption,
                SegmentLength = (ushort)segmentLength
            };

            // CMSG_LEN(sizeof(__u16)), which the kernel checks exactly.
            control.Length = (nuint)((byte*)&control.SegmentLength - (byte*)&control + sizeof(ushort));
            var message = new LinuxMessage
            {
                Name = (nint)name,
                NameLength = (uint)nameLength,
                Buffers = (nint)(&vector),
                BufferCount = 1,
                Control = (nint)(&control),
                ControlLength = (nuint)sizeof(LinuxSegmentControl)
            };

            while (true)
            {
                var sent = NativeMethods.LinuxSendMessage(socket, &message, 0);

                if (sent >= 0) return (SocketError.Success, sent);

                var error = Marshal.GetLastPInvokeError();

                if (error != LinuxInterrupted) return (LinuxError(error), 0);
            }
        }

        private const int LinuxInterrupted = 4;

        // The errno values sendmsg reports for a UDP socket, the same on every Linux
        // architecture .NET supports.
        private static SocketError LinuxError(int error) =>
            error switch
            {
                5 or 95 => SocketError.OperationNotSupported,
                11 => SocketError.WouldBlock,
                22 => SocketError.InvalidArgument,
                90 => SocketError.MessageSize,
                92 => SocketError.ProtocolOption,
                101 => SocketError.NetworkUnreachable,
                104 => SocketError.ConnectionReset,
                105 => SocketError.NoBufferSpaceAvailable,
                111 => SocketError.ConnectionRefused,
                112 => SocketError.HostDown,
                113 => SocketError.HostUnreachable,
                _ => SocketError.SocketError
            };

        // WSABUF.
        [StructLayout(LayoutKind.Sequential)]
        private struct WindowsBuffer
        {
            internal uint Length;
            internal nint Pointer;
        }

        // WSAMSG.
        [StructLayout(LayoutKind.Sequential)]
        private struct WindowsMessage
        {
            internal nint Name;
            internal int NameLength;
            internal nint Buffers;
            internal uint BufferCount;
            internal WindowsBuffer Control;
            internal uint Flags;
        }

        // A WSACMSGHDR and its DWORD, which starts at the header's natural alignment.
        [StructLayout(LayoutKind.Sequential)]
        private struct WindowsSegmentControl
        {
            internal nuint Length;
            internal int Level;
            internal int Type;
            internal uint SegmentLength;
        }

        // struct iovec.
        [StructLayout(LayoutKind.Sequential)]
        private struct LinuxVector
        {
            internal nint Base;
            internal nuint Length;
        }

        // struct msghdr. musl's 32-bit lengths and padding fill the same bytes as glibc's
        // size_t fields on little-endian 64-bit targets.
        [StructLayout(LayoutKind.Sequential)]
        private struct LinuxMessage
        {
            internal nint Name;
            internal uint NameLength;
            internal nint Buffers;
            internal nuint BufferCount;
            internal nint Control;
            internal nuint ControlLength;
            internal int Flags;
        }

        // A struct cmsghdr and its __u16, which starts at the header's natural alignment.
        [StructLayout(LayoutKind.Sequential)]
        private struct LinuxSegmentControl
        {
            internal nuint Length;
            internal int Level;
            internal int Type;
            internal ushort SegmentLength;
        }

        private static partial class NativeMethods
        {
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [LibraryImport("ws2_32.dll", EntryPoint = "WSASendMsg", SetLastError = true)]
            internal static partial int WindowsSendMessage(
                nint socket,
                WindowsMessage* message,
                uint flags,
                uint* sent,
                nint overlapped,
                nint completion
            );

            // The runtime resolves "libc" to the C library it runs on, glibc's or musl's.
            [LibraryImport("libc", EntryPoint = "sendmsg", SetLastError = true)]
            internal static partial nint LinuxSendMessage(int socket, LinuxMessage* message, int flags);
        }
    }
}
#endif
