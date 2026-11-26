# 3 on 3 NHL Arcade Recomp (DockingOnly fork)

[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)
![Windows x64](https://img.shields.io/badge/Windows-x64-blue)
![Status: work in progress](https://img.shields.io/badge/status-work%20in%20progress-yellow)
[![Forked from Wndwsqrrl/3on3NHL-recomp](https://img.shields.io/badge/fork%20of-Wndwsqrrl%2F3on3NHL--recomp-orange)](https://github.com/Wndwsqrrl/3on3NHL-recomp)

The 2009 Xbox Live Arcade hockey game **3 on 3 NHL Arcade**, running natively on PC. The
game's original Xbox 360 (PowerPC) executable is statically recompiled into C++ with the
[ReXGlue SDK](https://github.com/rexglue/rexglue-sdk) and built as a normal Windows program,
so no emulator runs while you play.

This repository is a **fork** of [Wndwsqrrl/3on3NHL-recomp](https://github.com/Wndwsqrrl/3on3NHL-recomp),
which did the port itself. This fork works on visuals and performance: higher resolutions,
frame rate, upscaling and controller prompts. See [Credits](#credits) for everyone whose work
this builds on.

> [!WARNING]
> **This fork is a work in progress.** Apart from a steady 60 fps at native resolution, every
> feature below is still being worked on and may be incomplete or broken.

> [!IMPORTANT]
> **No game files are included.** This repository and anything built from it contain no code,
> art, audio or other data from 3 on 3 NHL Arcade. To play, you need your own legally obtained
> copy of the game's XBLA package (title ID `58410975`).
>
> **This project is not affiliated with, endorsed by, sponsored by or connected to Electronic
> Arts Inc. (EA), EA Sports, the National Hockey League (NHL), the NHL Players' Association,
> Microsoft or any NHL team.** All trademarks belong to their respective owners.

![Gameplay](docs/images/gameplay.jpg)

---

## Status

Legend: ✅ done · 🚧 work in progress

| Feature | Status |
|---|---|
| 60 fps at native resolution | ✅ |
| Menus, intro movies and full matches | 🚧 |
| Xbox and PlayStation controllers | 🚧 |
| Achievements (**Achievements** menu item or **F7**, saved between sessions) | 🚧 |
| Launcher that sets the game up from your package | 🚧 |
| Higher internal resolution | 🚧 |
| Android (arm64) build | 🚧 |

<p align="center"><img src="docs/images/main-menu.jpg" alt="Main menu" width="720"></p>

---

## How this fork differs from upstream

The upstream project gave up on higher internal resolutions, because they broke the game's
text. Running the game sharper, smoother and on modern hardware is this fork's main goal.

| Area | Upstream | This fork |
|---|---|---|
| Internal resolution above 1x | Not planned (text and some textures break) | 🚧 Text fixed by [ReXGlue patch 0004](patches/rexglue/README.md); 2x and 3x run but drop below 60 fps for now |
| Frame rate | Native 60 fps | 🚧 Live frame stats in the F3 overlay; working toward going above 60 |
| DLSS / upscaling | Not on the roadmap | 🚧 Planned |
| Resolution options | Not on the roadmap | 🚧 Planned, in game and in the launcher |
| Controller button prompts | Xbox prompts only | 🚧 Planned: prompts that match the pad you use (Xbox or PlayStation) |
| Performance | Not on the roadmap | 🚧 Profiling the GPU work behind each frame |

---

## Playing it

### Option A: prebuilt release (upstream)

This fork has **no releases yet**. The upstream project publishes Windows builds, which do
**not** include this fork's changes:

1. Get the [latest upstream release](https://github.com/Wndwsqrrl/3on3NHL-recomp/releases/latest)
   and unzip it.
2. Put your XBLA package in the same folder: either the whole `58410975` folder or just the
   file inside `58410975\000D0000\` (the file with a long hex name).
3. Open `NHL 3on3 Launcher.exe`, click **Set up game**, then **Play**. The launcher checks
   that the package really is 3 on 3 NHL Arcade and unpacks it into a `game` folder.

### Option B: build this fork

[BUILDING.md](BUILDING.md) covers the Windows and Android builds. In short, you need:

- Clang 20 or newer and CMake (Visual Studio Build Tools with its Clang component works)
- ReXGlue SDK v0.10.0 with the patches in [`patches/rexglue`](patches/rexglue/README.md)
  applied (`0004` is the one that fixes text at higher resolutions)
- Your own copy of the game, which codegen reads from

### What your PC needs

| Component | Minimum |
|---|---|
| OS | Windows 10 or 11 (64-bit) |
| CPU | x86-64 with SSE4.2; AVX is not required |
| GPU | Any card with Direct3D 12 |
| RAM | 4 GB |
| Storage | About 350 MB with the game unpacked |

Raising the internal resolution takes much more GPU power than 1x. See
[Known issues](#known-issues).

---

## Roadmap

Everything on the roadmap is a work in progress (🚧).

### This fork's milestones (graphics and performance)

These come first, in roughly this order:

1. 🚧 **Higher internal resolution.** The text bug is fixed (patch 0004 ports the corrected
   `resolve_downscale` shader from Xenia Canary). Next is making 2x and 3x hold 60 fps.
2. 🚧 **Overall performance.** Find out where the GPU time goes in each frame (about 10 ms at
   720p today) and cut it.
3. 🚧 **Above 60 fps.** The game's logic runs at a fixed 60 steps a second, so uncapping the
   frame rate just speeds the game up. Going above 60 needs interpolated frames or decoupling
   rendering from the logic.
4. 🚧 **Resolution options** you can change without editing config files.
5. 🚧 **DLSS** (and possibly other upscalers) on supported GPUs.
6. 🚧 **Controller-aware button prompts** that show Xbox or PlayStation buttons to match the
   connected pad.

### Carried over from upstream

- 🚧 **Online play:** bring back the game's online mode through a community server and make
  the two online achievements (*Let's Play* and *Team Player*) earnable.
- 🚧 **Mods:** current rosters (names, numbers, ratings, player types and portraits), new team
  logos and artwork, more than the original 43 selectable players, and new uses for the
  Xbox Live menu. Mods would only contain changes applied to your own copy, never EA's files.
- 🚧 **Launcher:** mod install and selection, a settings page, an achievements page that works
  without starting the game, and update checks.
- 🚧 **Android:** a first-run importer for the XBLA package to replace the current `adb push`
  steps.

---

## Known issues

- **Higher resolutions are slow.** At 2x and 3x the frame rate snaps from 60 to 30 with vsync,
  even on high-end GPUs. This is what the performance milestone is about.
- **SmartScreen warning.** The executables aren't code-signed. Click **More info**, then
  **Run anyway**.
- **PlayStation controller not detected.** Steam Input may have taken it over. Quit Steam and
  start the game again.
- **The launcher can't find your package.** Click **Browse** and select the package file
  itself, inside `58410975\000D0000\`.

**Where files are kept:** settings are in `nhl3on3.toml` next to the executable (command-line
flags override it). Achievements and the shader cache are in `Documents\nhl3on3\`. Logs are in
the `logs` folder next to the executable.

---

## Credits

This fork would not exist without:

- **[Wndwsqrrl](https://github.com/Wndwsqrrl)**: author of
  [3on3NHL-recomp](https://github.com/Wndwsqrrl/3on3NHL-recomp), the original port. That
  includes the recompilation setup, launcher, achievements, ReXGlue patches 0001 to 0003, the
  Android build and the README this one replaces. This fork keeps their MIT license and
  copyright notice.
- **[startupandhold](https://github.com/startupandhold)**: author of
  [3on3ArcadeRecomp](https://github.com/startupandhold/3on3ArcadeRecomp), which parts of the
  code in `src/` are ported from (MIT). Their license is in
  [`dist/licenses/3on3ArcadeRecomp.txt`](dist/licenses/3on3ArcadeRecomp.txt).
- **[ReXGlue](https://github.com/rexglue/rexglue-sdk)**: the static recompilation SDK and
  runtime the port is built on (BSD 3-Clause).
- **[Xenia](https://xenia.jp)** and **[Xenia Canary](https://github.com/xenia-canary/xenia-canary)**:
  the Xbox 360 emulator projects whose code ReXGlue builds on. Patch 0004's shader comes from
  Xenia Canary (BSD 3-Clause).
- All other third-party libraries (FFmpeg, SDL 3, Dear ImGui, spdlog, stb_image and more) are
  listed with their licenses in [THIRD_PARTY.md](THIRD_PARTY.md).

## License

The source code in this repository is released under the [MIT License](LICENSE). It keeps the
original copyright notice from upstream and adds one for this fork's changes. Third-party
components keep their own licenses: see [THIRD_PARTY.md](THIRD_PARTY.md) and
[`dist/licenses/`](dist/licenses).

**3 on 3 NHL Arcade, its code and all of its assets are the property of Electronic Arts Inc.**
NHL, the NHL Shield and NHL team names and logos are trademarks of the National Hockey League.
Xbox and Xbox Live are trademarks of Microsoft. None of these are included in or licensed by
this project. This is an unofficial, non-commercial fan project. It is **not affiliated with
or endorsed by EA, the NHL or Microsoft in any way**. Please support official releases.
