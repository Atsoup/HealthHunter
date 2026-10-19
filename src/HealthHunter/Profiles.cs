using System.Text.Json;
using System.Text.Json.Serialization;

namespace HealthHunter;

/// <summary>How a frozen entry is held.</summary>
public enum FreezeMode
{
    /// <summary>Keep it topped up at max (infinite health). Overheal above max is left alone.</summary>
    LockAtMax,
    /// <summary>Let it move normally but never drop below FloorPercent of max (demi-god).</summary>
    Floor,
    /// <summary>Hold the exact value it had when frozen (classic Cheat Engine freeze).</summary>
    FreezeCurrent,
    /// <summary>Block decreases, allow increases (Cheat Engine "allow increase").</summary>
    AllowIncrease,
    /// <summary>Gains are multiplied (x2 gold / XP); losses pass through unchanged.</summary>
    MultiplyGains
}

/// <summary>What a per-entry hotkey does.</summary>
public enum HotkeyAction
{
    Toggle,
    SetValue,
    Add,
    Subtract,
    Refill,
    SaveSlot,
    RestoreSlot
}

/// <summary>A keyboard combo and/or controller button combo bound to one entry.</summary>
public sealed class EntryHotkey
{
    /// <summary>MOD_ALT / MOD_CONTROL / MOD_SHIFT flags.</summary>
    public uint Modifiers { get; set; }
    /// <summary>Virtual-key code, 0 for none.</summary>
    public int Key { get; set; }
    /// <summary>XInput button mask, 0 for none. All buttons in the mask must be held.</summary>
    public ushort PadButtons { get; set; }
    public HotkeyAction Action { get; set; } = HotkeyAction.Toggle;
    /// <summary>Value for Set/Add/Subtract, slot number for Save/Restore slot.</summary>
    public double Value { get; set; }

    public string KeyText()
    {
        var parts = new List<string>();
        if (Key != 0)
        {
            var k = "";
            if ((Modifiers & Native.MOD_CONTROL) != 0) k += "Ctrl+";
            if ((Modifiers & Native.MOD_ALT) != 0) k += "Alt+";
            if ((Modifiers & Native.MOD_SHIFT) != 0) k += "Shift+";
            parts.Add(k + ((Keys)Key));
        }
        if (PadButtons != 0) parts.Add("Pad " + Gamepad.Describe(PadButtons));
        return parts.Count == 0 ? "(none)" : string.Join(" / ", parts);
    }

    public string ActionText(ValueKind kind) => Action switch
    {
        HotkeyAction.Toggle => "toggle freeze",
        HotkeyAction.SetValue => "set to " + kind.Format(Value),
        HotkeyAction.Add => "add " + kind.Format(Value),
        HotkeyAction.Subtract => "subtract " + kind.Format(Value),
        HotkeyAction.Refill => "refill to max",
        HotkeyAction.SaveSlot => $"save group to slot {(int)Value}",
        HotkeyAction.RestoreSlot => $"restore group from slot {(int)Value}",
        _ => Action.ToString()
    };

    public override string ToString() => KeyText();
}

/// <summary>One watched value (health, shield, stamina…).</summary>
public sealed class TableEntry
{
    public string Name { get; set; } = "Health";
    public ValueKind Kind { get; set; } = ValueKind.Int32;

    /// <summary>Saved path to the value. Null means a raw address that only works this session.</summary>
    public PointerChain Chain { get; set; }

    /// <summary>Used by "Refill" and as the default freeze value.</summary>
    public double MaxValue { get; set; } = 100;

    /// <summary>Where the game's own max value lives, relative to this value. Tracks level-ups.</summary>
    public long? MaxOffset { get; set; }

    public FreezeMode Mode { get; set; } = FreezeMode.FreezeCurrent;
    public double FloorPercent { get; set; } = 0.5;
    /// <summary>Factor for MultiplyGains.</summary>
    public double Multiplier { get; set; } = 2;
    /// <summary>Optional group name (e.g. "Position"). Groups can be frozen and saved/restored together.</summary>
    public string Group { get; set; } = "";
    public List<EntryHotkey> Hotkeys { get; set; } = new();

    [JsonIgnore] public long RawAddress { get; set; }
    [JsonIgnore] public bool Frozen { get; set; }
    [JsonIgnore] public double FreezeValue { get; set; }
    [JsonIgnore] public double LastValue { get; set; } = double.NaN;

    public long? Resolve(ProcessMemory mem) => Chain != null ? Chain.Resolve(mem) : (RawAddress != 0 ? RawAddress : null);

    /// <summary>Live max from game memory when known, otherwise the stored MaxValue.</summary>
    public double CurrentMax(ProcessMemory mem, long addr)
    {
        if (MaxOffset is long off && mem.TryReadValue(addr + off, Kind, out var m) && m > 0 && m < 1e9 && !double.IsNaN(m))
        {
            MaxValue = m;
            return m;
        }
        return MaxValue;
    }

    /// <summary>The value to write back given the current value, or null to leave it.</summary>
    public double? Desired(double cur, double max)
    {
        switch (Mode)
        {
            case FreezeMode.LockAtMax:
                return cur < max ? max : null;
            case FreezeMode.Floor:
                double floor = max * FloorPercent;
                if (!Kind.IsFloating()) floor = Math.Ceiling(floor);
                return cur < floor ? floor : null;
            case FreezeMode.AllowIncrease:
                if (cur > FreezeValue) { FreezeValue = cur; return null; }
                return cur < FreezeValue ? FreezeValue : null;
            case FreezeMode.MultiplyGains:
                // FreezeValue holds the last value seen. A rise is scaled up; a drop is just remembered.
                const double eps = 1e-6;
                if (double.IsNaN(FreezeValue) || cur <= FreezeValue + eps) { FreezeValue = cur; return null; }
                double next = FreezeValue + (cur - FreezeValue) * Multiplier;
                if (!Kind.IsFloating()) next = Math.Round(next);
                FreezeValue = next;
                return next;
            default:
                return cur != FreezeValue ? FreezeValue : null;
        }
    }

    public static string ModeLabel(FreezeMode m) => m switch
    {
        FreezeMode.LockAtMax => "Lock at max",
        FreezeMode.Floor => "Never below %",
        FreezeMode.AllowIncrease => "Allow increase",
        FreezeMode.MultiplyGains => "Multiply gains",
        _ => "Freeze value"
    };

    public string Describe() => Chain != null ? Chain.ToString() : $"{RawAddress:X} (this session only)";
}

public sealed class BarCalibration
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int FillArgb { get; set; }
    public int Tolerance { get; set; } = 45;
    public bool Vertical { get; set; }
    /// <summary>Which end the fill is anchored to: 0 = not known yet, 1 = left/top, 2 = right/bottom. Learned automatically.</summary>
    public int Anchor { get; set; }
}

public sealed class GameProfile
{
    public string ProcessName { get; set; }
    public List<TableEntry> Entries { get; set; } = new();
    /// <summary>Legacy single bar (read for old profiles).</summary>
    public BarCalibration Bar { get; set; }
    /// <summary>One calibrated on-screen bar per stat (Health, Stamina…).</summary>
    public Dictionary<string, BarCalibration> Bars { get; set; } = new();
    /// <summary>Saved values per "group#slot": entry name → value (save/restore position etc).</summary>
    public Dictionary<string, Dictionary<string, double>> Slots { get; set; } = new();
    /// <summary>Size and file version of the game exe when saved, to spot game updates.</summary>
    public string ExeFingerprint { get; set; }
    public DateTime Saved { get; set; }
    /// <summary>v6: bars the assistant found on screen by itself, relative to the game window, so it knows them next time.</summary>
    public List<SavedBar> AutoBars { get; set; } = new();
}

/// <summary>A HUD bar the assistant detected, in the game window's client pixels.</summary>
public sealed class SavedBar
{
    public string Label { get; set; } = "";
    public bool Vertical { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Anchor { get; set; }
    public int FillArgb { get; set; }
    public int Tolerance { get; set; } = 45;
    /// <summary>Client size of the game window when saved; a different resolution means the position is only a guess.</summary>
    public int ClientWidth { get; set; }
    public int ClientHeight { get; set; }
}

/// <summary>App-wide preferences, stored next to the profiles folder.</summary>
public sealed class AppSettings
{
    public bool AutoAttach { get; set; }
    public bool Sounds { get; set; } = true;
    public bool Overlay { get; set; } = true;
    public bool PauseWhileScanning { get; set; }
    public bool FastScan { get; set; } = true;
    public bool IncludeMapped { get; set; }
    // v6 assistant
    public bool AssistantOn { get; set; }
    public bool AssistantAttachFront { get; set; } = true;
    public bool AssistantApply { get; set; } = true;
    public bool AssistantNumbers { get; set; } = true;
    public bool AssistantMovement { get; set; } = true;
    public bool AssistantVerify { get; set; } = true;

    private static string FilePath => Path.Combine(Path.GetDirectoryName(ProfileStore.Folder)!, "settings.json");

    public static AppSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }
}

public static class ProfileStore
{
    public static string Folder
    {
        get
        {
            var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HealthHunter", "profiles");
            Directory.CreateDirectory(f);
            return f;
        }
    }

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string PathFor(string processName) =>
        Path.Combine(Folder, string.Concat(processName.Split(Path.GetInvalidFileNameChars())) + ".json");

    public static string Save(GameProfile p)
    {
        p.Saved = DateTime.Now;
        // Only entries with a pointer chain survive a restart; raw addresses are dropped.
        var copy = new GameProfile
        {
            ProcessName = p.ProcessName,
            Bars = p.Bars ?? new(),
            Slots = p.Slots ?? new(),
            ExeFingerprint = p.ExeFingerprint,
            Saved = p.Saved,
            Entries = p.Entries.Where(e => e.Chain != null).ToList()
        };
        var path = PathFor(p.ProcessName);
        File.WriteAllText(path, JsonSerializer.Serialize(copy, Opts));
        return path;
    }

    /// <summary>Process names that have a saved profile (for auto-attach).</summary>
    public static List<string> ProfiledProcessNames() =>
        Directory.GetFiles(Folder, "*.json")
                 .Where(f => !f.EndsWith(".paths.json", StringComparison.OrdinalIgnoreCase))
                 .Select(Path.GetFileNameWithoutExtension).ToList();

    public static GameProfile Load(string processName)
    {
        var path = PathFor(processName);
        return File.Exists(path) ? LoadFile(path) : null;
    }

    public static GameProfile LoadFile(string path)
    {
        var p = JsonSerializer.Deserialize<GameProfile>(File.ReadAllText(path), Opts);
        p.Bars ??= new();
        p.Slots ??= new();
        p.AutoBars ??= new();
        foreach (var e in p.Entries) { e.Hotkeys ??= new(); e.Group ??= ""; }
        if (p.Bar != null && !p.Bars.ContainsKey("Health")) p.Bars["Health"] = p.Bar; // older profiles
        p.Bar = null;
        return p;
    }
}

/// <summary>Appends values to a CSV file while enabled.</summary>
public sealed class ValueLogger : IDisposable
{
    private StreamWriter _w;
    public string FilePath { get; private set; }
    public bool Active => _w != null;

    public void Start(string processName)
    {
        Stop();
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HealthHunter Logs");
        Directory.CreateDirectory(dir);
        FilePath = Path.Combine(dir, $"{processName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        _w = new StreamWriter(FilePath) { AutoFlush = true };
        _w.WriteLine("timestamp,source,value");
    }

    /// <summary>One row per change, so entries can be added or removed mid-session.</summary>
    public void Write(string source, string value)
    {
        _w?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff},{Csv(source)},{Csv(value)}");
    }

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public void Stop() { _w?.Dispose(); _w = null; }
    public void Dispose() => Stop();
}
