using Prison.Shared.Events;
using Prison.Shared.World;

namespace Prison.Shared.Evolution;

/// <summary>
/// The structured record of one match the Evolution pipeline consumes (PLAN §9.1): what was
/// exploited, where, and whether it worked — never raw input streams or replays. This is
/// offline data for between-generation evolution; nothing in a live match ever reads it
/// (Pillar #1: the two AIs stay strictly separated).
/// </summary>
public sealed record EscapeReport
{
    public required string PrisonId { get; init; }

    public bool Escaped { get; init; }

    public ulong EscapeTick { get; init; }

    public TilePos? ExitPosition { get; init; }

    public IReadOnlyList<TilePos> TunnelsDug { get; init; } = [];

    public IReadOnlyList<TilePos> FencesCut { get; init; } = [];

    public IReadOnlyList<TilePos> DoorsLockpicked { get; init; } = [];

    public int DisguisesWorn { get; init; }

    public int DisguiseCompromises { get; init; }

    public int Diversions { get; init; }

    /// <summary>How often staff physically sighted a prisoner over the whole match.</summary>
    public int TimesObserved { get; init; }

    public int Arrests { get; init; }
}

/// <summary>
/// Assembles an <see cref="EscapeReport"/> from the in-simulation event bus — one more
/// passive subscriber (§7.9), it never influences the match. For player-hosted prisons the
/// report stays local unless the host opts in to sharing (Pillar #7).
/// </summary>
public sealed class EscapeReportBuilder
{
    private readonly string _prisonId;
    private readonly List<TilePos> _tunnels = [];
    private readonly List<TilePos> _fences = [];
    private readonly List<TilePos> _lockpicks = [];
    private int _disguises;
    private int _compromises;
    private int _diversions;
    private int _observations;
    private int _arrests;
    private bool _escaped;
    private ulong _escapeTick;
    private TilePos? _exit;

    public EscapeReportBuilder(string prisonId, EventBus events)
    {
        _prisonId = prisonId;
        events.Subscribe<TileDugEvent>(evt => _tunnels.Add(evt.Position));
        events.Subscribe<FenceCutEvent>(evt => _fences.Add(evt.Position));
        events.Subscribe<DoorUnlockedEvent>(evt => _lockpicks.Add(evt.Position));
        events.Subscribe<DisguiseChangedEvent>(evt =>
        {
            if (evt.Role is not null)
                _disguises++;
        });
        events.Subscribe<DisguiseCompromisedEvent>(_ => _compromises++);
        events.Subscribe<DiversionEvent>(_ => _diversions++);
        events.Subscribe<PrisonerObservedEvent>(_ => _observations++);
        events.Subscribe<ArrestEvent>(_ => _arrests++);
        events.Subscribe<EscapeSucceededEvent>(evt =>
        {
            if (_escaped)
                return;
            _escaped = true;
            _escapeTick = evt.Tick;
            _exit = evt.Position;
        });
    }

    public bool EscapeHappened => _escaped;

    public EscapeReport Build() => new()
    {
        PrisonId = _prisonId,
        Escaped = _escaped,
        EscapeTick = _escapeTick,
        ExitPosition = _exit,
        TunnelsDug = _tunnels.ToList(),
        FencesCut = _fences.ToList(),
        DoorsLockpicked = _lockpicks.ToList(),
        DisguisesWorn = _disguises,
        DisguiseCompromises = _compromises,
        Diversions = _diversions,
        TimesObserved = _observations,
        Arrests = _arrests,
    };
}
