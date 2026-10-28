using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Questyle.Desktop.Services;

public sealed record PeqBand(bool Enabled, string Type, double Frequency, double Gain, double Q);
public sealed record PeqSettings(bool Enabled, double Preamp, PeqBand[] Bands)
{
    public static readonly string[] Types = ["PK", "LSC", "HSC", "LPQ", "HPQ", "NO"];
    public static PeqSettings Flat => new(false, 0, [.. new double[] { 31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 }.Select(f => new PeqBand(true, "PK", f, 0, 0.70710678))]);

    public void Validate()
    {
        if (!double.IsFinite(Preamp) || Preamp is < -20 or > 10) throw new InvalidDataException("前级增益范围为 -20 至 10 dB。");
        if (Bands.Length > 64) throw new InvalidDataException("最多支持 64 段滤波器。");
        foreach (var b in Bands)
        {
            if (!Types.Contains(b.Type)) throw new InvalidDataException("不支持的滤波器类型：" + b.Type);
            if (!double.IsFinite(b.Frequency) || b.Frequency is < 10 or > 22000) throw new InvalidDataException("频率范围为 10 至 22000 Hz。");
            if (!double.IsFinite(b.Gain) || b.Gain is < -30 or > 30) throw new InvalidDataException("滤波器增益范围为 -30 至 30 dB。");
            if (!double.IsFinite(b.Q) || b.Q is < 0.1 or > 30) throw new InvalidDataException("Q 范围为 0.1 至 30。");
            if (b.Type is "LPQ" or "HPQ" or "NO" && b.Gain != 0) throw new InvalidDataException("低通、高通和陷波滤波器的增益必须为 0。");
        }
    }

    public byte[] ToBytes(long revision)
    {
        Validate();
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(0x31514550u); writer.Write(1u); writer.Write(revision);
        writer.Write(Enabled ? 1 : 0); writer.Write(Bands.Length); writer.Write(Preamp);
        foreach (var b in Bands)
        {
            writer.Write(b.Enabled ? 1 : 0); writer.Write(Array.IndexOf(Types, b.Type));
            writer.Write(b.Frequency); writer.Write(b.Gain); writer.Write(b.Q);
        }
        return stream.ToArray();
    }

    public double Response(double frequency, double rate)
    {
        var bytes = ToBytes(0);
        Marshal.ThrowExceptionForHR(PeqResponse(bytes, bytes.Length, frequency, rate, out var db));
        return db;
    }
    [DllImport("Questyle.Peq.Native.dll", ExactSpelling = true)]
    private static extern int PeqResponse(byte[] data, int size, double frequency, double rate, out double db);

    public double[] ResponseCurve(double[] frequencies, double rate)
    {
        var bytes = ToBytes(0);
        var db = new double[frequencies.Length];
        Marshal.ThrowExceptionForHR(PeqResponseCurve(bytes, bytes.Length, frequencies, frequencies.Length, rate, db));
        return db;
    }
    [DllImport("Questyle.Peq.Native.dll", ExactSpelling = true)]
    private static extern int PeqResponseCurve(byte[] data, int size, double[] frequencies, int count, double rate, [Out] double[] db);

    // Import only effects that can be represented without losing or broadening their meaning.
    public static PeqSettings Parse(string text)
    {
        double preamp = 0; List<PeqBand> bands = [];
        int number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.Split('#')[0].Trim().TrimStart('\uFEFF');
            if (line.Length == 0) continue;
            var pre = Regex.Match(line, @"^Preamp:\s*(\S+)\s+dB\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (pre.Success) { preamp += Number(pre.Groups[1].Value); continue; }
            var match = Regex.Match(line, @"^Filter(?:\s+\d+)?\s*:\s*(ON|OFF)\s+(PK|PEQ|LSC|HSC|LPQ|HPQ|NO)\s+Fc\s+(\S+)\s+Hz(?:\s+Gain\s+(\S+)\s+dB)?\s+Q\s+(\S+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) throw new InvalidDataException($"第 {number} 行包含不支持的指令或滤波格式。");
            var type = match.Groups[2].Value.ToUpperInvariant(); if (type == "PEQ") type = "PK";
            bool gainRequired = type is "PK" or "LSC" or "HSC";
            if (gainRequired != match.Groups[4].Success) throw new InvalidDataException($"第 {number} 行的增益参数不正确。");
            bands.Add(new(match.Groups[1].Value.Equals("ON", StringComparison.OrdinalIgnoreCase), type,
                Number(match.Groups[3].Value), gainRequired ? Number(match.Groups[4].Value) : 0, Number(match.Groups[5].Value)));
        }
        var settings = new PeqSettings(true, preamp, [.. bands]); settings.Validate(); return settings;
    }
    private static double Number(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    public string Export()
    {
        Validate();
        var text = new StringBuilder();
        text.AppendLine(FormattableString.Invariant($"Preamp: {Preamp:R} dB"));
        foreach (var b in Bands)
        {
            string gain = b.Type is "PK" or "LSC" or "HSC" ? FormattableString.Invariant($" Gain {b.Gain:R} dB") : "";
            text.AppendLine(FormattableString.Invariant($"Filter: {(b.Enabled ? "ON" : "OFF")} {b.Type} Fc {b.Frequency:R} Hz{gain} Q {b.Q:R}"));
        }
        return text.ToString();
    }
}
