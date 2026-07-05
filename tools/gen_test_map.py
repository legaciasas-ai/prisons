#!/usr/bin/env python3
"""Regenerates content/maps/test_prison.json (the Phase 1 hand-authored test map).
Run from anywhere: python3 tools/gen_test_map.py"""
import json

W, H = 38, 22

def blank(ch=' '):
    return [[ch] * W for _ in range(H)]

# ---------- Floor 0: yard + ground floor ----------
f0 = blank(',')  # grass everywhere inside

# fence perimeter
for x in range(W):
    f0[0][x] = 'f'
    f0[H - 1][x] = 'f'
for y in range(H):
    f0[y][0] = 'f'
    f0[y][W - 1] = 'f'

# dirt path around building
for y in range(2, 20):
    for x in range(2, 36):
        if 3 <= x <= 28 and 2 <= y <= 18:
            f0[y][x] = 'd'

# building shell x4..27, y3..17
def rect(grid, x0, y0, x1, y1, wall='#', floor='.'):
    for y in range(y0, y1 + 1):
        for x in range(x0, x1 + 1):
            grid[y][x] = floor
    for x in range(x0, x1 + 1):
        grid[y0][x] = wall
        grid[y1][x] = wall
    for y in range(y0, y1 + 1):
        grid[y][x0] = wall
        grid[y][x1] = wall

rect(f0, 4, 3, 27, 17)

# cells along the top: interiors y4..5, dividing walls, fronts at y6 with doors
for wx in (7, 10, 13, 16, 19, 22):
    f0[4][wx] = '#'
    f0[5][wx] = '#'
for x in range(5, 23):
    f0[6][x] = '#'
for door in (5, 8, 11, 14, 17, 20):
    f0[6][door] = '.'
# stairs room front (x23..26) stays open at y6

# corridor y7..8 already floor

# wall y9 with door into common room + glass wall into stairs room
for x in range(5, 27):
    f0[9][x] = '#'
f0[9][15] = '.'
f0[9][16] = '.'
for x in (23, 24, 25):
    f0[9][x] = 'g'

# stairs room x23..26 y10..13, west wall x22 with door at y11
for y in (10, 11, 12, 13):
    f0[y][22] = '#'
f0[11][22] = '.'
f0[12][25] = 's'

# wall y14 with door at x8..9
for x in range(5, 27):
    f0[14][x] = '#'
f0[14][8] = '.'
f0[14][9] = '.'

# workshop y15..16 metal floor
for y in (15, 16):
    for x in range(5, 27):
        if f0[y][x] == '.':
            f0[y][x] = 'm'

# exterior door to yard in south wall
f0[17][12] = '.'
f0[17][13] = '.'

# ---------- Floor 1: offices over the building footprint only ----------
f1 = blank(' ')
rect(f1, 4, 3, 27, 17)

# two offices y4..8 split by wall x14, glass on the corridor side
for y in range(4, 9):
    f1[y][14] = '#'
for x in range(5, 27):
    f1[9][x] = '#'
f1[9][8] = '.'
f1[9][20] = '.'
for x in (11, 12, 17, 18):
    f1[9][x] = 'g'

# landing corridor y10..13 open; stairs at same spot as below
f1[12][25] = 's'

# storage y15..16 behind wall y14 with door x10..11
for x in range(5, 27):
    f1[14][x] = '#'
f1[14][10] = '.'
f1[14][11] = '.'
for y in (15, 16):
    for x in range(5, 27):
        if f1[y][x] == '.':
            f1[y][x] = 'm'

rows0 = [''.join(r) for r in f0]
rows1 = [''.join(r) for r in f1]
assert all(len(r) == W for r in rows0 + rows1)

doc = {
    "id": "test_prison",
    "display_name": "Test Prison (hand-authored, Phase 1)",
    "legend": {
        "#": {"wall": "concrete_wall", "floor": "concrete_floor"},
        ".": {"floor": "concrete_floor"},
        "m": {"floor": "metal_floor"},
        "s": {"floor": "stairs"},
        "g": {"wall": "glass_wall", "floor": "concrete_floor"},
        "f": {"wall": "chain_fence", "floor": "dirt"},
        ",": {"floor": "grass"},
        "d": {"floor": "dirt"},
        " ": {},
    },
    "floors": [
        {"ambient_light": 0.9, "rows": rows0},
        {"ambient_light": 0.3, "rows": rows1},
    ],
    "stairs": [
        {"floor_a": 0, "x_a": 25, "y_a": 12, "floor_b": 1, "x_b": 25, "y_b": 12},
    ],
    "lights": [
        {"floor": 1, "x": 7, "y": 6, "radius": 6, "intensity": 0.8},
        {"floor": 1, "x": 20, "y": 11, "radius": 6, "intensity": 0.8},
    ],
    "zones": [
        # Stair room on the ground floor (behind the glass wall).
        {"id": "stair_room", "kind": "restricted", "floor": 0, "x0": 23, "y0": 10, "x1": 26, "y1": 13},
        # The whole upper floor is staff-only.
        {"id": "upper_offices", "kind": "restricted", "floor": 1, "x0": 4, "y0": 3, "x1": 27, "y1": 17},
    ],
    "guards": [
        # Ground-floor guard: loops the corridor and the common room.
        {"floor": 0, "x": 16, "y": 12, "patrol": [[6, 7], [20, 7], [16, 12], [6, 12]]},
        # Upper-floor guard: slow loop of the office landing.
        {"floor": 1, "x": 6, "y": 11, "patrol": [[6, 11], [20, 11], [24, 13], [10, 12]]},
    ],
    "player_spawn": {"floor": 0, "x": 5, "y": 4},
}

with open(str(__import__('pathlib').Path(__file__).resolve().parent.parent / 'content/maps/test_prison.json'), 'w') as fp:
    json.dump(doc, fp, indent=2)
    fp.write('\n')

for i, rows in enumerate((rows0, rows1)):
    print(f'--- floor {i} ---')
    print('\n'.join(rows))
