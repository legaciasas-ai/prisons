using System.Collections.Generic;
using System.IO;
using Godot;
using Prison.Shared;
using Prison.Shared.AI;
using Prison.Shared.ECS.Components;
using Prison.Shared.Events;
using Prison.Shared.Utilities;
using Prison.Shared.Visibility;
using Prison.Shared.World;

namespace Prison.Client;

/// <summary>
/// Local single-player bootstrap (PLAN §4.1): embeds the Core Simulation directly and renders it.
/// Phase 2 scope: walk (WASD) / sprint (Shift) / stairs (E) through the test prison while
/// guards patrol, hear, investigate, chase and arrest — all through the shared, physically
/// grounded Staff AI. This class only renders and forwards input (Pillar #5).
/// </summary>
public partial class Main : Node2D
{
    private const float PixelsPerTile = 32f;
    private const float PlayerMaxSight = 12f;
    private const float PlayerDarkSight = 3f;

    private Simulation _simulation = null!;
    private WorldGrid _world = null!;
    private FogOfWarMap _fog = null!;
    private Arch.Core.Entity _player;
    private Label _label = null!;
    private Camera2D _camera = null!;
    private HashSet<TilePos> _visible = [];
    private bool _stairsKeyWasPressed;
    private bool _heatMapKeyWasPressed;
    private bool _showHeatMap;
    private double _arrestFlashSecondsLeft;
    private Prison.Shared.Telemetry.EscapeRecorder _escape = null!;

    public override void _Ready()
    {
        _label = GetNode<Label>("Hud/StatusLabel");

        var contentRoot = ResolveContentRoot();
        var tiles = TileRegistry.LoadFromDirectory(Path.Combine(contentRoot, "tiles"));
        var items = Prison.Shared.Items.ItemRegistry.LoadFromDirectory(Path.Combine(contentRoot, "items"));
        var recipes = Prison.Shared.Items.RecipeDefinition.LoadFromDirectory(Path.Combine(contentRoot, "recipes"));
        var map = MapDefinition.Load(Path.Combine(contentRoot, "maps", "test_prison.json"));
        _world = map.BuildWorld(tiles);
        _fog = new FogOfWarMap(_world);

        var match = MatchFactory.Create(_world, map, items, recipes);
        _simulation = match.Simulation;
        _player = match.Player;
        _escape = match.Escape;

        _simulation.Events.Subscribe<ArrestEvent>(evt =>
        {
            if (evt.Prisoner == _player)
                _arrestFlashSecondsLeft = 3.0;
        });

        _camera = new Camera2D { Zoom = new Vector2(1.6f, 1.6f) };
        AddChild(_camera);
        _camera.MakeCurrent();
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
        ForwardInput();
        _simulation.Advance(delta);

        var position = _simulation.World.Get<Position>(_player);
        var origin = new TilePos((int)Mathf.Floor(position.X), (int)Mathf.Floor(position.Y), position.Floor);
        _visible = FieldOfView.Compute(_world, origin,
            VisionParameters.Omnidirectional(PlayerMaxSight, PlayerDarkSight));
        _fog.Update(_visible);

        _camera.Position = new Vector2(position.X, position.Y) * PixelsPerTile;

        var threat = _simulation.World.Get<ThreatScore>(_player).Threat;
        if (_arrestFlashSecondsLeft > 0)
            _arrestFlashSecondsLeft -= delta;

        _label.Text = $"Prison — Phase 4: telemetry\n" +
                      $"WASD move · Shift sprint · E stairs · H heat map\n" +
                      $"Floor {position.Floor} · suspicion {threat:F0}/100 · {Engine.GetFramesPerSecond()} fps" +
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

        ref var input = ref _simulation.World.Get<PlayerInput>(_player);
        input.MoveX = move.X;
        input.MoveY = move.Y;
        input.Running = Input.IsPhysicalKeyPressed(Key.Shift);
        input.UseStairs |= stairsJustPressed; // sticky until a tick consumes it
    }

    public override void _Draw()
    {
        var playerPosition = _simulation.World.Get<Position>(_player);
        var floorIndex = playerPosition.Floor;
        var floor = _world.Floor(floorIndex);

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

                var color = ColorFor(_world.Tiles.Get(tileId).Id);
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

        DrawGuards(floorIndex);

        DrawCircle(new Vector2(playerPosition.X, playerPosition.Y) * PixelsPerTile,
            PixelsPerTile * 0.32f, new Color(1f, 0.65f, 0.15f));
    }

    /// <summary>Debug overlay (Phase 4): where the prisoner has been, hottest tiles reddest.</summary>
    private void DrawHeatMap(int floorIndex)
    {
        var heat = _escape.Presence;
        if (heat.Max == 0)
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

    /// <summary>Guards render only if the *player* can physically see their tile — same fairness both ways.</summary>
    private void DrawGuards(int floorIndex)
    {
        var query = new Arch.Core.QueryDescription().WithAll<GuardTag, Position, Facing>();
        _simulation.World.Query(in query, (ref Position position, ref Facing facing) =>
        {
            if (position.Floor != floorIndex)
                return;
            var tile = new TilePos((int)Mathf.Floor(position.X), (int)Mathf.Floor(position.Y), position.Floor);
            if (!_visible.Contains(tile))
                return;

            var center = new Vector2(position.X, position.Y) * PixelsPerTile;
            DrawCircle(center, PixelsPerTile * 0.32f, new Color(0.25f, 0.45f, 0.95f));
            // A short heading line so the vision-cone direction is readable.
            var dir = new Vector2(Mathf.Cos(facing.Radians), Mathf.Sin(facing.Radians));
            DrawLine(center, center + dir * PixelsPerTile * 0.6f, Colors.White, 2f);
        });
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
        _simulation.Dispose();
    }
}
