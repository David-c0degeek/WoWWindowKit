using System.Text.Json;

namespace WoWWindowKit;

public sealed class Slot
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Hotkey { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public Rectangle Bounds => new(X, Y, Width, Height);
}

public sealed class LayoutProfile
{
    public string Name { get; set; } = "";
    public List<Slot> Slots { get; set; } = [];
}

public sealed class AppSettings
{
    public string DisplayName { get; set; } = "Notepad++";
    public string ActiveProfile { get; set; } = "5-man";
    public bool MouseFocus { get; set; } = true;
    public bool Borderless { get; set; } = true;
    public int MousePollMs { get; set; } = 50;
    public List<string> ProcessNames { get; set; } = ["Wow", "WowClassic", "WowClassicT"];
    public List<LayoutProfile> Profiles { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public LayoutProfile Current => Profiles.Single(p => p.Name == ActiveProfile);
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    internal static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");
    internal static AppSettings Load(string path)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Settings are empty.");
        settings.Validate();
        return settings;
    }
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 80) throw new InvalidDataException("App name must contain 1–80 characters.");
        if (MousePollMs < 20 || MousePollMs > 1000) throw new InvalidDataException("Mouse interval must be between 20 and 1000 ms.");
        if (ProcessNames is null || ProcessNames.Count == 0 || ProcessNames.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("At least one WoW process name is required.");
        if (Profiles is null || Profiles.Count == 0 || Profiles.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name))) throw new InvalidDataException("At least one named profile is required.");
        if (Profiles.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Profiles.Count) throw new InvalidDataException("Profile names must be unique.");
        if (!Profiles.Any(p => p.Name == ActiveProfile)) throw new InvalidDataException("Active profile was not found.");
        foreach (var profile in Profiles)
        {
            if (profile.Slots is null || profile.Slots.Count < 1 || profile.Slots.Count > 10) throw new InvalidDataException("A profile must have 1–10 slots.");
            var bindings = new HashSet<KeyBinding>();
            foreach (var slot in profile.Slots)
            {
                if (slot is null || slot.Width < 100 || slot.Height < 100 || slot.Width > 32768 || slot.Height > 32768 || Math.Abs((long)slot.X) > 100000 || Math.Abs((long)slot.Y) > 100000)
                    throw new InvalidDataException("Invalid layout coordinates or sizes.");
                if (!bindings.Add(KeyBinding.Parse(slot.Hotkey))) throw new InvalidDataException($"Duplicate hotkey in {profile.Name}.");
            }
        }
    }
    internal void Save(string path)
    {
        Validate();
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
        else File.Move(temporary, path);
    }
    internal AppSettings Copy() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
}

internal readonly record struct KeyBinding(int Key, bool Ctrl, bool Shift, bool Alt)
{
    internal static KeyBinding Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("A swap hotkey is required for every account.");
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = parts[..^1];
        if (modifiers.Any(m => m != "Ctrl" && m != "Shift" && m != "Alt") || modifiers.Distinct().Count() != modifiers.Length ||
            !Enum.TryParse<Keys>(parts[^1], true, out var key) || key < Keys.F1 || key > Keys.F12)
            throw new InvalidDataException($"Invalid hotkey '{text}'. Use F1–F12, optionally with Ctrl+, Shift+, or Alt+.");
        return new((int)key, modifiers.Contains("Ctrl"), modifiers.Contains("Shift"), modifiers.Contains("Alt"));
    }
}
