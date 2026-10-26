# Third-party notices

ValheimServerGUI is licensed under the GNU GPL v3 (see [LICENSE](LICENSE)). It includes the following code under
other, GPL-compatible licences.

## SeedLab.WorldGen (MIT)

`src/ThirdParty/SeedLab.WorldGen` is vendored from [Valheim-SeedLab](https://github.com/DoomMachine/Valheim-SeedLab)
by DoomMachine: an offline port of Valheim's world generator, used by the World Map tab to draw a world from its seed.
Copyright (c) 2026 DoomMachine, released under the MIT License; the full text is in
[src/ThirdParty/SeedLab.WorldGen/LICENSE](src/ThirdParty/SeedLab.WorldGen/LICENSE).

Its `Noise/` folder reproduces part of [FastNoise](https://github.com/Auburn/FastNoise_CSharp) by Jordan Peck,
Copyright (c) 2016 Jordan Peck, also released under the MIT License.

## BepInEx (MIT)

`src/ThirdParty/BepInEx/BepInEx.dll` is the unmodified `BepInEx/core/BepInEx.dll` from the
[BepInEx 5.4.23.2 release](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.2), Copyright (c) 2018 Bepis,
released under the MIT License (full text in [src/ThirdParty/BepInEx/LICENSE](src/ThirdParty/BepInEx/LICENSE)). It
is only a compile-time reference for the server plugin (`src/ValheimServerGUI.ServerPlugin`) and is never shipped:
the plugin runs on a server where the user installed BepInEx. Unity's assemblies are referenced the same way, from
the `UnityEngine.Modules` package, at build time only.

Valheim is a trademark of Iron Gate AB. This project is not affiliated with or endorsed by Iron Gate or Coffee Stain,
and contains no game assets.
