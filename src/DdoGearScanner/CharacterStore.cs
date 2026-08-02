using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DdoGearScanner.Model;

namespace DdoGearScanner;

/// <summary>
/// The set of characters and which one is active. Persists to %APPDATA%\DdoCompanion\characters.json.
/// A character's identity is <c>Id = Slug.Of(Name)</c> — the SAME key the web app + backend use — so the
/// same character is one entity across the scanner, the web app, and runs. Legacy stores (random GUID ids)
/// are migrated to slug ids on load (loadout files are renamed to match). Gear lives per-character in
/// <see cref="CaptureStore"/> (loadout-&lt;id&gt;.json). <see cref="Changed"/> fires on any local mutation
/// (drives the character-list UI refresh and the cloud push).
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

    /// <summary>Fires after any local mutation (add/update/remove/merge) — for UI refresh + cloud push.</summary>
    public event Action? Changed;

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

        store.MigrateToSlugIds(); // one-time: rekey GUID ids -> slug(Name), rename loadout files

        if (store._profiles.Count == 0)
            store._profiles.Add(new CharacterProfile(Slug.Of("Character 1"), "Character 1", Playstyle.Unknown));
        if (store._profiles.All(p => p.Id != store.ActiveId))
            store.ActiveId = store._profiles[0].Id;
        store.Save();
        return store;
    }

    /// <summary>Rekey legacy random-GUID ids to <c>Slug.Of(Name)</c> (identity now matches web/backend),
    /// renaming each character's loadout file and remapping the active id. Backs up characters.json once.
    /// Same-slug duplicates merge (first wins).</summary>
    private void MigrateToSlugIds()
    {
        if (_profiles.Count == 0 || _profiles.All(p => p.Id == Slug.Of(p.Name))) return;
        try
        {
            if (File.Exists(StorePath) && !File.Exists(StorePath + ".bak")) File.Copy(StorePath, StorePath + ".bak");
        }
        catch { /* best-effort backup */ }

        string oldActive = ActiveId;
        string? newActive = null;
        var result = new List<CharacterProfile>();
        var seen = new HashSet<string>();
        foreach (CharacterProfile p in _profiles)
        {
            string key = Slug.Of(p.Name);
            if (key.Length == 0) continue; // nameless junk
            if (p.Id == oldActive) newActive = key;
            if (!seen.Add(key))
            {
                TryRenameLoadout(p.Id, key, overwrite: false); // dup: keep the first's loadout
                continue;
            }
            TryRenameLoadout(p.Id, key, overwrite: true);
            result.Add(p with { Id = key });
        }
        _profiles.Clear();
        _profiles.AddRange(result);
        ActiveId = newActive ?? (result.Count > 0 ? result[0].Id : "");
    }

    private static void TryRenameLoadout(string oldId, string newId, bool overwrite)
    {
        if (oldId == newId || oldId.Length == 0 || newId.Length == 0) return;
        try
        {
            string src = Path.Combine(Dir, $"loadout-{oldId}.json");
            string dst = Path.Combine(Dir, $"loadout-{newId}.json");
            if (!File.Exists(src)) return;
            if (File.Exists(dst)) { if (!overwrite) return; File.Delete(dst); }
            File.Move(src, dst);
        }
        catch { /* a loadout that fails to move is orphaned, not lost */ }
    }

    public void SetActive(string id)
    {
        if (_profiles.Any(p => p.Id == id)) { ActiveId = id; Save(); }
    }

    /// <summary>Add a character (keyed by slug(name)); adding an existing name updates + activates it instead
    /// of duplicating. Returns the profile.</summary>
    public CharacterProfile Add(string name, Playstyle playstyle, string? classes = null, int? level = null)
    {
        string clean = Clean(name, "New Character");
        string key = Slug.Of(clean);
        if (key.Length == 0) key = "character";

        int existing = _profiles.FindIndex(p => p.Id == key);
        if (existing >= 0)
        {
            _profiles[existing] = _profiles[existing] with
            {
                Name = clean,
                Playstyle = playstyle,
                Classes = Blank(classes) ?? _profiles[existing].Classes,
                Level = level ?? _profiles[existing].Level,
            };
            ActiveId = key;
            Save();
            Changed?.Invoke();
            return _profiles[existing];
        }

        var profile = new CharacterProfile(key, clean, playstyle, Blank(classes), level);
        _profiles.Add(profile);
        ActiveId = profile.Id;
        Save();
        Changed?.Invoke();
        return profile;
    }

    /// <summary>Update a character. A name change that changes the slug is a MOVE (rename the loadout file,
    /// rekey the id); if a character already lives at the new slug the two merge (first-wins on loadout).</summary>
    public void Update(CharacterProfile updated)
    {
        int i = _profiles.FindIndex(p => p.Id == updated.Id);
        if (i < 0) return;
        string newName = Clean(updated.Name, _profiles[i].Name);
        string newKey = Slug.Of(newName);
        if (newKey.Length == 0) newKey = updated.Id;

        if (newKey != updated.Id)
        {
            int target = _profiles.FindIndex(p => p.Id == newKey);
            TryRenameLoadout(updated.Id, newKey, overwrite: target < 0);
            if (target >= 0)
            {
                _profiles.RemoveAt(i);
                int ti = _profiles.FindIndex(p => p.Id == newKey);
                _profiles[ti] = _profiles[ti] with { Name = newName, Playstyle = updated.Playstyle, Classes = Blank(updated.Classes), Level = updated.Level ?? _profiles[ti].Level };
            }
            else
            {
                _profiles[i] = updated with { Id = newKey, Name = newName, Classes = Blank(updated.Classes) };
            }
            if (ActiveId == updated.Id) ActiveId = newKey;
        }
        else
        {
            _profiles[i] = updated with { Name = newName, Classes = Blank(updated.Classes) };
        }
        Save();
        Changed?.Invoke();
    }

    /// <summary>Removes a character; returns the new active id. Never removes the last one.</summary>
    public string Remove(string id)
    {
        if (_profiles.Count <= 1) return ActiveId;
        _profiles.RemoveAll(p => p.Id == id);
        if (_profiles.All(p => p.Id != ActiveId)) ActiveId = _profiles[0].Id;
        Save();
        Changed?.Invoke();
        return ActiveId;
    }

    /// <summary>Merge the account's characters (from GET /characters) into the local list: any not present by
    /// slug are added (as Unknown playstyle; level seeded from the server's last-seen). Existing local profiles
    /// (with their playstyle/classes/loadout) are left untouched.</summary>
    public void MergeFromServer(IReadOnlyList<ServerCharacter> server)
    {
        bool added = false;
        foreach (ServerCharacter sc in server)
        {
            if (sc.CharacterKey.Length == 0 || _profiles.Any(p => p.Id == sc.CharacterKey)) continue;
            _profiles.Add(new CharacterProfile(sc.CharacterKey, sc.Name, Playstyle.Unknown, null, sc.LastSeenLevel));
            added = true;
        }
        if (added)
        {
            Save();
            Changed?.Invoke();
        }
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

    private static string Clean(string? s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
