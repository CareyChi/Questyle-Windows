using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Questyle.Desktop.Services;

public sealed class UiLanguage : INotifyPropertyChanged
{
    public static string[] Codes { get; } = ["zh-CN", "zh-TW", "en-US"];
    public static string[] Names { get; } = ["简体中文", "繁體中文", "English"];
    public static UiLanguage Current { get; } = new();
    private Dictionary<string, string> strings = [];
    public string Code { get; private set; } = "zh-CN";
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;
    private static string SettingsFile => Path.Combine(AppPaths.DataDirectory, "settings.json");

    private UiLanguage()
    {
        var code = File.Exists(SettingsFile) ? JsonSerializer.Deserialize<LanguageSetting>(File.ReadAllText(SettingsFile))!.Language : "zh-CN";
        Load(code);
    }

    public string this[string key]
    {
        get
        {
            if (key.StartsWith("nav:"))
            {
                var label = this[key[4..]];
                return Code == "en-US" ? label : string.Join("\u200A", label.AsEnumerable());
            }
            return strings.TryGetValue(key, out var value) ? value : key;
        }
    }
    public string Format(string key, params object[] values) => string.Format(CultureInfo.CurrentCulture, this[key], values);

    public void Select(string code)
    {
        if (code == Code) return;
        var translations = Read(code);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new LanguageSetting(code)));
        Apply(code, translations);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        Changed?.Invoke();
    }

    private static Dictionary<string, string> Read(string code)
    {
        if (!Codes.Contains(code)) throw new InvalidDataException("Unsupported application language: " + code);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppPaths.LanguageDirectory, code + ".json")))!;
    }
    private void Load(string code) => Apply(code, Read(code));
    private void Apply(string code, Dictionary<string, string> translations)
    {
        strings = translations;
        Code = code;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(code);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentCulture;
    }
    private sealed record LanguageSetting(string Language);
}
