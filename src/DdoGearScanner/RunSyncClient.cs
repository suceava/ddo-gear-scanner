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

/// <summary>
/// HTTP client for the DDO Gear Planner run-tracker API (see backend/CONTRACT.md in the web repo).
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

    private static string Truncate(string s, int n)
    {
        s = s.Replace("\r", "").Replace('\n', ' ');
        return s.Length <= n ? s : s[..n] + "…";
    }
}
