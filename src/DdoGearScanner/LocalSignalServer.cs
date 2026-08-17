using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DdoGearScanner;

/// <summary>
/// Same-machine live-update channel: a tiny always-on loopback HTTP server (same HttpListener recipe as
/// <see cref="DeviceLinkService"/>) that the web app polls LOCALLY — no AWS. `GET /runs-signal` returns a
/// bump-counter {"v":N} (see <see cref="Bump"/>, called after each run push); the web refetches its runs from
/// the backend only when N changes (i.e. only on a real new run), so AWS is touched a handful of times per
/// session, never on a timer. Bound to 127.0.0.1 only.
///
/// A DEPLOYED HTTPS page fetching this needs CORS + Chrome's Private-Network-Access preflight, so every response
/// carries the CORS headers and OPTIONS is answered with Access-Control-Allow-Private-Network: true.
/// </summary>
public sealed class LocalSignalServer : IDisposable
{
    public const int Port = 17429;

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private long _version;

    /// <summary>Bump the signal — call after a successful run push so the web knows to refetch.</summary>
    public void Bump() => Interlocked.Increment(ref _version);

    /// <summary>Start listening on 127.0.0.1:Port. Returns false (best-effort) if the port is taken.</summary>
    public bool Start()
    {
        try
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
        }
        catch
        {
            return false;
        }
        _ = Task.Run(LoopAsync);
        return true;
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }
            try { Handle(ctx); } catch { /* one bad request never kills the loop */ }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        HttpListenerResponse res = ctx.Response;
        // CORS + Private Network Access — lets a public HTTPS site fetch this local endpoint (Chrome preflight).
        res.Headers["Access-Control-Allow-Origin"] = "*";
        res.Headers["Access-Control-Allow-Private-Network"] = "true";
        res.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
        res.Headers["Access-Control-Allow-Headers"] = "*";
        res.Headers["Access-Control-Max-Age"] = "86400"; // cache the PNA/CORS preflight so polling isn't 2x requests

        if (ctx.Request.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }

        string path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
        if (path == "/runs-signal")
        {
            byte[] body = Encoding.UTF8.GetBytes($"{{\"v\":{Interlocked.Read(ref _version)}}}");
            res.StatusCode = 200;
            res.ContentType = "application/json";
            res.OutputStream.Write(body, 0, body.Length);
            res.Close();
            return;
        }
        res.StatusCode = 404;
        res.Close();
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
