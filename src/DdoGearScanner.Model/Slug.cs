using System.Text;

namespace DdoGearScanner.Model;

/// <summary>
/// Name → slug, kept BYTE-FOR-BYTE identical to the web app + backend `slug()`
/// (ddo-gear-planner/shared/src/slug.ts): lowercase, collapse every run of non-alphanumerics to a single
/// '-', trim leading/trailing '-'. e.g. "Lesk Redeye" → "lesk-redeye".
///
/// This is the SHARED per-user character identity (matches Run.characterKey / Holding.characterKey /
/// CHAR#&lt;slug&gt; server-side), so a character is the same entity across the scanner, the web app, and runs.
/// Changing this is a breaking cross-repo contract change.
/// </summary>
public static class Slug
{
    public static string Of(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var sb = new StringBuilder(name.Length);
        bool pendingDash = false;
        foreach (char c in name.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingDash && sb.Length > 0) sb.Append('-'); // collapse the run; never a leading dash
                pendingDash = false;
                sb.Append(c);
            }
            else
            {
                pendingDash = true; // a trailing run never gets appended → no trailing dash
            }
        }
        return sb.ToString();
    }
}
