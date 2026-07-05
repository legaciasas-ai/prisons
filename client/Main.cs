using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Prison.Shared.Utilities;
using Prison.Shared.Visibility;
using Prison.Shared.World;

namespace Prison.Client;

/// <summary>
/// The Godot presentation layer: renders an <see cref="IMatchClient"/> and forwards input
/// to it — nothing else (Pillar #5). The same rendering code serves the embedded local
/// simulation, an online match mirrored from an authoritative server, and a player-hosted
/// listen-server. Environment variables select the mode (a proper menu comes with real UI
/// work): <c>PRISON_SERVER=host[:port]</c> to join a server, <c>PRISON_HOST=[port]</c> to
/// host while playing, <c>PRISON_PLAYER_NAME</c> optional; nothing set = single-player.
/// </summary>
public partial class Main : Node2D
{
    private const float PixelsPerTile = 32f;
    private const float PlayerMaxSight = 12f;
    private const float PlayerDarkSight = 3f;

    private IMatchClient _client = null!;
    private FogOfWarMap? _fog;
    private Label _label = null!;
    private Camera2D _camera = null!;
    private HashSet<TilePos> _visible = [];
    private bool _stairsKeyWasPressed;
    private bool _heatMapKeyWasPressed;
    private bool _showHeatMap;
    private double _arrestFlashSecondsLeft;

    public override void _Ready()
    {
        _label = GetNode<Label>("Hud/StatusLabel");

        var contentRoot = ResolveContentRoot();
        _client = CreateClient(contentRoot);

        _camera = new Camera2D { Zoom = new Vector2(1.6f, 1.6f) };
        AddChild(_camera);
        _camera.MakeCurrent();
    }

    /// <summary>Local single-player unless PRISON_SERVER (join) or PRISON_HOST (host) is set.</summary>
    private static IMatchClient CreateClient(string contentRoot)
    {
        var name = OS.GetEnvironment("PRISON_PLAYER_NAME");
        if (string.IsNullOrWhiteSpace(name))
            name = $"prisoner-{Random.Shared.Next(1000, 9999)}";

        var host = OS.GetEnvironment("PRISON_HOST");
        if (!string.IsNullOrWhiteSpace(host))
            return new HostMatchClient(contentRoot,
                int.TryParse(host, out var hostPort) ? hostPort : 30500, name);

        var server = OS.GetEnvironment("PRISON_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return new LocalMatchClient(contentRoot);

        var parts = server.Split(':', 2);
        var port = parts.Length == 2 && int.TryParse(parts[1], out var p) ? p : 30500;
        var tiles = TileRegistry.LoadFromDirectory(Path.Combine(contentRoot, "tiles"));
        return new RemoteMatchClient(parts[0], port, name, mapId =>
        {
            var mapPath = Path.Combine(contentRoot, "maps", mapId + ".json");
            return File.Exists(mapPath) ? MapDefinition.Load(mapPath).BuildWorld(tiles) : null;
        });
    }

    /// <summary>content/ lives beside the Godot project (repo root), outside res://.</summary>
    private static string ResolveContentRoot()
    {
        var projectDir = ProjectSettings.GlobalizePath("res://");
        var candidate = Path.GetFullPath(Path.Combine(projectDir, "..", "content"));
        return ContentPaths.Resolve(Directory.Exists(candidate) ? candidate : null);
    }

    public override void _Process(double delta)
    {
        _client.Update(delta);

        if (_client.Error is { } error)
        {
            _label.Text = $"Prison — {_client.ModeLabel}\n\n>>> {error} <<<";
            return;
        }

        if (!_client.Ready)
        {
            _label.Text = $"Prison — {_client.ModeLabel}\n\nConnexion au serveur…";
            return;
        }

        ForwardInput();
        _fog ??= new FogOfWarMap(_client.World);

        var position = _client.PlayerPosition;
        var origin = new TilePos((int)Mathf.Floor(position.X), (int)Mathf.Floor(position.Y), position.Floor);
        _visible = FieldOfView.Compute(_client.World, origin,
            VisionParameters.Omnidirectional(PlayerMaxSight, PlayerDarkSight));
        _fog.Update(_visible);

        _camera.Position = new Vector2(position.X, position.Y) * PixelsPerTile;

        if (_client.ConsumeArrestSignal())
            _arrestFlashSecondsLeft = 3.0;
        if (_arrestFlashSecondsLeft > 0)
            _arrestFlashSecondsLeft -= delta;

        _label.Text = $"Prison — {_client.ModeLabel}\n" +
                      $"WASD move · Shift sprint · E stairs" +
                      (_client.PresenceHeat is not null ? " · H heat map\n" : "\n") +
                      $"Floor {position.Floor} · suspicion {_client.Threat:F0}/100 · {Engine.GetFramesPerSecond()} fps" +
                      (_arrestFlashSecondsLeft > 0 ? "\n\n>>> CAUGHT — escorted back to your cell <<<" : "");

        QueueRedraw();
    }

    private void ForwardInput()
    {
        var move = Vector2.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up)) move.Y -= 1;
        if (Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down)) move.Y += 1;
        if (Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left)) move.X -= 1;
        if (Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right)) move.X += 1;

        var stairsPressed = Input.IsPhysicalKeyPressed(Key.E);
        var stairsJustPressed = stairsPressed && !_stairsKeyWasPressed;
        _stairsKeyWasPressed = stairsPressed;

        var heatPressed = Input.IsPhysicalKeyPressed(Key.H);
        if (heatPressed && !_heatMapKeyWasPressed)
            _showHeatMap = !_showHeatMap;
        _heatMapKeyWasPressed = heatPressed;

        _client.ApplyInput(move.X, move.Y, Input.IsPhysicalKeyPressed(Key.Shift), stairsJustPressed);
    }

    public override void _Draw()
    {
        if (!_client.Ready || _fog is null)
            return;

        var playerPosition = _client.PlayerPosition;
        var floorIndex = playerPosition.Floor;
        var world = _client.World;
        var floor = world.Floor(floorIndex);

        for (var y = 0; y < floor.Height; y++)
        {
            for (var x = 0; x < floor.Width; x++)
            {
                var pos = new TilePos(x, y, floorIndex);
                var fog = _fog.StateAt(pos);
                if (fog == FogState.Unseen)
                    continue;

                var wall = floor.GetWallTile(x, y);
                var tileId = wall != TileRegistry.EmptyId ? wall : floor.GetFloorTile(x, y);
                if (tileId == TileRegistry.EmptyId)
                    continue;

                var color = ColorFor(world.Tiles.Get(tileId).Id);
                if (fog == FogState.Visible)
                {
                    var light = 0.45f + 0.55f * floor.GetLight(x, y);
                    color = new Color(color.R * light, color.G * light, color.B * light);
                }
                else
                {
                    var gray = (color.R + color.G + color.B) / 3f * 0.35f + 0.08f;
                    color = new Color(gray, gray, gray);
                }

                DrawRect(new Rect2(x * PixelsPerTile, y * PixelsPerTile, PixelsPerTile, PixelsPerTile), color);
            }
        }

        if (_showHeatMap)
            DrawHeatMap(floorIndex);

        DrawOtherActors(floorIndex);

        DrawCircle(new Vector2(playerPosition.X, playerPosition.Y) * PixelsPerTile,
            PixelsPerTile * 0.32f, new Color(1f, 0.65f, 0.15f));
    }

    /// <summary>Debug overlay (Phase 4): where the prisoner has been, hottest tiles reddest.</summary>
    private void DrawHeatMap(int floorIndex)
    {
        if (_client.PresenceHeat is not { Max: > 0 } heat)
            return;
        foreach (var (tile, count) in heat.Counts)
        {
            if (tile.Floor != floorIndex)
                continue;
            var intensity = Mathf.Sqrt(count / (float)heat.Max); // sqrt: keep low counts readable
            DrawRect(new Rect2(tile.X * PixelsPerTile, tile.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile),
                new Color(1f, 0.1f, 0.05f, 0.15f + 0.5f * intensity));
        }
    }

    /// <summary>Actors render only if the *player* can physically see their tile — same fairness both ways.</summary>
    private void DrawOtherActors(int floorIndex)
    {
        foreach (var actor in _client.OtherActors)
        {
            if (actor.Floor != floorIndex)
                continue;
            var tile = new TilePos((int)Mathf.Floor(actor.X), (int)Mathf.Floor(actor.Y), actor.Floor);
            if (!_visible.Contains(tile))
                continue;

            var center = new Vector2(actor.X, actor.Y) * PixelsPerTile;
            var color = actor.IsGuard
                ? new Color(0.25f, 0.45f, 0.95f)   // guards: blue
                : new Color(0.95f, 0.45f, 0.25f);  // fellow prisoners: red-orange
            DrawCircle(center, PixelsPerTile * 0.32f, color);
            // A short heading line so the vision-cone direction is readable.
            var dir = new Vector2(Mathf.Cos(actor.FacingRadians), Mathf.Sin(actor.FacingRadians));
            DrawLine(center, center + dir * PixelsPerTile * 0.6f, Colors.White, 2f);
        }
    }

    private static Color ColorFor(string tileId) => tileId switch
    {
        "concrete_floor" => new Color(0.62f, 0.62f, 0.59f),
        "metal_floor" => new Color(0.48f, 0.54f, 0.58f),
        "dirt" => new Color(0.54f, 0.43f, 0.30f),
        "grass" => new Color(0.35f, 0.54f, 0.28f),
        "stairs" => new Color(0.85f, 0.78f, 0.35f),
        "concrete_wall" => new Color(0.24f, 0.24f, 0.23f),
        "glass_wall" => new Color(0.62f, 0.83f, 0.88f),
        "chain_fence" => new Color(0.70f, 0.72f, 0.74f),
        "tunnel" => new Color(0.35f, 0.26f, 0.18f),
        "door_closed" => new Color(0.55f, 0.35f, 0.16f),
        "door_open" => new Color(0.75f, 0.58f, 0.35f),
        "brick_wall" => new Color(0.48f, 0.23f, 0.18f),
        "steel_wall" => new Color(0.35f, 0.40f, 0.44f),
        "sandstone_wall" => new Color(0.66f, 0.54f, 0.31f),
        "white_wall" => new Color(0.80f, 0.80f, 0.78f),
        "wood_floor" => new Color(0.61f, 0.45f, 0.29f),
        "tile_floor" => new Color(0.82f, 0.83f, 0.80f),
        "sand_floor" => new Color(0.79f, 0.66f, 0.42f),
        "cracked_floor" => new Color(0.52f, 0.52f, 0.49f),
        "bush" => new Color(0.25f, 0.42f, 0.20f),
        "tree" => new Color(0.15f, 0.30f, 0.13f),
        _ => new Color(0.05f, 0.05f, 0.06f),
    };

    public override void _ExitTree()
    {
        _client.Dispose();
    }
}
