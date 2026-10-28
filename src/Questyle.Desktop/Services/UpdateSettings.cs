using System.IO;
using System.Text.Json;

namespace Questyle.Desktop.Services;

public enum UpdateChannel { Stable, Development }

public sealed record UpdateSettings(bool Automatic = true, UpdateChannel Channel = UpdateChannel.Stable)
{
    private static string FilePath => Path.Combine(AppPaths.DataDirectory, "updates.json");
    public static UpdateSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        var settings = JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(FilePath))
            ?? throw new InvalidDataException("更新设置无效");
        if (!Enum.IsDefined(settings.Channel)) throw new InvalidDataException("更新通道无效");
        return settings;
    }
    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
}
