using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Questyle.Desktop.Services;

public static class PeqExport
{
    // Wavelet accepts this exact AutoEQ frequency grid.
    private static readonly int[] WaveletFrequencies = [
        20, 21, 22, 23, 24, 26, 27, 29, 30, 32, 34, 36, 38, 40, 43, 45, 48, 50, 53, 56, 59, 63, 66, 70, 74, 78, 83, 87, 92, 97, 103, 109, 115, 121, 128, 136, 143, 151, 160, 169, 178, 188, 199, 210, 222, 235, 248, 262, 277, 292, 309, 326, 345, 364, 385, 406, 429, 453, 479, 506, 534, 565, 596, 630, 665, 703, 743, 784, 829, 875, 924, 977, 1032, 1090, 1151, 1216, 1284, 1357, 1433, 1514, 1599, 1689, 1784, 1885, 1991, 2103, 2221, 2347, 2479, 2618, 2766, 2921, 3086, 3260, 3443, 3637, 3842, 4058, 4287, 4528, 4783, 5052, 5337, 5637, 5955, 6290, 6644, 7018, 7414, 7831, 8272, 8738, 9230, 9749, 10298, 10878, 11490, 12137, 12821, 13543, 14305, 15110, 15961, 16860, 17809, 18812, 19871
    ];

    public static string Wavelet(PeqSettings settings)
    {
        settings.Validate();
        var response = settings with { Enabled = true, Preamp = 0 };
        return "GraphicEQ: " + string.Join("; ", WaveletFrequencies.Select(f =>
            f.ToString(CultureInfo.InvariantCulture) + " " + Math.Max(-120, response.Response(f, 48000)).ToString("0.00", CultureInfo.InvariantCulture))) + Environment.NewLine;
    }

    public static string EasyEffects(PeqSettings settings)
    {
        settings.Validate();
        if (settings.Bands.Length > 32) throw new InvalidDataException("EasyEffects 最多支持 32 段滤波器。");
        var bands = settings.Bands.Length == 0 ? new[] { new PeqBand(false, "PK", 1000, 0, 1) } : settings.Bands;
        var channel = new Dictionary<string, object>();
        for (int i = 0; i < bands.Length; i++)
        {
            var band = bands[i];
            var type = band.Type switch { "PK" => "Bell", "LSC" => "Lo-shelf", "HSC" => "Hi-shelf", "LPQ" => "Lo-pass", "HPQ" => "Hi-pass", "NO" => "Notch", _ => throw new InvalidDataException("不支持的滤波器类型：" + band.Type) };
            channel.Add("band" + i, new { type, mode = "APO (DR)", slope = "x1", solo = false, mute = !settings.BandsEnabled || !band.Enabled, gain = band.Gain, frequency = band.Frequency, q = band.Q, width = 4.0 });
        }
        // Each EasyEffects gain stage accepts -36 to +36 dB.
        double inputGain = Math.Max(-36, settings.Preamp);
        var equalizer = new Dictionary<string, object> {
            ["bypass"] = false, ["input-gain"] = inputGain, ["output-gain"] = settings.Preamp - inputGain,
            ["mode"] = "IIR", ["decramp"] = "Off", ["split-channels"] = false, ["balance"] = 0,
            ["pitch-left"] = 0, ["pitch-right"] = 0, ["num-bands"] = bands.Length,
            ["left"] = channel, ["right"] = channel
        };
        var output = new Dictionary<string, object> { ["blocklist"] = Array.Empty<string>(), ["plugins_order"] = new[] { "equalizer#0" }, ["equalizer#0"] = equalizer };
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["output"] = output }, new JsonSerializerOptions { WriteIndented = true });
    }
}
