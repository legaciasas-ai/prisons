using Prison.Shared.World;

namespace Prison.Shared.Generation;

/// <summary>
/// The visual pipeline passes (PLAN §8.3 steps 7–9): Beautification (style kit), Storytelling
/// (decoration rules) and Exterior generation. All three are cosmetic by contract — they must
/// never change where an entity can walk or see *in a gameplay-relevant way*; structural
/// validation re-runs after them as a safety net (FamilyPipeline).
/// </summary>
public static class StylePasses
{
    /// <summary>
    /// Beautification (step 7): reskin the map by pointing its glyph legend at the kit's
    /// materials. Same layout + different kit = gameplay-identical, visually distinct (§8.2).
    /// Functional materials (metal work floors, fences, terrain) keep their identity.
    /// </summary>
    public static MapDefinition ApplyStyleKit(MapDefinition map, StyleKitDefinition kit)
    {
        var legend = new Dictionary<string, MapDefinition.LegendEntry>(map.Legend);
        legend["#"] = new() { Wall = kit.WallMaterial, Floor = kit.FloorMaterial };
        legend["."] = new() { Floor = kit.FloorMaterial };
        legend["g"] = new() { Wall = kit.WindowMaterial, Floor = kit.FloorMaterial };

        return map with
        {
            Legend = legend,
            DisplayName = $"{map.DisplayName} · {kit.DisplayName}",
        };
    }

    /// <summary>Glyphs the decoration/exterior passes may introduce, and their tiles.</summary>
    private static readonly Dictionary<string, (char Glyph, MapDefinition.LegendEntry Entry)> DecoGlyphs = new()
    {
        ["cracked_floor"] = ('c', new() { Floor = "cracked_floor" }),
        ["bush"] = ('b', new() { Floor = "bush" }),
        ["tree"] = ('T', new() { Wall = "tree", Floor = "grass" }),
    };

    /// <summary>
    /// Storytelling (step 8): seeded, sparse tile swaps per the family's decoration rules.
    /// Deterministic for a given (map, rule set, seed) — a regenerated family generation
    /// decays in exactly the same places.
    /// </summary>
    public static MapDefinition ApplyDecoration(MapDefinition map, DecorationRuleSet rules, int seed)
    {
        var rng = new Random(seed);
        var legend = new Dictionary<string, MapDefinition.LegendEntry>(map.Legend);

        // Which glyphs currently resolve to each rule's target tile (floor-only glyphs; wall
        // glyphs like '#' stay untouched — cracking through a wall would be structural).
        var glyphForTarget = new Dictionary<char, DecorationRuleSet.DecorationRule>();
        foreach (var rule in rules.Rules)
        {
            foreach (var (glyph, entry) in map.Legend)
            {
                if (entry.Wall is null && entry.Floor == rule.TargetTile && glyph.Length == 1)
                    glyphForTarget[glyph[0]] = rule;
            }
        }

        var rows = map.Floors[0].Rows.Select(r => r.ToCharArray()).ToList();
        foreach (var row in rows)
        {
            for (var x = 0; x < row.Length; x++)
            {
                if (!glyphForTarget.TryGetValue(row[x], out var rule))
                    continue;
                if (rng.NextDouble() >= rule.Chance)
                    continue;
                if (!DecoGlyphs.TryGetValue(rule.ReplacementTile, out var deco))
                    continue;
                legend[deco.Glyph.ToString()] = deco.Entry;
                row[x] = deco.Glyph;
            }
        }

        var floors = map.Floors.ToList();
        floors[0] = floors[0] with { Rows = rows.Select(r => new string(r)).ToList() };
        return map with { Legend = legend, Floors = floors };
    }

    /// <summary>
    /// Exterior generation (step 9, §8.5): purely procedural — an access road from the gate,
    /// tree scatter outside the fence, loose rocks in the yard. Nobody memorizes exteriors the
    /// way they memorize interiors, so no blueprint library is involved.
    /// </summary>
    public static MapDefinition ApplyExterior(MapDefinition map, int seed)
    {
        var rng = new Random(seed);
        var legend = new Dictionary<string, MapDefinition.LegendEntry>(map.Legend);
        var rows = map.Floors[0].Rows.Select(r => r.ToCharArray()).ToList();
        var height = rows.Count;
        var width = rows[0].Length;

        // Access road: dirt strip from the south gate to the map edge.
        var gate = map.Doors.FirstOrDefault(d => d.Locked);
        if (gate is not null)
        {
            for (var y = gate.Y + 1; y < height; y++)
                for (var x = gate.X - 1; x <= gate.X + 1; x++)
                    if (x >= 0 && x < width && rows[y][x] == ',')
                        rows[y][x] = 'd';
        }

        // Trees in the outer grass ring (never on the road), sparse.
        var treeEntry = DecoGlyphs["tree"];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var ring = Math.Min(Math.Min(x, y), Math.Min(width - 1 - x, height - 1 - y));
                if (ring < 2 && rows[y][x] == ',' && rng.NextDouble() < 0.10)
                {
                    legend[treeEntry.Glyph.ToString()] = treeEntry.Entry;
                    rows[y][x] = treeEntry.Glyph;
                }
            }
        }

        // A few loose rocks in the yard — future diversion ammunition (§7.8).
        var items = map.Items.ToList();
        var yardTiles = new List<(int X, int Y)>();
        for (var y = 5; y < height - 5; y++)
            for (var x = 5; x < width - 5; x++)
                if (rows[y][x] == ',')
                    yardTiles.Add((x, y));
        for (var i = 0; i < 3 && yardTiles.Count > 0; i++)
        {
            var (x, y) = yardTiles[rng.Next(yardTiles.Count)];
            items.Add(new MapDefinition.MapItem { Id = "rock", X = x, Y = y });
        }

        var floors = map.Floors.ToList();
        floors[0] = floors[0] with { Rows = rows.Select(r => new string(r)).ToList() };
        return map with { Legend = legend, Floors = floors, Items = items };
    }
}
