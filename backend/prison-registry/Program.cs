using System.Text.Json;
using NATS.Client.Core;
using Npgsql;
using Prison.Shared.Lifecycle;
using Serilog;

// Prison Registry & Accounts service (PLAN §11, Phase 12): players, friends, prison
// lifecycle backed by Postgres, escape ingestion publishing on NATS (§11.4). One thin
// minimal-API deployable; the state *rules* stay in shared/Lifecycle (Pillar #3).

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

var db = new NpgsqlDataSourceBuilder(
    Environment.GetEnvironmentVariable("PRISON_DB")
    ?? "Host=localhost;Username=prison;Password=prison;Database=prison").Build();

var natsUrl = Environment.GetEnvironmentVariable("PRISON_NATS") ?? "nats://localhost:4222";
await using var nats = new NatsConnection(new NatsOpts { Url = natsUrl });

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// ---- players & friends (the Phase 8 friend-invite dependency) ----

app.MapPost("/players", async (PlayerDto dto) =>
{
    await using var cmd = db.CreateCommand(
        "INSERT INTO players(username) VALUES ($1) RETURNING player_id");
    cmd.Parameters.AddWithValue(dto.Username);
    return Results.Ok(new { playerId = await cmd.ExecuteScalarAsync() });
});

app.MapPost("/friends", async (FriendDto dto) =>
{
    await using var cmd = db.CreateCommand(
        "INSERT INTO friends(player_a, player_b) VALUES ($1,$2) ON CONFLICT DO NOTHING");
    cmd.Parameters.AddWithValue(Guid.Parse(dto.From));
    cmd.Parameters.AddWithValue(Guid.Parse(dto.To));
    await cmd.ExecuteNonQueryAsync();
    return Results.Ok();
});

app.MapPost("/friends/accept", async (FriendDto dto) =>
{
    await using var cmd = db.CreateCommand(
        "UPDATE friends SET status='accepted' WHERE player_a=$1 AND player_b=$2");
    cmd.Parameters.AddWithValue(Guid.Parse(dto.From));
    cmd.Parameters.AddWithValue(Guid.Parse(dto.To));
    return await cmd.ExecuteNonQueryAsync() == 1 ? Results.Ok() : Results.NotFound();
});

app.MapGet("/players/{id:guid}/friends", async (Guid id) =>
{
    await using var cmd = db.CreateCommand(
        "SELECT player_a, player_b FROM friends WHERE status='accepted' AND (player_a=$1 OR player_b=$1)");
    cmd.Parameters.AddWithValue(id);
    var friends = new List<Guid>();
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
        friends.Add(r.GetGuid(0) == id ? r.GetGuid(1) : r.GetGuid(0));
    return Results.Ok(friends);
});

// ---- prison registry & lifecycle (§10/§11.1; rules from shared PrisonLifecycle) ----

app.MapPost("/prisons/official", async (OfficialDto dto) =>
{
    var record = PrisonLifecycle.CreateOfficial(dto.FamilyId, dto.Generation, dto.ParentPrisonId,
        DateTimeOffset.UtcNow);
    await Store.Insert(db, record);
    return Results.Ok(record);
});

app.MapPost("/prisons/community", async (CommunityDto dto) =>
{
    var record = PrisonLifecycle.CreateCommunity(dto.FamilyId, dto.OwnerId,
        Enum.Parse<PrisonVisibility>(dto.Visibility, true), dto.ShareEscapeData, DateTimeOffset.UtcNow);
    await Store.Insert(db, record);
    return Results.Ok(record);
});

app.MapGet("/prisons/{id}", async (string id) =>
    await Store.Get(db, id) is { } p ? Results.Ok(p) : Results.NotFound());

// Admin lifecycle control (§10.4): advance a step, optionally forcing past the 24h window.
app.MapPost("/prisons/{id}/advance", async (string id, bool admin = false) =>
{
    if (await Store.Get(db, id) is not { } prison)
        return Results.NotFound();
    try
    {
        var next = PrisonLifecycle.Advance(prison, DateTimeOffset.UtcNow, admin);
        await Store.Update(db, next);
        if (next.Status == PrisonStatus.Compromised)
            await nats.PublishAsync("prison.compromised", JsonSerializer.SerializeToUtf8Bytes(next));
        return Results.Ok(next);
    }
    catch (InvalidOperationException e)
    {
        return Results.Conflict(new { error = e.Message });
    }
});

// ---- escape ingestion (§11.4): store structured report, feed the evolution pipeline ----

app.MapPost("/escapes", async (EscapeDto dto) =>
{
    if (await Store.Get(db, dto.PrisonId) is not { ShareEscapeData: true })
        return Results.NotFound(new { error = "unknown prison, or host has not opted into sharing" });

    await using var cmd = db.CreateCommand(
        """
        WITH e AS (INSERT INTO escapes(prison_id, player_id) VALUES ($1,$2) RETURNING escape_id)
        INSERT INTO escape_events(escape_id, report_json) SELECT escape_id, $3::jsonb FROM e RETURNING escape_id
        """);
    cmd.Parameters.AddWithValue(dto.PrisonId);
    cmd.Parameters.AddWithValue(dto.PlayerId is { } p ? Guid.Parse(p) : DBNull.Value);
    cmd.Parameters.AddWithValue(dto.ReportJson);
    var escapeId = await cmd.ExecuteScalarAsync();

    await nats.PublishAsync("escape.finished", JsonSerializer.SerializeToUtf8Bytes(
        new { escapeId, dto.PrisonId }));
    return Results.Ok(new { escapeId });
});

// ---- server browser (§11.1 Servers) ----

app.MapPost("/servers/heartbeat", async (ServerDto dto) =>
{
    await using var cmd = db.CreateCommand(
        """
        INSERT INTO servers(server_id, prison_id, address, players, last_seen)
        VALUES ($1,$2,$3,$4,now())
        ON CONFLICT (server_id) DO UPDATE SET players=$4, last_seen=now()
        """);
    cmd.Parameters.AddWithValue(dto.ServerId);
    cmd.Parameters.AddWithValue((object?)dto.PrisonId ?? DBNull.Value);
    cmd.Parameters.AddWithValue(dto.Address);
    cmd.Parameters.AddWithValue(dto.Players);
    await cmd.ExecuteNonQueryAsync();
    return Results.Ok();
});

app.MapGet("/servers", async () =>
{
    await using var cmd = db.CreateCommand(
        "SELECT server_id, prison_id, address, players FROM servers WHERE last_seen > now() - interval '2 minutes'");
    var list = new List<object>();
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
        list.Add(new { serverId = r.GetString(0), prisonId = r.IsDBNull(1) ? null : r.GetString(1),
            address = r.GetString(2), players = r.GetInt32(3) });
    return Results.Ok(list);
});

app.Run();

record PlayerDto(string Username);
record FriendDto(string From, string To);
record OfficialDto(string FamilyId, int Generation, string? ParentPrisonId);
record CommunityDto(string FamilyId, string OwnerId, string Visibility, bool ShareEscapeData);
record EscapeDto(string PrisonId, string? PlayerId, string ReportJson);
record ServerDto(string ServerId, string? PrisonId, string Address, int Players);

/// <summary>Thin SQL mapping for <see cref="PrisonRecord"/> — no ORM, explicit columns.</summary>
static class Store
{
    public static async Task Insert(NpgsqlDataSource db, PrisonRecord p)
    {
        await using var cmd = db.CreateCommand(
            """
            INSERT INTO prisons(prison_id, family_id, generation, host_type, status, owner_id,
                visibility, created_at, compromised_at, retire_at, share_escape_data, parent_prison_id)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
            """);
        Bind(cmd, p, insert: true);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task Update(NpgsqlDataSource db, PrisonRecord p)
    {
        await using var cmd = db.CreateCommand(
            """
            UPDATE prisons SET status=$2, compromised_at=$3, retire_at=$4, visibility=$5,
                share_escape_data=$6 WHERE prison_id=$1
            """);
        cmd.Parameters.AddWithValue(p.PrisonId);
        cmd.Parameters.AddWithValue(p.Status.ToString());
        cmd.Parameters.AddWithValue((object?)p.CompromisedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)p.RetireAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue(p.Visibility.ToString());
        cmd.Parameters.AddWithValue(p.ShareEscapeData);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void Bind(NpgsqlCommand cmd, PrisonRecord p, bool insert)
    {
        cmd.Parameters.AddWithValue(p.PrisonId);
        cmd.Parameters.AddWithValue(p.FamilyId);
        cmd.Parameters.AddWithValue(p.Generation);
        cmd.Parameters.AddWithValue(p.HostType.ToString());
        cmd.Parameters.AddWithValue(p.Status.ToString());
        cmd.Parameters.AddWithValue(p.OwnerId is { } o ? Guid.Parse(o) : DBNull.Value);
        cmd.Parameters.AddWithValue(p.Visibility.ToString());
        cmd.Parameters.AddWithValue(p.CreatedAt);
        cmd.Parameters.AddWithValue((object?)p.CompromisedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)p.RetireAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue(p.ShareEscapeData);
        cmd.Parameters.AddWithValue((object?)p.ParentPrisonId ?? DBNull.Value);
        _ = insert;
    }

    public static async Task<PrisonRecord?> Get(NpgsqlDataSource db, string id)
    {
        await using var cmd = db.CreateCommand(
            """
            SELECT prison_id, family_id, generation, host_type, status, owner_id, visibility,
                created_at, compromised_at, retire_at, share_escape_data, parent_prison_id
            FROM prisons WHERE prison_id=$1
            """);
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync())
            return null;
        return new PrisonRecord
        {
            PrisonId = r.GetString(0),
            FamilyId = r.GetString(1),
            Generation = r.GetInt32(2),
            HostType = Enum.Parse<HostType>(r.GetString(3)),
            Status = Enum.Parse<PrisonStatus>(r.GetString(4)),
            OwnerId = r.IsDBNull(5) ? null : r.GetGuid(5).ToString(),
            Visibility = Enum.Parse<PrisonVisibility>(r.GetString(6)),
            CreatedAt = r.GetFieldValue<DateTimeOffset>(7),
            CompromisedAt = r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
            RetireAt = r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9),
            ShareEscapeData = r.GetBoolean(10),
            ParentPrisonId = r.IsDBNull(11) ? null : r.GetString(11),
        };
    }
}
