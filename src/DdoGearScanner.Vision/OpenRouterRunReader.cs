using System.Text.Json;
using OpenCvMat = OpenCvSharp.Mat;

namespace DdoGearScanner.Vision;

/// <summary>
/// LLM (OpenRouter) reads for the run tracker's EVENT moments: the quest-entry popup (name / level /
/// difficulty / duration in one shot — including the selection ring local OCR needs pixel hacks for) and
/// the character avatar (name + level). Called at most once per event by the pipeline; results override
/// the local-OCR values when they land. Null on any failure — local OCR's answer stands.
/// </summary>
public sealed class OpenRouterRunReader
{
    private readonly OpenRouterClient _client;
    public OpenRouterRunReader(OpenRouterClient client) => _client = client;

    public bool IsEnabled => _client.IsEnabled;

    private const string EntryPrompt = """
        This is a screenshot region from Dungeons & Dragons Online showing an adventure-entry popup
        (the dialog with "Select Difficulty", a quest name, "Level: N", "Duration: ...", difficulty icons).
        Return ONLY a JSON object, no markdown:
        {
          "quest": string or null,       // the quest's name shown in the popup
          "level": int or null,          // from "Level: N"
          "difficulty": string or null,  // the SELECTED difficulty (highlighted icon/label): "Casual", "Normal", "Hard", "Elite", or "Reaper N" (N = skull count shown)
          "duration": string or null     // from "Duration:": "Short", "Medium", "Long" or "Very Long"
        }
        If there is no such popup in the image, return {"quest": null}.
        """;

    private const string CharacterPrompt = """
        This is a screenshot of a character's avatar/vitals area from Dungeons & Dragons Online:
        the character NAME appears above the health bar, and the character LEVEL is a small number
        near the portrait. Return ONLY a JSON object, no markdown:
        { "name": string or null, "level": int or null }
        """;

    public async Task<QuestEntry?> ReadEntryAsync(OpenCvMat popupBgr, CancellationToken ct = default)
    {
        string? reply = await _client.ReadImageAsync(EntryPrompt, popupBgr, ct).ConfigureAwait(false);
        string? json = OpenRouterClient.ExtractJson(reply);
        if (json is null) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;
            string? quest = Str(r, "quest");
            if (string.IsNullOrWhiteSpace(quest)) return null;
            return new QuestEntry(
                quest.Trim(),
                Int(r, "level"),
                Str(r, "difficulty"),
                Str(r, "duration"));
        }
        catch { return null; }
    }

    private const string ChatXpPrompt = """
        This is the chat-log region from Dungeons & Dragons Online at the moment a quest completed. Find the
        quest-completion experience line — "You receive N XP" (or "N experience points"). Return ONLY a JSON
        object, no markdown: { "xp": int or null }. 0 is a VALID amount (some quests award none), so return 0
        for "You receive 0 XP". If no such line is visible in the image, return {"xp": null}.
        """;

    /// <summary>Read the quest-completion XP from the chat crop at completion. The vision LLM handles the noisy,
    /// fast-scrolling chat far better than local OCR (which misses/garbles the one-shot "You receive N XP" line);
    /// 0 is returned as 0, not null. Null on failure — the local capture's value stands.</summary>
    public async Task<int?> ReadChatXpAsync(OpenCvMat chatBgr, CancellationToken ct = default)
    {
        string? reply = await _client.ReadImageAsync(ChatXpPrompt, chatBgr, ct).ConfigureAwait(false);
        string? json = OpenRouterClient.ExtractJson(reply);
        if (json is null) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return Int(doc.RootElement, "xp");
        }
        catch { return null; }
    }

    public async Task<CharacterInfo?> ReadCharacterAsync(OpenCvMat avatarBgr, CancellationToken ct = default)
    {
        string? reply = await _client.ReadImageAsync(CharacterPrompt, avatarBgr, ct).ConfigureAwait(false);
        string? json = OpenRouterClient.ExtractJson(reply);
        if (json is null) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;
            string? name = Str(r, "name");
            int? level = Int(r, "level");
            return string.IsNullOrWhiteSpace(name) && level is null ? null : new CharacterInfo(name?.Trim() ?? string.Empty, level);
        }
        catch { return null; }
    }

    private static string? Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement e, string prop)
        => e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}
