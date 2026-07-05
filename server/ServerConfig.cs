using Serilog.Events;
using Tomlyn;
using Tomlyn.Model;

namespace Prison.Server;

/// <summary>Server configuration loaded from a TOML file (PLAN §5).</summary>
public sealed class ServerConfig
{
    public string Name { get; init; } = "Prison Server";
    public int TickRate { get; init; } = Shared.Simulation.DefaultTicksPerSecond;
    public LogEventLevel LogLevel { get; init; } = LogEventLevel.Information;
    public string PerformanceProfile { get; init; } = "Balanced";

    public static ServerConfig Load(string path)
    {
        var table = Toml.ToModel(File.ReadAllText(path), path);

        var server = GetTable(table, "server");
        var logging = GetTable(table, "logging");
        var performance = GetTable(table, "performance");

        return new ServerConfig
        {
            Name = GetString(server, "name") ?? "Prison Server",
            TickRate = (int)(GetLong(server, "tick_rate") ?? Shared.Simulation.DefaultTicksPerSecond),
            LogLevel = Enum.TryParse<LogEventLevel>(GetString(logging, "level"), ignoreCase: true, out var level)
                ? level
                : LogEventLevel.Information,
            PerformanceProfile = GetString(performance, "profile") ?? "Balanced",
        };
    }

    private static TomlTable? GetTable(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) ? value as TomlTable : null;

    private static string? GetString(TomlTable? table, string key) =>
        table is not null && table.TryGetValue(key, out var value) ? value as string : null;

    private static long? GetLong(TomlTable? table, string key) =>
        table is not null && table.TryGetValue(key, out var value) && value is long l ? l : null;
}
