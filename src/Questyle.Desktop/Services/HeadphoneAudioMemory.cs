using System.IO;
using System.Text.Json;

namespace Questyle.Desktop.Services;

public sealed record HeadphoneAudioPreference(byte Codec, byte? Ldac, byte? Adaptive);

public sealed class HeadphoneAudioMemory(string path)
{
    private sealed record Saved(Dictionary<string, HeadphoneAudioPreference> Headphones, bool? DisableMicrophone, bool? Auracast);
    private Saved saved = new(new(StringComparer.OrdinalIgnoreCase), null, null);
    public bool? DisableMicrophone => saved.DisableMicrophone;
    public bool? Auracast => saved.Auracast;

    public void Load()
    {
        if (!File.Exists(path)) return;
        var read = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path))
            ?? throw new InvalidDataException("耳机音频设置记录格式不正确。");
        saved = read with { Headphones = new(read.Headphones, StringComparer.OrdinalIgnoreCase) };
    }
    public HeadphoneAudioPreference? Find(string address) => saved.Headphones.GetValueOrDefault(address);

    // Called with a confirmed readback, never with unsent control selections.
    public void RememberConfirmed(DongleSnapshot value, bool? microphoneChoice = null, bool? auracastChoice = null)
    {
        if (!value.HasConnectedHeadphone) throw new InvalidOperationException("请先连接耳机。");
        var previous = Find(value.HeadphoneAddress);
        var codec = DongleController.Codecs.Single(option => option.Name == value.ActiveCodec).Code;
        var preference = new HeadphoneAudioPreference(codec,
            value.ActiveCodec == "LDAC" && value.Ldac is >= 1 and <= 3 ? value.Ldac : previous?.Ldac,
            value.ActiveCodec == "aptX Adaptive" && value.Adaptive is >= 1 and <= 2 ? value.Adaptive : previous?.Adaptive);
        bool lc3 = value.ActiveCodec == "LC3";
        var microphone = microphoneChoice ?? saved.DisableMicrophone ?? (!lc3 ? value.GameMode : null);
        var aura = auracastChoice ?? saved.Auracast ?? (lc3 && value.Auracast is 0 or 1 ? value.Auracast == 1 : (bool?)null);
        var updated = saved with
        {
            Headphones = new(saved.Headphones, StringComparer.OrdinalIgnoreCase) { [value.HeadphoneAddress] = preference },
            DisableMicrophone = microphone, Auracast = aura
        };
        if (previous == preference && saved.DisableMicrophone == microphone && saved.Auracast == aura) return;
        Save(updated);
    }
    public void Remove(string address)
    {
        if (!saved.Headphones.ContainsKey(address)) return;
        var updated = new Dictionary<string, HeadphoneAudioPreference>(saved.Headphones, StringComparer.OrdinalIgnoreCase);
        updated.Remove(address); Save(saved with { Headphones = updated });
    }
    private void Save(Saved updated)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
        saved = updated;
    }
}
