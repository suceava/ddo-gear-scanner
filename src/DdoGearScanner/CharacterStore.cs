using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DdoGearScanner.Model;

namespace DdoGearScanner;

/// <summary>
/// The set of scanned characters and which one is active. Persists to
/// %APPDATA%\DdoGearScanner\characters.json. There's always at least one character (a default is
/// created on first run); gear itself lives per-character in <see cref="CaptureStore"/>.
/// </summary>
public sealed class CharacterStore
{
    private static readonly string Dir = AppSettings.AppDataDir;
    private static readonly string StorePath = Path.Combine(Dir, "characters.json");
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class State
    {
        public List<CharacterProfile> Profiles { get; set; } = new();
        public string ActiveId { get; set; } = "";
    }

    private readonly List<CharacterProfile> _profiles = new();
    public IReadOnlyList<CharacterProfile> Profiles => _profiles;
    public string ActiveId { get; private set; } = "";
    public CharacterProfile Active =>
        _profiles.FirstOrDefault(p => p.Id == ActiveId) ?? _profiles[0];

    public static CharacterStore Load()
    {
        var store = new CharacterStore();
        try
        {
            if (File.Exists(StorePath))
            {
                State? s = JsonSerializer.Deserialize<State>(File.ReadAllText(StorePath), JsonOpts);
                if (s is not null)
                {
                    store._profiles.AddRange(s.Profiles);
                    store.ActiveId = s.ActiveId;
                }
            }
        }
        catch { /* start fresh on a corrupt file */ }

        if (store._profiles.Count == 0)
            store._profiles.Add(new CharacterProfile(NewId(), "Character 1", Playstyle.Unknown));
        if (store._profiles.All(p => p.Id != store.ActiveId))
            store.ActiveId = store._profiles[0].Id;
        store.Save();
        return store;
    }

    public void SetActive(string id)
    {
        if (_profiles.Any(p => p.Id == id)) { ActiveId = id; Save(); }
    }

    public CharacterProfile Add(string name, Playstyle playstyle, string? classes = null, int? level = null)
    {
        var profile = new CharacterProfile(NewId(), Clean(name, "New Character"), playstyle, Blank(classes), level);
        _profiles.Add(profile);
        ActiveId = profile.Id;
        Save();
        return profile;
    }

    public void Update(CharacterProfile updated)
    {
        int i = _profiles.FindIndex(p => p.Id == updated.Id);
        if (i < 0) return;
        _profiles[i] = updated with { Name = Clean(updated.Name, _profiles[i].Name), Classes = Blank(updated.Classes) };
        Save();
    }

    /// <summary>Removes a character; returns the new active id. Never removes the last one.</summary>
    public string Remove(string id)
    {
        if (_profiles.Count <= 1) return ActiveId;
        _profiles.RemoveAll(p => p.Id == id);
        if (_profiles.All(p => p.Id != ActiveId)) ActiveId = _profiles[0].Id;
        Save();
        return ActiveId;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(new State { Profiles = _profiles, ActiveId = ActiveId }, JsonOpts));
        }
        catch { /* losing one save beats crashing */ }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..8];
    private static string Clean(string? s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
