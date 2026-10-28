using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Questyle.Desktop.Services;

public sealed record HidDevice(string Path, string Name, ushort Vendor, ushort Product,
    ushort UsagePage, ushort Usage, ushort InputLength, ushort OutputLength, byte InputReportId, byte OutputReportId)
{
    public string Description => $"{Name}  ·  {Vendor:X4}:{Product:X4}  ·  Usage {UsagePage:X4}:{Usage:X4}  ·  IN {InputLength}({InputReportId:X2}) / OUT {OutputLength}({OutputReportId:X2})";
    public override string ToString() => Description;
}

public static class HidTransport
{

    public static IReadOnlyList<HidDevice> Enumerate()
    {
        Native.HidD_GetHidGuid(out var guid);
        var info = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (info == new IntPtr(-1)) throw new Win32Exception();
        var devices = new List<HidDevice>();
        try
        {
            for (uint i = 0; ; i++)
            {
                var iface = new Native.InterfaceData { Size = Marshal.SizeOf<Native.InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(info, IntPtr.Zero, ref guid, i, ref iface))
                {
                    if (Marshal.GetLastWin32Error() == 259) break;
                    throw new Win32Exception();
                }
                Native.SetupDiGetDeviceInterfaceDetail(info, ref iface, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(info, ref iface, buffer, required, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(buffer + 4)!;
                    // The Android application recognizes exactly these two VID/PID pairs.
                    if (!(path.Contains("vid_0a12&pid_4007", StringComparison.OrdinalIgnoreCase) ||
                          path.Contains("vid_0e6a&pid_4007", StringComparison.OrdinalIgnoreCase))) continue;
                    using var handle = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid) continue;
                    var attr = new Native.Attributes { Size = Marshal.SizeOf<Native.Attributes>() };
                    if (!Native.HidD_GetAttributes(handle, ref attr)) continue;
                    var nameBuffer = new byte[256];
                    var name = Native.HidD_GetProductString(handle, nameBuffer, nameBuffer.Length)
                        ? Encoding.Unicode.GetString(nameBuffer).TrimEnd('\0') : "Questyle Dongle";
                    if (!Native.HidD_GetPreparsedData(handle, out var preparsed)) continue;
                    try
                    {
                        if (Native.HidP_GetCaps(preparsed, out var caps) != 0x110000) continue;
                        byte GetReportId(int type, ushort count)
                        {
                            if (count == 0) return 0;
                            var values = new byte[count * 72];
                            if (Native.HidP_GetValueCaps(type, values, ref count, preparsed) != 0x110000) return 0;
                            byte preferred = type == 0 ? (byte)0xBB : (byte)0xCC;
                            return Enumerable.Range(0, count).Any(n => values[n * 72 + 2] == preferred) ? preferred : values[2];
                        }
                        devices.Add(new(path, name, attr.Vendor, attr.Product, caps.UsagePage, caps.Usage, caps.InputLength, caps.OutputLength,
                            GetReportId(0, caps.InputValues), GetReportId(1, caps.OutputValues)));
                    }
                    finally { Native.HidD_FreePreparsedData(preparsed); }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(info); }
        return devices;
    }

    public static async Task WaitForControlInterfaceAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        do
        {
            await Task.Delay(400);
            var devices = await Task.Run(Enumerate);
            if (devices.Any(device => device.InputReportId == 0xbb)) return;
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException("USB 接口尚未恢复，请重新读取设备。");
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData { internal int Size; internal Guid Guid; internal int Flags; internal UIntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] internal struct Attributes { internal int Size; internal ushort Vendor, Product, Version; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Caps
        {
            internal ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] internal ushort[] Reserved;
            internal ushort LinkNodes, InputButtons, InputValues, InputData, OutputButtons, OutputValues, OutputData, FeatureButtons, FeatureValues, FeatureData;
        }
        [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetAttributes(SafeFileHandle h, ref Attributes attr);
        [DllImport("hid.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buffer, int size);
        [DllImport("hid.dll")][return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")][return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
        [DllImport("hid.dll")] internal static extern int HidP_GetValueCaps(int type, [Out] byte[] caps, ref ushort count, IntPtr data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr info, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr info, ref InterfaceData iface, IntPtr detail, uint size, out uint required, IntPtr device);
        [DllImport("setupapi.dll")] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    }
}
