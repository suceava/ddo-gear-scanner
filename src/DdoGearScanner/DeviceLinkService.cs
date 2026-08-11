using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DdoGearScanner;

/// <summary>
/// Web-brokered device link (Design B). Opens the DDO Companion web app's /link-desktop page in the user's
/// browser; the web app signs them in with Google, mints an API key, and redirects back to a one-shot
/// loopback listener started here. No Google/OAuth/PKCE code lives in the desktop — it only receives the key.
/// Best-effort: any failure returns Ok=false with a detail message; the user can fall back to pasting a key.
/// </summary>
public sealed class DeviceLinkService
{
    private readonly Func<string> _webBase; // e.g. https://ddo.gnarlybits.com

    public DeviceLinkService(Func<string> webBase) => _webBase = webBase;

    public sealed record LinkResult(bool Ok, string? ApiKey, string Detail);

    // Shares the sync log file so a failed link is diagnosable (%TEMP%\ddo-gear-scanner.log).
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ddo-gear-scanner.log");
    private static void Log(string m) { try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [link] {m}{Environment.NewLine}"); } catch { } }

    /// <summary>Run the link flow: start a loopback listener, open the browser to /link-desktop, and await the
    /// redirect carrying the minted key. Validates the returned state matches. Times out after 3 minutes.</summary>
    public async Task<LinkResult> LinkAsync(CancellationToken ct = default)
    {
        int port = FreeLoopbackPort();
        string state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string prefix = $"http://127.0.0.1:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try { listener.Start(); }
        catch (Exception ex) { Log($"listener start FAILED: {ex.Message}"); return new LinkResult(false, null, $"Couldn't start the local listener: {ex.Message}"); }

        try
        {
            string webBase = _webBase().TrimEnd('/');
            string redirect = Uri.EscapeDataString($"{prefix}cb");
            string url = $"{webBase}/link-desktop?redirect={redirect}&state={state}";
            Log($"listening on {prefix}; opening {url}");
            OpenBrowser(url);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));

            // Ignore stray requests (favicon, etc.) until the real /cb callback arrives.
            while (true)
            {
                HttpListenerContext ctx = await GetContextAsync(listener, timeout.Token).ConfigureAwait(false);
                string path = ctx.Request.Url?.AbsolutePath ?? "";
                Log($"request: {path}?{ctx.Request.Url?.Query}");
                if (!path.TrimEnd('/').EndsWith("/cb"))
                {
                    Respond(ctx, 404, "<h1>Not found</h1>");
                    continue;
                }

                string? key = ctx.Request.QueryString["key"];
                string? gotState = ctx.Request.QueryString["state"];
                if (gotState != state || string.IsNullOrWhiteSpace(key))
                {
                    Log($"callback invalid: stateMatch={gotState == state} hasKey={!string.IsNullOrWhiteSpace(key)}");
                    Respond(ctx, 400, "<h1>Link failed</h1><p>Close this tab and try again from DDO Companion.</p>");
                    return new LinkResult(false, null, "The link response was invalid (state mismatch or missing key).");
                }

                Log("callback OK — key received");
                Respond(ctx, 200, "<h1>DDO Companion connected ✓</h1><p>You can close this tab and return to the app.</p>");
                return new LinkResult(true, key.Trim(), "Connected.");
            }
        }
        catch (OperationCanceledException) { Log("timed out / canceled"); return new LinkResult(false, null, "Sign-in timed out or was canceled."); }
        catch (Exception ex) { Log($"error: {ex.Message}"); return new LinkResult(false, null, ex.Message); }
        finally { try { listener.Stop(); } catch { } }
    }

    /// <summary>Await a request or cancellation (HttpListener has no cancellable GetContext).</summary>
    private static async Task<HttpListenerContext> GetContextAsync(HttpListener listener, CancellationToken ct)
    {
        Task<HttpListenerContext> get = listener.GetContextAsync();
        Task finished = await Task.WhenAny(get, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
        if (finished != get) throw new OperationCanceledException(ct);
        return await get.ConfigureAwait(false);
    }

    /// <summary>Grab a free loopback TCP port by binding to :0 and releasing it.</summary>
    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static void Respond(HttpListenerContext ctx, int status, string bodyHtml)
    {
        try
        {
            string html = "<!doctype html><meta charset=utf-8>" +
                "<body style=\"font-family:system-ui,Segoe UI,sans-serif;background:#1a1712;color:#e8dcc0;" +
                "text-align:center;padding-top:15vh\">" + bodyHtml + "</body>";
            byte[] buf = Encoding.UTF8.GetBytes(html);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = buf.Length;
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
            ctx.Response.OutputStream.Close();
        }
        catch { /* the client navigated away — nothing to do */ }
    }

    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* browser couldn't open; the caller surfaces a failure detail */ }
    }
}
