using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace DdoGearScanner;

/// <summary>Result of an update check. <see cref="UpdateAvailable"/> = a newer release exists; <see cref="Major"/>
/// = the newer release bumps the MAJOR version (likely breaking), so the UI can warn harder.</summary>
public sealed record UpdateInfo(Version Current, Version Latest, bool UpdateAvailable, bool Major, string Url);

/// <summary>Checks GitHub for a release newer than the running build. Best-effort by design: ANY failure
/// (offline, API rate limit, malformed tag) returns null, so a startup check can never block or crash the app.
/// The running version comes from the assembly (set by &lt;Version&gt; in the csproj, which publish.ps1 stamps
/// with the release tag), and releases are compared by semver major.minor.patch.</summary>
public static class UpdateChecker
{
    private const string LatestReleaseApi = "https://api.github.com/repos/suceava/ddo-gear-scanner/releases/latest";
    private const string ReleasesPage = "https://github.com/suceava/ddo-gear-scanner/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        // GitHub requires a User-Agent; the JSON media type pins the v3 API shape.
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DdoCompanion", CurrentVersion.ToString()));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>The running build's version (assembly version = the &lt;Version&gt; / -p:Version stamped at publish).</summary>
    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>"1.2.3" for display (drops the 4th/revision component the assembly version carries).</summary>
    public static string CurrentDisplay
    {
        get { Version v = CurrentVersion; return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}"; }
    }

    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using HttpResponseMessage resp = await Http.GetAsync(LatestReleaseApi).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            await using Stream stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("tag_name", out JsonElement tagEl)) return null;
            if (!TryParseTag(tagEl.GetString(), out Version latest)) return null;

            string url = doc.RootElement.TryGetProperty("html_url", out JsonElement urlEl) ? (urlEl.GetString() ?? ReleasesPage) : ReleasesPage;
            Version current = CurrentVersion;
            bool newer = Cmp(latest, current) > 0;
            bool major = latest.Major > current.Major;
            return new UpdateInfo(current, latest, newer, major, url);
        }
        catch
        {
            return null; // offline / rate-limited / malformed — silently report "no update"
        }
    }

    /// <summary>Release tags look like "v0.10.0" or "0.10.0" (with an optional -pre/+build suffix). Parse the
    /// leading major.minor.patch.</summary>
    private static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        string s = tag.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out version!);
    }

    // Compare on major.minor.patch only (the assembly version's 4th component is meaningless here).
    private static int Cmp(Version a, Version b)
    {
        if (a.Major != b.Major) return a.Major - b.Major;
        if (a.Minor != b.Minor) return a.Minor - b.Minor;
        return Math.Max(a.Build, 0) - Math.Max(b.Build, 0);
    }
}
