using System.Text.Json;
using DdoGearScanner.DataImport;

// DDOBuilder → catalog importer. Reusable / re-runnable (a scheduled refresh is just `dotnet run`):
//   1. sync a sparse checkout of DDOBuilder's data files (clone first time, pull after),
//   2. parse every *.item + BonusTypes.xml,
//   3. write items.json + bonustypes.json into the Vision project's Data folder.
//
// Args (all optional):
//   --source <dir>   use this DataFiles dir instead of the git cache (skips the network)
//   --out <dir>      output dir (default: <repo>/src/DdoGearScanner.Vision/Data)
//   --cache <dir>    git cache dir (default: <repo>/tools/DdoDataImporter/.cache)

string? sourceArg = ArgValue(args, "--source");
string? outArg = ArgValue(args, "--out");
string? cacheArg = ArgValue(args, "--cache");

string repoRoot = FindRepoRoot();
string cacheDir = cacheArg ?? Path.Combine(repoRoot, "tools", "DdoDataImporter", ".cache");
string outDir = outArg ?? Path.Combine(repoRoot, "data");

string dataFiles = sourceArg ?? DataSource.EnsureUpToDate(cacheDir);
Directory.CreateDirectory(outDir);

string itemsDir = Path.Combine(dataFiles, "Items");
string bonusTypesXml = Path.Combine(dataFiles, "BonusTypes.xml");

Console.WriteLine($"Parsing items from {itemsDir} …");
List<CatalogItem> items = Parsers.ParseItems(itemsDir, out List<string> warnings, out int cosmeticSkipped);
List<BonusTypeDef> bonusTypes = Parsers.ParseBonusTypes(bonusTypesXml);

var opts = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
string itemsPath = Path.Combine(outDir, "items.json");
string bonusTypesPath = Path.Combine(outDir, "bonustypes.json");
File.WriteAllText(itemsPath, JsonSerializer.Serialize(items, opts));
File.WriteAllText(bonusTypesPath, JsonSerializer.Serialize(bonusTypes, opts));

int modCount = items.Sum(i => i.Mods.Count);
int withSets = items.Count(i => i.Sets.Count > 0);
Console.WriteLine($"\nWrote {items.Count:N0} items ({modCount:N0} mods, {withSets:N0} with set bonuses; skipped {cosmeticSkipped:N0} cosmetic-only) → {itemsPath}");
Console.WriteLine($"Wrote {bonusTypes.Count} bonus types → {bonusTypesPath}");
if (warnings.Count > 0)
{
    Console.WriteLine($"\n{warnings.Count} warning(s):");
    foreach (string w in warnings.Take(20)) Console.WriteLine("  " + w);
}

static string? ArgValue(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DdoGearScanner.sln")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}
