using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Questyle.Desktop.Services;

public sealed record DeviceInterfaceChange(bool Arrived, bool IsUsbInterface);

internal sealed class DeviceChangeWatcher : IDisposable
{
    internal static readonly Guid UsbInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    internal static readonly Guid HidInterface = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    private readonly HwndSource source;
    private readonly Action<DeviceInterfaceChange> changed;
    private readonly IntPtr registration;

    public DeviceChangeWatcher(HwndSource source, Action<DeviceInterfaceChange> changed)
    {
        this.source = source; this.changed = changed;
        var filter = new InterfaceFilter { Size = Marshal.SizeOf<InterfaceFilter>(), DeviceType = 5 };
        registration = RegisterDeviceNotification(source.Handle, ref filter, 4);
        if (registration == IntPtr.Zero) throw new Win32Exception();
        source.AddHook(HandleMessage);
    }

    internal static DeviceInterfaceChange? Parse(int eventCode, Guid interfaceClass, string path)
    {
        if (eventCode is not (0x8000 or 0x8004) || (interfaceClass != UsbInterface && interfaceClass != HidInterface)) return null;
        if (!path.Contains("vid_0a12&pid_4007", StringComparison.OrdinalIgnoreCase) && !path.Contains("vid_0e6a&pid_4007", StringComparison.OrdinalIgnoreCase)) return null;
        return new(eventCode == 0x8000, interfaceClass == UsbInterface);
    }

    private IntPtr HandleMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0219 || wParam.ToInt64() is not (0x8000 or 0x8004) || lParam == IntPtr.Zero) return IntPtr.Zero;
        int nameOffset = Marshal.OffsetOf<InterfaceFilter>(nameof(InterfaceFilter.Name)).ToInt32();
        int size = Marshal.ReadInt32(lParam);
        if (size < nameOffset + 2 || Marshal.ReadInt32(lParam, 4) != 5) return IntPtr.Zero;
        var filter = Marshal.PtrToStructure<InterfaceFilter>(lParam);
        var path = Marshal.PtrToStringUni(lParam + nameOffset, (size - nameOffset) / 2)!.TrimEnd('\0');
        var change = Parse(wParam.ToInt32(), filter.ClassGuid, path);
        if (change != null) changed(change);
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        source.RemoveHook(HandleMessage);
        if (!UnregisterDeviceNotification(registration)) throw new Win32Exception();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InterfaceFilter { public int Size, DeviceType, Reserved; public Guid ClassGuid; public char Name; }
    [DllImport("user32.dll", EntryPoint = "RegisterDeviceNotificationW", SetLastError = true)] private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, ref InterfaceFilter filter, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterDeviceNotification(IntPtr handle);
}
