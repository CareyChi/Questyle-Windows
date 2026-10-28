using Microsoft.Win32;
using System.IO;

namespace Questyle.Desktop.Services;

public sealed record PeqEndpoint(string Id, string Name, bool Installed)
{
    public const string Clsid = "{6C4460CE-5428-4C08-97E5-2D9EF8E6B4D1}";
    public const string RenderPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    public const string FxFormat = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},";
    public static string SharedDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Questyle", "peq");

    public static PeqEndpoint? FindActive()
    {
        using var render = Registry.LocalMachine.OpenSubKey(RenderPath)!;
        var found = render.GetSubKeyNames().Select(id => Read(id)).Where(e => e != null).ToArray();
        if (found.Length > 1) throw new InvalidOperationException("检测到多个 Dongle 播放端点，请只保留一个。");
        return found.SingleOrDefault();
    }
    public static PeqEndpoint? Read(string id)
    {
        if (!Guid.TryParseExact(id, "B", out _)) throw new ArgumentException("音频端点标识无效。");
        using var key = Registry.LocalMachine.OpenSubKey(RenderPath + "\\" + id);
        if (key == null || key.GetValue("DeviceState") is not int state || state != 1) return null;
        using var properties = key.OpenSubKey("Properties")!;
        if (properties.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},2") is not string parent || !IsDongle(parent)) return null;
        using var effects = key.OpenSubKey("FxProperties");
        bool installed = string.Equals(effects?.GetValue(FxFormat + "2") as string, Clsid, StringComparison.OrdinalIgnoreCase);
        return new PeqEndpoint(id.ToUpperInvariant(), (string)properties.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6")!, installed);
    }
    public static bool IsDongle(string parent) =>
        parent.Contains("VID_0A12&PID_4007", StringComparison.OrdinalIgnoreCase) || parent.Contains("VID_0E6A&PID_4007", StringComparison.OrdinalIgnoreCase);

    public (long Revision, double Rate, int Error, bool Processing) ReadStatus()
    {
        var path = Path.Combine(SharedDirectory, "status-" + Id + ".bin");
        if (!File.Exists(path)) return (0, 48000, 0, false);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream);
        if (stream.Length != 32) throw new InvalidDataException("PEQ 运行状态无效。");
        long revision = reader.ReadInt64(), heartbeat = reader.ReadInt64();
        int rate = reader.ReadInt32(); reader.ReadInt32(); int error = reader.ReadInt32();
        return (revision, rate, error, heartbeat > 0 && Environment.TickCount64 - heartbeat is >= 0 and < 2000);
    }
}
