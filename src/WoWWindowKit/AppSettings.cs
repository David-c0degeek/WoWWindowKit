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
    internal const string DefaultDisplayName = "Notepad++";
    public string DisplayName { get; set; } = DefaultDisplayName;
    public string ActiveProfile { get; set; } = "5-man";
    public bool MouseFocus { get; set; } = true;
    public bool Borderless { get; set; } = true;
    public int MousePollMs { get; set; } = 50;
    public string MouseToggleHotkey { get; set; } = "Ctrl+Shift+F";
    public List<string> ProcessNames { get; set; } = ["Wow", "WowClassic", "WowClassicT"];
    public List<LayoutProfile> Profiles { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public LayoutProfile Current => Profiles.Single(p => p.Name == ActiveProfile);
    /// <summary>The profile's exact name, matched case-insensitively (profile names are unique that way); null if none.</summary>
    internal string? FindProfile(string name) => Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Name;
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
        var toggle = KeyBinding.Parse(MouseToggleHotkey);
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
                var binding = KeyBinding.Parse(slot.Hotkey);
                if (!bindings.Add(binding)) throw new InvalidDataException($"Duplicate hotkey in {profile.Name}.");
                if (binding == toggle) throw new InvalidDataException($"{slot.Hotkey} is both a swap key in {profile.Name} and the mouse-focus toggle.");
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
    private static readonly HashSet<Keys> Modifiers = [Keys.ShiftKey, Keys.ControlKey, Keys.Menu, Keys.LShiftKey, Keys.RShiftKey,
        Keys.LControlKey, Keys.RControlKey, Keys.LMenu, Keys.RMenu, Keys.LWin, Keys.RWin];
    internal static bool IsModifier(int key) => Modifiers.Contains((Keys)key);

    internal static KeyBinding Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Every hotkey needs a key.");
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = parts[..^1].Select(m => m.ToLowerInvariant() switch { "ctrl" or "control" => "Ctrl", "shift" => "Shift", "alt" => "Alt", _ => m }).ToArray();
        if (modifiers.Any(m => m is not ("Ctrl" or "Shift" or "Alt")) || modifiers.Distinct().Count() != modifiers.Length || !TryKey(parts[^1], out var key))
            throw new InvalidDataException($"Invalid hotkey '{text}'. Use a key name such as F1, NumPad1, Ctrl+1 or Alt+Q, optionally with Ctrl+, Shift+, or Alt+.");
        var binding = new KeyBinding((int)key, modifiers.Contains("Ctrl"), modifiers.Contains("Shift"), modifiers.Contains("Alt"));
        if (binding.Problem() is { } problem) throw new InvalidDataException($"Hotkey '{text}': {problem}");
        return binding;
    }

    /// <summary>Reads a WinForms key combination; false for a lone modifier key.</summary>
    internal static bool TryFromKeys(Keys keyData, out KeyBinding binding)
    {
        var key = keyData & Keys.KeyCode;
        binding = new((int)key, keyData.HasFlag(Keys.Control), keyData.HasFlag(Keys.Shift), keyData.HasFlag(Keys.Alt));
        return key != Keys.None && !IsModifier((int)key);
    }

    private static bool TryKey(string name, out Keys key)
    {
        key = Keys.None;
        if (name.Length == 1 && char.IsAsciiDigit(name[0])) { key = Keys.D0 + (name[0] - '0'); return true; }
        // Reject numeric and comma-separated forms, which Enum.TryParse would otherwise accept.
        return name.Length > 0 && char.IsAsciiLetter(name[0]) && name.All(char.IsAsciiLetterOrDigit)
            && Enum.TryParse(name, true, out key) && Enum.IsDefined(key) && !IsModifier((int)key);
    }

    /// <summary>Null when the combination is usable as a hotkey; otherwise why not.</summary>
    internal string? Problem()
    {
        var key = (Keys)Key;
        if (key is >= Keys.F1 and <= Keys.F24 or >= Keys.NumPad0 and <= Keys.Divide
            or Keys.Insert or Keys.Delete or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown or Keys.Pause or Keys.Scroll) return null;
        // Plain or Shift-only typing and movement keys would stop working in chat and in game.
        if (key is >= Keys.A and <= Keys.Z or >= Keys.D0 and <= Keys.D9 or Keys.Space or Keys.Left or Keys.Up or Keys.Right or Keys.Down
            or >= Keys.OemSemicolon and <= Keys.Oem102)
            return Ctrl || Alt ? null : "letters, digits, punctuation, Space and arrows need Ctrl or Alt, so typing and movement keep working.";
        return "use F1–F24, a numpad or navigation key, or Ctrl/Alt with a letter or digit.";
    }

    public override string ToString()
    {
        var name = Key is >= (int)Keys.D0 and <= (int)Keys.D9 ? ((char)('0' + Key - (int)Keys.D0)).ToString() : ((Keys)Key).ToString();
        return (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "") + name;
    }
}
