using System.Text.Json;
using Prison.Shared.Generation;
using Prison.Shared.Utilities;
using Prison.Shared.World;

// The standalone generator CLI (PLAN §14 Phase 5): designers generate and inspect prisons
// without launching the full game. Usage:
//   prison-gen [--seed N] [--capacity N] [--security low|medium|high]
//              [--attempts N] [--out file.json] [--quiet]

int seed = Environment.TickCount, capacity = 20, attempts = 8;
var security = SecurityLevel.Medium;
string? outPath = null;
var quiet = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seed": seed = int.Parse(args[++i]); break;
        case "--capacity": capacity = int.Parse(args[++i]); break;
        case "--security": security = Enum.Parse<SecurityLevel>(args[++i], ignoreCase: true); break;
        case "--attempts": attempts = int.Parse(args[++i]); break;
        case "--out": outPath = args[++i]; break;
        case "--quiet": quiet = true; break;
        case "--help" or "-h":
            Console.WriteLine("prison-gen [--seed N] [--capacity N] [--security low|medium|high] [--attempts N] [--out file.json] [--quiet]");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

var contentRoot = ContentPaths.Resolve();
var tiles = TileRegistry.LoadFromDirectory(Path.Combine(contentRoot, "tiles"));
var blueprints = BlueprintDefinition.LoadFromDirectory(Path.Combine(contentRoot, "rooms"));

var intent = new DesignIntent { Seed = seed, Capacity = capacity, Security = security };
var generator = new PrisonGenerator(blueprints);
var outcome = generator.GenerateBest(intent, tiles, attempts);

if (outcome is null)
{
    Console.Error.WriteLine($"No candidate passed validation + quality scoring in {attempts} attempt(s).");
    return 1;
}

if (!quiet)
{
    // ASCII preview: the map rows, with spawn and guards overlaid.
    var rows = outcome.Map.Floors[0].Rows.Select(r => r.ToCharArray()).ToArray();
    foreach (var guard in outcome.Map.Guards)
        rows[guard.Y][guard.X] = 'G';
    rows[outcome.Map.PlayerSpawn.Y][outcome.Map.PlayerSpawn.X] = '@';
    foreach (var row in rows)
        Console.WriteLine(new string(row));
    Console.WriteLine();
}

Console.WriteLine($"Seed {outcome.SeedUsed} (from base {seed}, {outcome.Attempts} attempt(s))");
Console.WriteLine($"Capacity {capacity} · security {security} · {outcome.Map.Rooms.Count} rooms · {outcome.Map.Guards.Count} guards");
Console.WriteLine($"Quality: {outcome.Quality.Total:F1}/100 (pass ≥ {QualityScorer.PassThreshold})");
foreach (var (metric, value) in outcome.Quality.Metrics.OrderBy(kv => kv.Key))
    Console.WriteLine($"  {metric,-18} {value,6:F1}");

if (outPath is not null)
{
    File.WriteAllText(outPath, JsonSerializer.Serialize(outcome.Map, TileDefinition.JsonOptions));
    Console.WriteLine($"Map written to {outPath} (loadable as a content/maps file)");
}

return 0;
