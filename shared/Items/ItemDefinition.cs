using Prison.Shared.World;

namespace Prison.Shared.Items;

/// <summary>
/// An item is a bag of properties defined in data (<c>content/items/*.json</c>), never a
/// hardcoded type (Pillar #4). Capability seconds of 0 mean "this item cannot do that";
/// engine code only ever asks "how long would this item take to dig", never "is this a shovel".
/// </summary>
public sealed record ItemDefinition
{
    public required string Id { get; init; }

    public string DisplayName { get; init; } = "";

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Seconds to dig through one diggable tile with this item (0 = cannot dig).</summary>
    public float DigSeconds { get; init; }

    /// <summary>Seconds to cut through one cuttable wall tile (0 = cannot cut).</summary>
    public float CutSeconds { get; init; }

    /// <summary>Seconds to pick one lock (0 = cannot lockpick).</summary>
    public float LockpickSeconds { get; init; }

    /// <summary>Staff role this item impersonates when worn (e.g. "guard"), or null.</summary>
    public string? DisguiseRole { get; init; }

    public bool HasTag(string tag) => Tags.Contains(tag);
}
