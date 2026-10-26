# SeedLab.WorldGen (vendored)

An offline, bit-exact port of Valheim 1.0's `WorldGenerator` (biomes, terrain heights, lakes, rivers and
streams) together with `UnityEngine.Random` and `Mathf.PerlinNoise`. VSG uses it to draw a world's map in the
app from its seed.

- Upstream: https://github.com/DoomMachine/Valheim-SeedLab, `src/SeedLab.WorldGen`
- Commit: `c990746e52e2abdd4963ec63e44e6c0115404d46` (2026-10-02)
- Licence: MIT, copyright (c) 2026 DoomMachine; see [LICENSE](LICENSE). `Noise/` reproduces part of
  FastNoise by Jordan Peck (MIT, https://github.com/Auburn/FastNoise_CSharp).

The sources are copied unchanged so they can be re-synced; only the project file is VSG's. To update, copy
`src/SeedLab.WorldGen/*.cs` (and its subfolders) from a newer upstream commit and update the commit above.

Valheim is a trademark of Iron Gate AB. This code contains no game assets.
