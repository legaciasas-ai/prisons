namespace Prison.Shared.Telemetry;

/// <summary>
/// Persists a match's telemetry to local disk for offline analysis (PLAN Phase 4): the event
/// journal, the movement/escape record, and the replay. For player-hosted prisons this data
/// stays local unless the host opts in to sharing (Pillar #7) — nothing here uploads anything.
/// </summary>
public static class TelemetrySink
{
    /// <summary>Writes the session's three records into <paramref name="directory"/> (created if needed).</summary>
    public static void WriteSession(
        string directory, TelemetryRecorder telemetry, EscapeRecorder escape, ReplayRecorder replay)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "telemetry.json"), telemetry.ToJson());
        File.WriteAllText(Path.Combine(directory, "escape.json"), escape.ToJson());
        File.WriteAllText(Path.Combine(directory, "replay.json"), replay.ToJson());
    }
}
