using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;

namespace DdoGearScanner.Vision;

/// <summary>Live OpenRouter settings (key + model id). Null from the provider = LLM reading disabled.</summary>
public sealed record OpenRouterConfig(string ApiKey, string Model);

/// <summary>
/// Minimal OpenRouter chat-completions client for VISION reads: send one image + a prompt, get the model's
/// text back. Config comes from a provider delegate so the user can flip the setting live. Everything is
/// best-effort: any failure (no key, network, quota, bad model, timeout) returns null and the caller keeps
/// its local-OCR result — an LLM outage must never block or break a capture.
/// </summary>
public sealed class OpenRouterClient
{
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    private readonly Func<OpenRouterConfig?> _config;
    public OpenRouterClient(Func<OpenRouterConfig?> config) => _config = config;

    public bool IsEnabled => _config() is { ApiKey.Length: > 0 };

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ddo-gear-scanner.log");
    private static void Log(string m) { try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [llm] {m}{Environment.NewLine}"); } catch { } }

    /// <summary>Send one BGR crop + prompt; returns the model's raw text reply, or null on any failure.</summary>
    public async Task<string?> ReadImageAsync(string prompt, OpenCvMat imageBgr, CancellationToken ct = default)
    {
        OpenRouterConfig? cfg = _config();
        if (cfg is null || cfg.ApiKey.Length == 0 || imageBgr.Empty()) return null;
        try
        {
            Cv2.ImEncode(".png", imageBgr, out byte[] png);
            object body = new
            {
                model = cfg.Model,
                temperature = 0,
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = prompt },
                            new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(png) } },
                        },
                    },
                },
            };
            string? content = await PostAsync(cfg, body, ct).ConfigureAwait(false);
            return content;
        }
        catch (Exception ex) { Log($"read failed: {ex.GetType().Name}: {ex.Message}"); return null; }
    }

    /// <summary>Cheap text-only ping for the Settings "Test" button: returns (ok, human-readable detail).</summary>
    public async Task<(bool Ok, string Detail)> TestAsync(CancellationToken ct = default)
    {
        OpenRouterConfig? cfg = _config();
        if (cfg is null || cfg.ApiKey.Length == 0) return (false, "No API key set.");
        try
        {
            object body = new
            {
                model = cfg.Model,
                max_tokens = 8,
                messages = new object[] { new { role = "user", content = "Reply with exactly: OK" } },
            };
            string? content = await PostAsync(cfg, body, ct).ConfigureAwait(false);
            return content is null ? (false, "Request failed — see log.") : (true, $"Connected — {cfg.Model} replied \"{content.Trim()}\".");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static async Task<string?> PostAsync(OpenRouterConfig cfg, object body, CancellationToken ct)
    {
        using HttpRequestMessage req = new(HttpMethod.Post, Endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
        req.Headers.Add("HTTP-Referer", "https://github.com/suceava/ddo-gear-scanner");
        req.Headers.Add("X-Title", "DDO Companion");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            Log($"HTTP {(int)resp.StatusCode}: {Truncate(text, 300)}");
            return null;
        }
        using JsonDocument doc = JsonDocument.Parse(text);
        string? content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        Log($"ok ({cfg.Model}): {Truncate(content ?? "", 200)}");
        return content;
    }

    private static string Truncate(string s, int n)
    {
        s = s.Replace("\r", "").Replace('\n', ' ');   // keep the whole reply on one log line
        return s.Length <= n ? s : s[..n] + "…";
    }

    /// <summary>Extract the first JSON object from a model reply (models love ```json fences and prose).</summary>
    public static string? ExtractJson(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        int start = reply.IndexOf('{');
        int end = reply.LastIndexOf('}');
        return start >= 0 && end > start ? reply[start..(end + 1)] : null;
    }
}
