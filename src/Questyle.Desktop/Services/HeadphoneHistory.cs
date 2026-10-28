using System.IO;
using System.Text.Json;

namespace Questyle.Desktop.Services;

public sealed class HeadphoneHistory(string path)
{
    private readonly List<AudioSink> headphones = [];
    public IReadOnlyList<AudioSink> Headphones => headphones;

    public void Load()
    {
        if (!File.Exists(path)) return;
        var saved = JsonSerializer.Deserialize<AudioSink[]>(File.ReadAllText(path))
            ?? throw new InvalidDataException("耳机记录格式不正确。");
        headphones.Clear();
        headphones.AddRange(saved);
    }

    public void Remember(AudioSink headphone)
    {
        var updated = headphones.ToList();
        var index = updated.FindIndex(item => item.Address.Equals(headphone.Address, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            if (headphone.Name.Length == 0) headphone = headphone with { Name = headphones[index].Name };
            if (headphones[index] == headphone) return;
            updated[index] = headphone;
        }
        else updated.Add(headphone);
        Save(updated);
    }

    public void Remove(string address)
    {
        Save(headphones.Where(item => !item.Address.Equals(address, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    private void Save(List<AudioSink> updated)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
        headphones.Clear(); headphones.AddRange(updated);
    }
}
