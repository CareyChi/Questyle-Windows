using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Questyle.Desktop.Services;

public sealed record RawUsbDevice(string DeviceId, string InstanceId, ushort Vendor, ushort Product)
{
    public override string ToString() => $"{Vendor:X4}:{Product:X4} {InstanceId}";
}

// UsbDk is used only for short, explicit control sessions. Windows USB audio is
// unavailable while a device is redirected. Always release it in finally/Dispose.
public sealed class UsbDkTransport : IQuestyleTransport, IDisposable
{
    private IntPtr handle;
    private byte inputEndpoint, outputEndpoint;
    public event Action<string>? Log;
    public static string? HelperPath { get; set; }
    static UsbDkTransport()
    {
        NativeLibrary.SetDllImportResolver(typeof(UsbDkTransport).Assembly, (name, assembly, path) =>
            name == "UsbDkHelper.dll" && HelperPath != null ? NativeLibrary.Load(HelperPath) : IntPtr.Zero);
    }

    public static IReadOnlyList<RawUsbDevice> Enumerate()
    {
        if (!Native.UsbDk_GetDevicesList(out var list, out var count)) throw new Win32Exception();
        try
        {
            var result = new List<RawUsbDevice>();
            for (int i = 0; i < count; i++)
            {
                var dev = Marshal.PtrToStructure<Native.DeviceInfo>(list + i * Marshal.SizeOf<Native.DeviceInfo>());
                if ((dev.Descriptor.Vendor == 0x0a12 || dev.Descriptor.Vendor == 0x0e6a) && dev.Descriptor.Product == 0x4007)
                    result.Add(new(dev.Id.Device, dev.Id.Instance, dev.Descriptor.Vendor, dev.Descriptor.Product));
            }
            return result;
        }
        finally { Native.UsbDk_ReleaseDevicesList(list); }
    }

    public static byte[] Configuration(RawUsbDevice device)
    {
        var request = new Native.ConfigRequest { Id = new() { Device = device.DeviceId, Instance = device.InstanceId } };
        if (!Native.UsbDk_GetConfigurationDescriptor(ref request, out var pointer, out var length)) throw new Win32Exception();
        try { var bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, (int)length); return bytes; }
        finally { Native.UsbDk_ReleaseConfigurationDescriptor(pointer); }
    }

    public void Open(RawUsbDevice device)
    {
        Dispose();
        var config = Configuration(device);
        // Same selection as Android s2.h.k: first HID interface with IN and OUT
        // interrupt endpoints. Never select UAC, DFU or vendor bulk endpoints.
        bool hid = false; byte input = 0, output = 0;
        for (int p = 0; p + 2 <= config.Length && config[p] >= 2; p += config[p])
        {
            int length = config[p]; if (p + length > config.Length) break;
            if (config[p + 1] == 4 && length >= 9) { hid = config[p + 5] == 3; input = output = 0; }
            if (hid && config[p + 1] == 5 && length >= 7 && (config[p + 3] & 3) == 3)
            {
                byte endpoint = config[p + 2];
                if ((endpoint & 0x80) != 0) input = endpoint; else output = endpoint;
                if (input != 0 && output != 0) { inputEndpoint = input; outputEndpoint = output; break; }
            }
        }
        if (inputEndpoint == 0 || outputEndpoint == 0) throw new IOException("设备没有可用的 Questyle HID 中断端点");
        var id = new Native.DeviceId { Device = device.DeviceId, Instance = device.InstanceId };
        handle = Native.UsbDk_StartRedirect(ref id);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) { handle = IntPtr.Zero; throw new Win32Exception(Marshal.GetLastWin32Error(), "无法开始 USB 控制会话"); }
    }

    public byte[] Exchange(byte[] packet, TimeSpan timeout)
    {
        if (handle == IntPtr.Zero) throw new IOException("USB 控制会话未打开");
        var request = new byte[packet.Length + 1];
        request[0] = 0xbb; packet.CopyTo(request, 1);
        byte[] firstReply = new byte[64];
        Task<int>? firstRead = Task.Factory.StartNew(() => Transfer(inputEndpoint, firstReply, true, timeout),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        // Keep the IN pipe armed before sending, as the Android reader does.
        Thread.Sleep(40);
        try { Transfer(outputEndpoint, request, false, timeout); }
        catch
        {
            Native.UsbDk_AbortPipe(handle, inputEndpoint);
            try { firstRead.GetAwaiter().GetResult(); } catch { }
            throw;
        }
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var reply = firstRead != null ? firstReply : new byte[64];
            int count = firstRead != null ? firstRead.GetAwaiter().GetResult() : Transfer(inputEndpoint, reply, true, deadline - DateTime.UtcNow);
            firstRead = null;
            Log?.Invoke($"USB IN 0x{inputEndpoint:X2}: {Convert.ToHexString(reply.AsSpan(0, count))}");
            if (count >= 7 && (reply[0] == 0xbb || reply[0] == 0xcc) && reply[1] == 0x51 && reply[2] == 0x4c && reply[3] == packet[2])
                return reply.AsSpan(1, count - 1).ToArray();
        }
        throw new TimeoutException("设备响应超时");
    }

    private int Transfer(byte endpoint, byte[] buffer, bool read, TimeSpan timeout, ulong type = 2)
    {
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        using var signal = new EventWaitHandle(false, EventResetMode.ManualReset);
        var overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<Native.Overlapped>());
        var requestPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Native.TransferRequest>());
        try
        {
            Marshal.StructureToPtr(new Native.Overlapped { Event = signal.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
            var req = new Native.TransferRequest { Endpoint = endpoint, Buffer = pinned.AddrOfPinnedObject(), Length = (ulong)buffer.Length, Type = type };
            Marshal.StructureToPtr(req, requestPtr, false);
            var result = read ? Native.UsbDk_ReadPipe(handle, requestPtr, overlapped) : Native.UsbDk_WritePipe(handle, requestPtr, overlapped);
            if (result == 0) throw new Win32Exception();
            if (result == 2 && !signal.WaitOne(timeout > TimeSpan.Zero ? timeout : TimeSpan.Zero))
            {
                Native.UsbDk_AbortPipe(handle, endpoint);
                // Do not free pinned memory/OVERLAPPED until completion is delivered.
                Native.GetOverlappedResult(Native.UsbDk_GetRedirectorSystemHandle(handle), overlapped, out _, true);
                throw new TimeoutException("USB 传输超时");
            }
            if (result == 2 && !Native.GetOverlappedResult(Native.UsbDk_GetRedirectorSystemHandle(handle), overlapped, out _, false)) throw new Win32Exception();
            req = Marshal.PtrToStructure<Native.TransferRequest>(requestPtr);
            if (req.Status != 0) throw new IOException($"USB 传输失败：0x{req.Status:X}");
            return checked((int)req.Transferred);
        }
        finally { Marshal.FreeHGlobal(requestPtr); Marshal.FreeHGlobal(overlapped); pinned.Free(); }
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero) { Native.UsbDk_StopRedirect(handle); handle = IntPtr.Zero; }
        inputEndpoint = outputEndpoint = 0;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DeviceId
        { [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 200)] internal string Device; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 200)] internal string Instance; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct Descriptor
        { internal byte Length, Type; internal ushort Usb; internal byte Class, Subclass, Protocol, PacketSize; internal ushort Vendor, Product, Version; internal byte Manufacturer, Name, Serial, Configs; }
        [StructLayout(LayoutKind.Sequential)] internal struct DeviceInfo { internal DeviceId Id; internal ulong Filter, Port, Speed; internal Descriptor Descriptor; }
        [StructLayout(LayoutKind.Sequential)] internal struct ConfigRequest { internal DeviceId Id; internal ulong Index; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct TransferRequest
        { internal ulong Endpoint; internal IntPtr Buffer; internal ulong Length, Type, IsoSize; internal IntPtr IsoPackets; internal ulong Transferred, Status; internal IntPtr IsoResults; }
        [StructLayout(LayoutKind.Sequential)] internal struct Overlapped { internal UIntPtr Internal, InternalHigh; internal uint Offset, OffsetHigh; internal IntPtr Event; }
        [DllImport("UsbDkHelper.dll", SetLastError = true)] internal static extern bool UsbDk_GetDevicesList(out IntPtr devices, out uint count);
        [DllImport("UsbDkHelper.dll")] internal static extern void UsbDk_ReleaseDevicesList(IntPtr devices);
        [DllImport("UsbDkHelper.dll", SetLastError = true)] internal static extern bool UsbDk_GetConfigurationDescriptor(ref ConfigRequest request, out IntPtr descriptor, out uint length);
        [DllImport("UsbDkHelper.dll")] internal static extern void UsbDk_ReleaseConfigurationDescriptor(IntPtr descriptor);
        [DllImport("UsbDkHelper.dll", SetLastError = true)] internal static extern IntPtr UsbDk_StartRedirect(ref DeviceId id);
        [DllImport("UsbDkHelper.dll", SetLastError = true)] internal static extern bool UsbDk_StopRedirect(IntPtr device);
        [DllImport("UsbDkHelper.dll", SetLastError = true)] internal static extern int UsbDk_WritePipe(IntPtr device, IntPtr request, IntPtr overlapped);
        [DllImport("UsbDkHelper.dll", SetLastError = true)] internal static extern int UsbDk_ReadPipe(IntPtr device, IntPtr request, IntPtr overlapped);
        [DllImport("UsbDkHelper.dll", SetLastError = true)] internal static extern bool UsbDk_AbortPipe(IntPtr device, ulong endpoint);
        [DllImport("UsbDkHelper.dll", SetLastError = true)] internal static extern IntPtr UsbDk_GetRedirectorSystemHandle(IntPtr device);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetOverlappedResult(IntPtr handle, IntPtr overlapped, out uint count, bool wait);
    }
}
