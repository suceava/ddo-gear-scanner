using System.Diagnostics;

namespace DdoGearScanner.DataImport;

/// <summary>Keeps a local, sparse checkout of just DDOBuilder's data files up to date, so a scheduled
/// run is a single command. First run does a blobless sparse clone (only Output/DataFiles); later runs
/// fast-forward pull. Returns the path to the DataFiles directory.</summary>
public static class DataSource
{
    private const string RepoUrl = "https://github.com/Maetrim/DDOBuilderV2";
    private const string DataFilesRel = "Output/DataFiles";

    public static string EnsureUpToDate(string cacheDir)
    {
        string repoDir = Path.Combine(cacheDir, "DDOBuilderV2");
        if (!Directory.Exists(Path.Combine(repoDir, ".git")))
        {
            Directory.CreateDirectory(cacheDir);
            Console.WriteLine($"Cloning DDOBuilder data (sparse) into {repoDir} …");
            Git(cacheDir, "clone", "--filter=blob:none", "--sparse", RepoUrl, repoDir);
            // Cone-mode sparse-checkout of the data dir; ancestor files (BonusTypes.xml, etc.) come too.
            Git(repoDir, "sparse-checkout", "set", DataFilesRel);
        }
        else
        {
            Console.WriteLine("Updating DDOBuilder data (git pull) …");
            Git(repoDir, "pull", "--ff-only");
        }

        string dataFiles = Path.Combine(repoDir, DataFilesRel.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(dataFiles))
            throw new DirectoryNotFoundException($"DataFiles not found after sync: {dataFiles}");
        return dataFiles;
    }

    private static void Git(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDir, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start git");
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {p.StandardError.ReadToEnd()}");
    }
}
