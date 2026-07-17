using System.Text.Json;
using DdoGearScanner.Model;
using OpenCvMat = OpenCvSharp.Mat;

namespace DdoGearScanner.Vision;

/// <summary>
/// The Phase-2 LLM tooltip reader: sends the captured tooltip crop to an OpenRouter vision model and gets
/// the STRUCTURED item back in one shot (name, ML, mods with stat/value/bonus-type — the parts local OCR
/// gets ~75-80% right). Wraps the local reader: when the LLM is disabled or fails, the local result stands,
/// so enabling AI can only improve a capture, never lose one.
/// </summary>
public sealed class OpenRouterTooltipReader : ITooltipReader
{
    private readonly OpenRouterClient _client;
    private readonly ITooltipReader _fallback;

    public OpenRouterTooltipReader(OpenRouterClient client, ITooltipReader fallback)
    {
        _client = client;
        _fallback = fallback;
    }

    public string BackendName => _client.IsEnabled ? "OpenRouter" : _fallback.BackendName;
    public bool IsAvailable => _fallback.IsAvailable || _client.IsEnabled;

    private const string Prompt = """
        This is a screenshot of an item tooltip from Dungeons & Dragons Online (DDO).
        Return ONLY a JSON object, no markdown, with this exact shape:
        {
          "name": string,                // the item's name (top of the tooltip)
          "minimumLevel": int or null,   // from "Minimum Level: N"
          "itemType": string or null,    // e.g. "Heavy Armor", "Bastard Sword (one-handed)"
          "binding": string or null,     // e.g. "Bound to Character on Acquire"
          "isNamed": boolean,            // true for a named/unique item, false for random loot
          "mods": [                      // one entry per gold-bullet enchantment line
            {
              "stat": string,            // affix name WITHOUT the value, e.g. "Constitution", "Insightful Constitution", "Fortification"
              "value": number,           // the +N (0 if the effect has no number)
              "bonusType": string,       // from the description's "+N <Type> bonus"; "Enhancement" if not stated
              "isPercent": boolean,      // true when the value is a percentage
              "description": string or null   // the effect prose, for named effects with no number
            }
          ],
          "augments": [ { "color": string, "filled": string or null } ],   // e.g. {"color":"Blue","filled":null} for an empty blue slot
          "setBonuses": [ string ]       // set names referenced on the tooltip
        }
        Read carefully: each gold ▶ bullet is ONE mod even when its description wraps lines or restates
        numbers. Do not invent mods that aren't visible. Use null when a field isn't on the tooltip.
        """;

    public async Task<TooltipReadResult> ReadAsync(OpenCvMat tooltipBgr, CancellationToken ct = default)
    {
        if (_client.IsEnabled)
        {
            string? reply = await _client.ReadImageAsync(Prompt, tooltipBgr, ct).ConfigureAwait(false);
            GearItem? item = ParseItemJson(OpenRouterClient.ExtractJson(reply));
            if (item is not null)
                return new TooltipReadResult(item, reply ?? string.Empty, 0.95, "OpenRouter");
            // fall through: bad key / network / unparseable reply → the local reader still answers
        }
        return await _fallback.ReadAsync(tooltipBgr, ct).ConfigureAwait(false);
    }

    /// <summary>Parse the model's JSON into a GearItem. Null on any shape problem (caller falls back).
    /// Slot stays Unknown — the calibrated inventory slot overrides it downstream, same as local OCR.</summary>
    internal static GearItem? ParseItemJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;
            string name = Str(r, "name") ?? string.Empty;
            if (name.Length == 0) return null;

            var mods = new List<Mod>();
            if (r.TryGetProperty("mods", out JsonElement modsEl) && modsEl.ValueKind == JsonValueKind.Array)
                foreach (JsonElement m in modsEl.EnumerateArray())
                {
                    string? stat = Str(m, "stat");
                    if (string.IsNullOrWhiteSpace(stat)) continue;
                    double value = m.TryGetProperty("value", out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                    mods.Add(new Mod(
                        stat.Trim(), value,
                        string.IsNullOrWhiteSpace(Str(m, "bonusType")) ? "Enhancement" : Str(m, "bonusType")!.Trim(),
                        m.TryGetProperty("isPercent", out JsonElement p) && p.ValueKind == JsonValueKind.True,
                        Str(m, "description")));
                }

            var augments = new List<AugmentSlot>();
            if (r.TryGetProperty("augments", out JsonElement augEl) && augEl.ValueKind == JsonValueKind.Array)
                foreach (JsonElement a in augEl.EnumerateArray())
                {
                    string? filled = Str(a, "filled");
                    AugmentColor color = Enum.TryParse(Str(a, "color"), ignoreCase: true, out AugmentColor c) ? c : AugmentColor.Unknown;
                    augments.Add(new AugmentSlot(color, filled, string.IsNullOrWhiteSpace(filled)));
                }

            var sets = new List<SetBonus>();
            if (r.TryGetProperty("setBonuses", out JsonElement setEl) && setEl.ValueKind == JsonValueKind.Array)
                foreach (JsonElement s in setEl.EnumerateArray())
                    if (s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()))
                        sets.Add(new SetBonus(s.GetString()!.Trim()));

            return new GearItem(
                Name: name.Trim(),
                MinimumLevel: r.TryGetProperty("minimumLevel", out JsonElement ml) && ml.ValueKind == JsonValueKind.Number ? ml.GetInt32() : null,
                Slot: EquipSlot.Unknown,
                ItemTypeText: Str(r, "itemType"),
                Mods: mods,
                Augments: augments,
                SetBonuses: sets,
                Binding: Str(r, "binding"),
                IsLikelyNamed: r.TryGetProperty("isNamed", out JsonElement n) && n.ValueKind == JsonValueKind.True,
                RawOcrText: json,
                CapturedUtc: DateTime.UtcNow);
        }
        catch { return null; }
    }

    private static string? Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
