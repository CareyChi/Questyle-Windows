using System.Runtime.InteropServices;

namespace Questyle.Desktop.Services;

// Windows volume of the dongle's active playback endpoint. After Pro2 switches the microphone
// setting, Windows registers a new endpoint for it, which starts at the default 100%.
// Pro2 declares a volume control in both modes, but a new endpoint does not always get it. With it,
// the Windows level is sent to and stored by the dongle; without it, Windows only attenuates the
// samples and the dongle keeps its stored level, so the two levels must not be copied across.
public sealed record DongleVolume(string EndpointId, float Level, bool Hardware)
{
    // Parent device instance of an endpoint, e.g. "{1}.USB\VID_0A12&PID_4007&MI_02\...".
    private static PropertyKey parentInstance = new(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 2);
    // Last level seen while the dongle exposed hardware volume, i.e. the headphone volume.
    private static float? hardwareLevel;

    public static DongleVolume? Capture()
    {
        var endpoint = FindActive(_ => true);
        if (endpoint == null) return null;
        var volume = Volume(endpoint.Value.Device);
        var captured = new DongleVolume(endpoint.Value.Id, volume.GetMasterVolumeLevelScalar(), (volume.QueryHardwareSupport() & 1) != 0);
        if (captured.Hardware) hardwareLevel = captured.Level;
        return captured;
    }

    // Software endpoint: full scale keeps the loudness at the headphone volume set before.
    // Hardware endpoint: the last headphone volume, so the default 100% is not sent to it.
    public async Task ApplyToNewEndpointAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var endpoint = FindActive(id => id != EndpointId);
            if (endpoint != null)
            {
                var volume = Volume(endpoint.Value.Device);
                float? level = (volume.QueryHardwareSupport() & 1) != 0 ? hardwareLevel : 1f;
                var context = Guid.Empty;
                if (level.HasValue) volume.SetMasterVolumeLevelScalar(level.Value, ref context);
                return;
            }
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("系统音量未恢复，请手动调整音量。");
            await Task.Delay(300);
        }
    }

    private static (string Id, IMMDevice Device)? FindActive(Func<string, bool> accept)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        enumerator.EnumAudioEndpoints(0, 1, out var devices); // eRender, DEVICE_STATE_ACTIVE
        devices.GetCount(out int count);
        for (int index = 0; index < count; index++)
        {
            devices.Item(index, out var device);
            device.GetId(out var id);
            device.OpenPropertyStore(0, out var store);
            store.GetValue(ref parentInstance, out var value);
            string parent = value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? "" : ""; // VT_LPWSTR
            PropVariantClear(ref value);
            bool dongle = parent.Contains("VID_0A12&PID_4007", StringComparison.OrdinalIgnoreCase) ||
                          parent.Contains("VID_0E6A&PID_4007", StringComparison.OrdinalIgnoreCase);
            if (dongle && accept(id)) return (id, device);
        }
        return null;
    }

    private static IAudioEndpointVolume Volume(IMMDevice device)
    {
        var iid = typeof(IAudioEndpointVolume).GUID;
        device.Activate(ref iid, 1, IntPtr.Zero, out var volume); // CLSCTX_INPROC_SERVER
        return (IAudioEndpointVolume)volume;
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, int id) { public Guid Format = format; public int Id = id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant { public ushort Type, Reserved1, Reserved2, Reserved3; public IntPtr Pointer, Extra; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator { void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices); }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection { void GetCount(out int count); void Item(int index, out IMMDevice device); }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        void OpenPropertyStore(int access, out IPropertyStore store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out int count);
        void GetAt(int index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr notify);
        void UnregisterControlChangeNotify(IntPtr notify);
        uint GetChannelCount();
        void SetMasterVolumeLevel(float decibels, ref Guid context);
        void SetMasterVolumeLevelScalar(float level, ref Guid context);
        float GetMasterVolumeLevel();
        float GetMasterVolumeLevelScalar();
        void SetChannelVolumeLevel(uint channel, float decibels, ref Guid context);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        float GetChannelVolumeLevel(uint channel);
        float GetChannelVolumeLevelScalar(uint channel);
        void SetMute(bool mute, ref Guid context);
        bool GetMute();
        void GetVolumeStepInfo(out uint step, out uint count);
        void VolumeStepUp(ref Guid context);
        void VolumeStepDown(ref Guid context);
        uint QueryHardwareSupport(); // ENDPOINT_HARDWARE_SUPPORT_VOLUME = 1
    }
}
