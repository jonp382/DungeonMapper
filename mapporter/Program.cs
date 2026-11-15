using System.Text.Json;
using System.Text.Json.Nodes;
using DungeonMapper.Models;

// Map porter: reads DungeonMapper json files (map files and project files),
// ports them to the latest json spec (or a specific version), and writes them
// back. Accepts any number of files and folders on the command line.
//
// usage:
//   mapporter <file-or-folder> [more files/folders ...] [--to <version>] [--recursive] [--dry-run] [--no-backup]
//
// --to <version>   port to a specific spec version (default: latest for the
//                  file kind: map spec, project spec). Porting to a version
//                  newer than the app's latest is refused.
// --recursive      folders are scanned recursively for *.json
// --dry-run        report what would change, write nothing
// --no-backup      do not write a .bak next to each ported file (default writes one)

var paths = new List<string>();
int? targetVersion = null;
bool recursive = false, dryRun = false, backup = true;
var JsonOpts = new JsonSerializerOptions { WriteIndented = true };

for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    if (a == "--to")
    {
        if (i + 1 >= args.Length || !int.TryParse(args[++i], out int v))
        { Console.Error.WriteLine("--to needs a version number"); return 2; }
        targetVersion = v;
    }
    else if (a == "--recursive") recursive = true;
    else if (a == "--dry-run") dryRun = true;
    else if (a == "--no-backup") backup = false;
    else if (a == "--help" || a == "-h") { PrintUsage(); return 0; }
    else paths.Add(a);
}

if (paths.Count == 0) { PrintUsage(); return 2; }

var files = new List<string>();
foreach (var p in paths)
{
    if (System.IO.Directory.Exists(p))
    {
        var opts = new System.IO.EnumerationOptions { RecurseSubdirectories = recursive };
        foreach (var f in System.IO.Directory.EnumerateFiles(p, "*.json", opts))
            files.Add(f);
    }
    else if (System.IO.File.Exists(p)) files.Add(p);
    else Console.Error.WriteLine($"skipping {p}: not a file or folder");
}

if (files.Count == 0) { Console.Error.WriteLine("no json files found"); return 2; }

int ported = 0, skipped = 0, failed = 0;
foreach (var f in files)
{
    string json;
    try { json = File.ReadAllText(f); }
    catch (Exception e) { Console.WriteLine($"FAIL {f}: {e.Message}"); failed++; continue; }

    JsonNode? root;
    try { root = JsonNode.Parse(json); }
    catch (Exception e) { Console.WriteLine($"FAIL {f}: not valid json ({e.Message})"); failed++; continue; }
    if (root is not JsonObject obj) { Console.WriteLine($"SKIP {f}: root is not a json object"); skipped++; continue; }

    // file kind: a campaign (project) file, an exported map file (one named
    // map plus its pawn templates) or a bare map from before campaigns
    var kind = MapSerializer.DetectKind(json);
    bool isProject = kind == SaveFileKind.Campaign;
    bool isExport = kind == SaveFileKind.Map;
    if (kind == SaveFileKind.Unknown) { Console.WriteLine($"SKIP {f}: not a campaign or map file"); skipped++; continue; }

    int current = 0;
    if (obj["version"] != null)
    {
        try { current = obj["version"].GetValue<int>(); } catch { current = 0; }
    }
    int latestForKind = isProject ? MapSerializer.ProjectSpecVersion : MapSerializer.MapSpecVersion;
    int to = targetVersion ?? latestForKind;
    if (to > latestForKind)
    {
        Console.WriteLine($"FAIL {f}: cannot port to version {to}, latest for {(isProject ? "campaign" : "map")} files is {latestForKind}");
        failed++; continue;
    }
    if (current == to) { Console.WriteLine($"SKIP {f}: already version {to} ({(isProject ? "campaign" : "map")})"); skipped++; continue; }

    // port: re-serialize through the current DTOs so every field lands in the
    // canonical latest shape, then stamp the target version.
    string outJson;
    try
    {
        if (isProject)
        {
            var dto = JsonSerializer.Deserialize<ProjectFileDto>(json)!;
            // v4 drops polygon objects, the maps inside are ported with it
            if (to >= 4)
                foreach (var m in dto.Maps) m.Map = MapSerializer.PortPolygons(m.Map);
            dto.Version = to;
            outJson = JsonSerializer.Serialize(dto, JsonOpts);
        }
        else if (isExport)
        {
            var dto = JsonSerializer.Deserialize<MapExportDto>(json)!;
            if (to >= 4) dto.Map = MapSerializer.PortPolygons(dto.Map);
            dto.Version = to;
            dto.Map.Version = to;
            outJson = JsonSerializer.Serialize(dto, JsonOpts);
        }
        else
        {
            var dto = JsonSerializer.Deserialize<MapFileDto>(json)!;
            // v4 drops polygon objects: walls, cells and a flood region instead
            if (to >= 4) dto = MapSerializer.PortPolygons(dto);
            dto.Version = to;
            outJson = JsonSerializer.Serialize(dto, JsonOpts);
        }
    }
    catch (Exception e) { Console.WriteLine($"FAIL {f}: {e.Message}"); failed++; continue; }

    if (dryRun) { Console.WriteLine($"PORT {f}: version {current} -> {to} (dry run, nothing written)"); ported++; continue; }

    if (backup)
    {
        string bak = f + ".bak";
        if (!System.IO.File.Exists(bak)) File.WriteAllText(bak, json);
    }
    File.WriteAllText(f, outJson);
    Console.WriteLine($"PORT {f}: version {current} -> {to}");
    ported++;
}

Console.WriteLine($"done: {ported} ported, {skipped} skipped, {failed} failed");
return failed > 0 ? 1 : 0;

void PrintUsage()
{
    Console.WriteLine("usage: mapporter <file-or-folder> [more ...] [--to <version>] [--recursive] [--dry-run] [--no-backup]");
    Console.WriteLine($"latest spec versions: map = {MapSerializer.MapSpecVersion}, project = {MapSerializer.ProjectSpecVersion}");
}
