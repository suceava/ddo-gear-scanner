using System.IO;

namespace DdoGearScanner;

/// <summary>
/// Debug crop-dump folders, in ONE place with a consistent scheme: everything under a single
/// %APPDATA%\DdoGearScanner\debug\ root, one subfolder per feature. Each dump is gated by its own
/// checkbox; unchecking it wipes that subfolder (see <see cref="Clear"/>).
/// </summary>
public static class DebugPaths
{
    private static string Root => Path.Combine(AppSettings.AppDataDir, "debug");
    public static string Gear => Path.Combine(Root, "gear");   // one crop per gear capture
    public static string Run => Path.Combine(Root, "run");      // the run tracker's region crops

    public static void ClearGear() => Clear(Gear);
    public static void ClearRun() => Clear(Run);

    private static void Clear(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    /// <summary>One-time housekeeping: remove the OLD inconsistently-named folders (debug-crops, run-debug)
    /// left by earlier builds, so the ~unbounded gear dump doesn't linger after the rename.</summary>
    public static void RemoveLegacyFolders()
    {
        foreach (string name in new[] { "debug-crops", "run-debug" })
            Clear(Path.Combine(AppSettings.AppDataDir, name));
    }
}
