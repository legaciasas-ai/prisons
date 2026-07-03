# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project state

Here (in CONVERSATION.md) is the conversation i had with a friend, it describes almost exactly what i want for our game. Read it, make a complete and accurate and explicit and big plan to make this project live and write it under PLAN.md.

## Engine configuration (from `project.godot`)

- **Engine**: Godot 4.7, Forward+ rendering.
- **.NET/C# enabled**: `[dotnet] project/assembly_name="prison"` — this project is set up for C# scripting.
  No `.csproj`/`.sln` exists yet; Godot generates it the first time a C# script is added or the project is
  opened/built via the editor.
- **Physics**: 3D physics engine is set to **Jolt Physics** (not the default Godot Physics/GodotJolt-3D).
  Keep this in mind when writing physics-related code (API differences vs. default engine in edge cases).
- **Display**: `window/stretch/mode="canvas_items"`, `window/stretch/aspect="expand"` — a 2D/UI-friendly
  stretch config, though 3D physics (Jolt) is enabled, suggesting a project that may mix 2D presentation
  with 3D physics/gameplay.
- **Rendering driver override**: `d3d12` is forced for the Windows rendering device driver.

## Common commands

There is no build system, task runner, or test suite in this repo yet — this is a Godot project, so the
primary workflows are through the Godot editor/CLI:

- Open the project in the editor: `godot4 --editor --path /home/titouan/prison` (or `godot --editor .` if
  the binary is named `godot`).
- Run the project headlessly: `godot4 --path /home/titouan/prison`.
- If/when C# scripts are added, build the generated C# project with `dotnet build` from the project root
  (once Godot has generated the `.csproj`/`.sln` on first open).

No Godot binary is currently installed in this environment (`godot`/`godot4` not found on PATH) — verify
availability before assuming these commands can run directly.
