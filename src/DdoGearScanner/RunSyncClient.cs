using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DdoGearScanner.Model;

namespace DdoGearScanner;

/// <summary>Live sync settings (API key + base URL). Null from the provider = sync disabled (no key).</summary>
public sealed record SyncConfig(string ApiKey, string ApiBase);

/// <summary>Startup auth-gate result. NoKey = none stored; Ok = valid (200); Unauthorized = key missing/
/// revoked (401/403); Unreachable = network/5xx (caller applies offline grace — let a known user in).</summary>
public enum AuthStatus { NoKey, Ok, Unauthorized, Unreachable }

/// <summary>The signed-in account, from GET /me — the Google identity (email/name/picture) the backend
/// captured at login.</summary>
public sealed record AccountInfo(string? Email, string? Name, string? AvatarUrl);

/// <summary>
/// HTTP client for the DDO Companion run-tracker API (see backend/CONTRACT.md in the web repo).
/// Auth is the per-user API key as a bearer token. Everything is best-effort — any failure returns
/// false and the run stays in the local outbox to retry; a sync outage must never disrupt tracking.
/// </summary>
public sealed class RunSyncClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly Func<SyncConfig?> _config;
    private readonly Func<RunRecord, string> _characterName;

    public RunSyncClient(Func<SyncConfig?> config, Func<RunRecord, string> characterName)
    {
        _config = config;
        _characterName = characterName;
    }

    public bool IsConfigured => _config() is { ApiKey.Length: > 0 };

    /// <summary>A run can be pushed only if the API's required fields are present — a resolved character name
    /// (OCR'd, else the active profile) and a dungeon name (enteredUtc/completed are always set). Guards the
    /// outbox so one bad capture can't 400 the whole batch: non-pushable runs stay local and unsynced for the
    /// user to fix (edit the missing field → it becomes pushable) or delete, never silently dropped.</summary>
    public bool IsPushable(RunRecord r) =>
        !string.IsNullOrWhiteSpace(_characterName(r)) && !string.IsNullOrWhiteSpace(r.DungeonName);

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ddo-gear-scanner.log");
    private static void Log(string m) { try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [sync] {m}{Environment.NewLine}"); } catch { } }

    private static string Iso(DateTime dt) => dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>RunRecord → the wire shape (CONTRACT.md POST /runs). CharacterId + transient Paused fields
    /// are intentionally not sent; the character name is resolved (OCR'd name, else the active profile).</summary>
    private object ToWire(RunRecord r) => new
    {
        runId = r.Id,
        characterName = _characterName(r),
        characterLevel = r.CharacterLevel,
        dungeonName = r.DungeonName,
        difficulty = r.Difficulty,
        party = r.Party,
        questLevel = r.QuestLevel,
        questDuration = r.QuestDuration,
        enteredUtc = Iso(r.EnteredUtc),
        completedUtc = r.CompletedUtc is { } c ? Iso(c) : null,
        xp = r.Xp,
        completed = r.Completed,
        edited = r.Edited,
        rawOcrText = r.RawOcrText,
    };

    private HttpRequestMessage Authed(HttpMethod method, SyncConfig cfg, string path)
    {
        var req = new HttpRequestMessage(method, cfg.ApiBase.TrimEnd('/') + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
        return req;
    }

    /// <summary>Idempotent batch upsert of runs. Returns true on success (safe to mark them synced).</summary>
    public async Task<bool> PushAsync(IReadOnlyList<RunRecord> runs, CancellationToken ct = default)
    {
        SyncConfig? cfg = _config();
        if (cfg is null || cfg.ApiKey.Length == 0 || runs.Count == 0) return false;
        try
        {
            object body = new { runs = runs.Select(ToWire).ToArray() };
            using HttpRequestMessage req = Authed(HttpMethod.Post, cfg, "/runs");
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log($"push HTTP {(int)resp.StatusCode}: {Truncate(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), 200)}");
                return false;
            }
            Log($"pushed {runs.Count} run(s)");
            return true;
        }
        catch (Exception ex) { Log($"push failed: {ex.GetType().Name}: {ex.Message}"); return false; }
    }

    /// <summary>Delete a run server-side. 404 counts as success (already gone).</summary>
    public async Task<bool> DeleteAsync(string runId, CancellationToken ct = default)
    {
        SyncConfig? cfg = _config();
        if (cfg is null || cfg.ApiKey.Length == 0) return false;
        try
        {
            using HttpRequestMessage req = Authed(HttpMethod.Delete, cfg, "/runs/" + Uri.EscapeDataString(runId));
            using HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            bool ok = resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.NotFound;
            if (!ok) Log($"delete HTTP {(int)resp.StatusCode}");
            return ok;
        }
        catch (Exception ex) { Log($"delete failed: {ex.Message}"); return false; }
    }

    /// <summary>
    /// Pull the account's runs (GET /runs). Returns the server's runs on a clean 200, or null on any failure
    /// / non-200 — the caller MUST treat null as "don't reconcile" (never as "the account is empty"), so a
    /// network blip or a bad key can't wipe local history. Server-owned fields only; CharacterId (local) and
    /// the transient Paused fields are not carried, and every pulled run is marked Synced (it's on the server).
    /// </summary>
    public async Task<IReadOnlyList<RunRecord>?> PullAsync(CancellationToken ct = default)
    {
        SyncConfig? cfg = _config();
        if (cfg is null || cfg.ApiKey.Length == 0) return null;
        try
        {
            using HttpRequestMessage req = Authed(HttpMethod.Get, cfg, "/runs");
            using HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log($"pull HTTP {(int)resp.StatusCode}");
                return null;
            }
            string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("runs", out JsonElement runs) || runs.ValueKind != JsonValueKind.Array)
                return null;

            var list = new List<RunRecord>(runs.GetArrayLength());
            foreach (JsonElement e in runs.EnumerateArray())
                if (FromWire(e) is { } run) list.Add(run);
            Log($"pulled {list.Count} run(s)");
            return list;
        }
        catch (Exception ex) { Log($"pull failed: {ex.GetType().Name}: {ex.Message}"); return null; }
    }

    /// <summary>One GET /runs element → RunRecord, or null if it's missing the required fields. Inverse of
    /// <see cref="ToWire"/>; CharacterId is left null (local-only) and Synced is true (it came from the server).</summary>
    private static RunRecord? FromWire(JsonElement e)
    {
        string? id = Str(e, "runId");
        if (id is null || Date(e, "enteredUtc") is not { } entered) return null;
        return new RunRecord(
            Id: id,
            DungeonName: Str(e, "dungeonName") ?? string.Empty,
            Difficulty: Str(e, "difficulty"),
            CharacterLevel: Int(e, "characterLevel"),
            CharacterId: null,
            EnteredUtc: entered,
            CompletedUtc: Date(e, "completedUtc"),
            Xp: Int(e, "xp"),
            Completed: Bool(e, "completed") ?? false,
            RawOcrText: Str(e, "rawOcrText") ?? string.Empty,
            Edited: Bool(e, "edited") ?? false,
            QuestLevel: Int(e, "questLevel"),
            CharacterName: Str(e, "characterName"),
            QuestDuration: Str(e, "questDuration"),
            Paused: false,
            PausedUtc: null,
            Synced: true,
            Party: Str(e, "party"));
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;
    private static bool? Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    private static DateTime? Date(JsonElement e, string name)
        => Str(e, name) is { } s && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime d) ? d : null;

    /// <summary>Validate the key via GET /me — returns (ok, detail) for the Settings "Test" button.</summary>
    public async Task<(bool Ok, string Detail)> ValidateAsync(CancellationToken ct = default)
    {
        SyncConfig? cfg = _config();
        if (cfg is null || cfg.ApiKey.Length == 0) return (false, "No API key set.");
        try
        {
            using HttpRequestMessage req = Authed(HttpMethod.Get, cfg, "/me");
            using HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (false, $"HTTP {(int)resp.StatusCode} — check the key.");
            using JsonDocument doc = JsonDocument.Parse(text);
            string who =
                doc.RootElement.TryGetProperty("email", out JsonElement em) && em.ValueKind == JsonValueKind.String ? em.GetString()! :
                doc.RootElement.TryGetProperty("name", out JsonElement nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString()! :
                "your account";
            return (true, $"Connected as {who}.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>The signed-in account (email/name/avatar) via GET /me — for the header account button.
    /// Null on any failure (not signed in / offline).</summary>
    public async Task<AccountInfo?> AccountAsync(CancellationToken ct = default)
    {
        SyncConfig? cfg = _config();
        if (cfg is null || cfg.ApiKey.Length == 0) return null;
        try
        {
            using HttpRequestMessage req = Authed(HttpMethod.Get, cfg, "/me");
            using HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            JsonElement r = doc.RootElement;
            return new AccountInfo(Str(r, "email"), Str(r, "name"), Str(r, "avatarUrl"));
        }
        catch { return null; }
    }

    /// <summary>Classify the stored credential for the startup gate (GET /me). Distinguishes a definitively
    /// bad/absent key (block) from a transient network failure (offline grace) so a dropped connection at
    /// launch never locks a signed-in user out.</summary>
    public async Task<AuthStatus> CheckAuthAsync(CancellationToken ct = default)
    {
        SyncConfig? cfg = _config();
        if (cfg is null || cfg.ApiKey.Length == 0) return AuthStatus.NoKey;
        try
        {
            using HttpRequestMessage req = Authed(HttpMethod.Get, cfg, "/me");
            using HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return AuthStatus.Ok;
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return AuthStatus.Unauthorized;
            return AuthStatus.Unreachable; // 5xx / unexpected — treat as transient
        }
        catch { return AuthStatus.Unreachable; } // network error / timeout / cancellation
    }

    /// <summary>Upsert a character's equipped loadout (POST /loadouts). Matched items carry the catalog id
    /// (slug(name)-ml&lt;ML&gt;); all slots carry the captured mods so the planner can analyze unmatched gear.
    /// Best-effort.</summary>
    public async Task<bool> PushLoadoutAsync(string characterKey, string characterName, IReadOnlyDictionary<EquipSlot, GearItem> loadout, CancellationToken ct = default)
    {
        SyncConfig? cfg = _config();
        if (cfg is null || cfg.ApiKey.Length == 0 || characterKey.Length == 0) return false;
        try
        {
            var slots = new Dictionary<string, object>();
            foreach ((EquipSlot slot, GearItem item) in loadout)
            {
                int? ml = item.MinimumLevel;
                string? itemId = item.Matched && ml.HasValue ? $"{Slug.Of(item.Name)}-ml{ml.Value}" : null;
                slots[slot.ToString()] = new
                {
                    itemId,
                    name = item.Name,
                    minLevel = ml,
                    matched = item.Matched,
                    mods = item.Mods.Select(m => new { stat = m.Stat, value = m.Value, bonusType = m.BonusType, isPercent = m.IsPercent }).ToArray(),
                };
            }
            // characterName lets the server provision the Character from the loadout (no separate char-list push).
            object body = new { characterKey, characterName, source = "scanner", slots };
            using HttpRequestMessage req = Authed(HttpMethod.Post, cfg, "/loadouts");
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { Log($"loadout push HTTP {(int)resp.StatusCode}"); return false; }
            Log($"pushed loadout {characterKey} ({slots.Count} slot(s))");
            return true;
        }
        catch (Exception ex) { Log($"loadout push failed: {ex.GetType().Name}: {ex.Message}"); return false; }
    }

    private static string Truncate(string s, int n)
    {
        s = s.Replace("\r", "").Replace('\n', ' ');
        return s.Length <= n ? s : s[..n] + "…";
    }
}
