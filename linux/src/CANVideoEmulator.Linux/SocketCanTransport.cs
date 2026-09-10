using System.Runtime.InteropServices;
using CANVideoEmulator.Core.Can;

namespace CANVideoEmulator.Linux;

/// <summary>Classical CAN transport through Linux SocketCAN, e.g. PCAN-USB on can0.</summary>
internal sealed partial class SocketCanTransport(string interfaceName) : ICanTransport
{
    private const int AfCan = 29;
    private const int SockRaw = 3;
    private const int CanRaw = 1;
    private const uint SiocGifIndex = 0x8933;
    private const uint CanEffFlag = 0x8000_0000;
    private const uint CanRtrFlag = 0x4000_0000;
    private const uint CanSffMask = 0x0000_07ff;
    private const uint CanEffMask = 0x1fff_ffff;
    private const int EAgain = 11;

    private int _socket = -1;
    private CanTransportStatus _status = CanTransportStatus.NotConnected();

    public string Name => interfaceName;
    public bool IsOpen => _socket >= 0;
    public int? Bitrate => null;
    public CanTransportStatus Status => _status;

    public void Open()
    {
        if (IsOpen) return;
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("SocketCAN is available only on Linux.");
        }

        _socket = Native.Socket(AfCan, SockRaw, CanRaw);
        if (_socket < 0) ThrowLastError("opening SocketCAN socket");

        try
        {
            var request = new IfRequest { Name = interfaceName };
            if (Native.Ioctl(_socket, SiocGifIndex, ref request) < 0)
                ThrowLastError($"finding SocketCAN interface '{interfaceName}'");

            var address = new SockAddrCan
            {
                Family = AfCan,
                InterfaceIndex = request.Index,
                Address = new byte[8],
            };
            if (Native.Bind(_socket, ref address, Marshal.SizeOf<SockAddrCan>()) < 0)
                ThrowLastError($"binding SocketCAN interface '{interfaceName}'");

            _status = CanTransportStatus.Ok($"SocketCAN {interfaceName} open");
        }
        catch
        {
            Close();
            throw;
        }
    }

    public void Close()
    {
        if (!IsOpen) return;
        Native.Close(_socket);
        _socket = -1;
        _status = CanTransportStatus.NotConnected($"SocketCAN {interfaceName} closed");
    }

    public CanSendResult Send(in CanFrame frame)
    {
        if (!IsOpen) return CanSendResult.Failed($"SocketCAN {interfaceName} is not open");

        var nativeFrame = new NativeCanFrame
        {
            CanId = (frame.IsExtended ? frame.CanId & CanEffMask | CanEffFlag : frame.CanId & CanSffMask)
                | (frame.IsRemoteRequest ? CanRtrFlag : 0),
            CanDlc = frame.Dlc,
        };
        frame.Data.CopyTo(nativeFrame.Data);
        var written = Native.Write(_socket, ref nativeFrame, Marshal.SizeOf<NativeCanFrame>());
        if (written == Marshal.SizeOf<NativeCanFrame>()) return CanSendResult.Sent;

        var error = Marshal.GetLastPInvokeError();
        var detail = $"SocketCAN write on {interfaceName} failed: {Marshal.GetPInvokeErrorMessage(error)}";
        _status = new CanTransportStatus(CanTransportHealth.Error, detail, (uint)error);
        return error == EAgain ? CanSendResult.QueueFull(detail) : CanSendResult.Failed(detail);
    }

    public CanTransportStatus RefreshStatus() => _status;
    public void Dispose() => Close();

    private static void ThrowLastError(string operation) =>
        throw new InvalidOperationException($"{operation} failed: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct IfRequest
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string Name;
        public int Index;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockAddrCan
    {
        public ushort Family;
        public int InterfaceIndex;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[]? Address;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct NativeCanFrame
    {
        public uint CanId;
        public byte CanDlc;
        private byte _pad;
        private byte _reserved0;
        private byte _reserved1;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] Data;

        public NativeCanFrame() => Data = new byte[8];
    }

    private static class Native
    {
        [DllImport("libc", SetLastError = true)] public static extern int Socket(int domain, int type, int protocol);
        [DllImport("libc", SetLastError = true)] public static extern int Ioctl(int fd, uint request, ref IfRequest value);
        [DllImport("libc", SetLastError = true)] public static extern int Bind(int fd, ref SockAddrCan address, int length);
        [DllImport("libc", SetLastError = true)] public static extern int Write(int fd, ref NativeCanFrame buffer, int count);
        [DllImport("libc", SetLastError = true)] public static extern int Close(int fd);
    }
}