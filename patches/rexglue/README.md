# ReXGlue patches

Fixes to [ReXGlue](https://github.com/rexglue/rexglue-sdk) v0.10.0 (`c94f5eb`) that this port
needs. Apply them to the SDK source before building it (see [BUILDING.md](../../BUILDING.md)):

```
cd rexglue-sdk
git apply <path-to-this-repo>/patches/rexglue/0001-input-system-lock.patch
git apply <path-to-this-repo>/patches/rexglue/0002-codegen-vpk-unsigned-alias.patch
git apply <path-to-this-repo>/patches/rexglue/0003-android-support.patch
git apply <path-to-this-repo>/patches/rexglue/0004-scaled-resolve-downscale-layout.patch
git apply <path-to-this-repo>/patches/rexglue/0005-wait-reg-mem-spin-before-sleep.patch
git apply <path-to-this-repo>/patches/rexglue/0006-texture-watch-race.patch
```

`0003` is only needed for the Android (arm64) build; the Windows build works without it.

| Patch | Symptom without it | Cause |
|---|---|---|
| `0001-input-system-lock.patch` | Game hangs on the loading screen after pressing Start (Debug builds show "vector iterators incompatible" in `rexruntimed.dll`). | `InputSystem` has no locking; the game polls input from two threads while `RefreshDevices()` rebuilds the device list. Adds a mutex. |
| `0002-codegen-vpk-unsigned-alias.patch` | Intro movies show green/orange striped corruption. | Codegen for `vpkuwus`/`vpkuhus` writes the destination element by element, corrupting the result when it is also a source register (as in the VP6 decoder). Builds into a temporary instead. Rerun codegen after applying. |
| `0003-android-support.patch` | The SDK does not build or run on Android. | Fills in ReXGlue's Android (arm64) support: the Vulkan `ANativeWindow` surface, a pthread-backed fiber backend (Bionic has no `ucontext`), `ASharedMemory`-backed guest memory (`/dev/ashmem` is gone on modern Android), loading the GPU plugin by soname, logging to logcat, and assorted libc++/Bionic build fixes. Much of this was already stubbed upstream. Rerun codegen after applying (it refreshes `generated/rexglue.cmake`). |
| `0004-scaled-resolve-downscale-layout.patch` | With `resolution_scale` above 1, text the game builds at runtime (menu labels, "ADVANCE", the faceoff countdown, the scoreboard) loses letters or goes blank. | The text is drawn on the GPU and read back to the CPU. With resolution scaling, the readback shrinks the scaled resolve back to 1x, but the shader treated the scaled data as a flat `scale_x * scale_y` copy of each pixel, while scaled resolves are stored in Xenia's group-packed layout, so it read the wrong pixels. Ports the corrected `resolve_downscale` shader from Xenia Canary (HLSL and GLSL) and regenerates the D3D12 and SPIR-V bytecode. The Vulkan build is compiled and validated but not yet tested in game. |
| `0005-wait-reg-mem-spin-before-sleep.patch` | Matches at `resolution_scale=3` run a few fps slower than they need to. | The game makes the GPU command stream wait (`WAIT_REG_MEM`) for the CPU to finish with data it just read back. Each time the value wasn't there yet, the command processor slept at least ~1 ms, usually far longer than the CPU needed. Adds `wait_reg_mem_spin_us` (default 2000): poll with yields for that long before sleeping. |
| `0006-texture-watch-race.patch` | In about two of three matches the scoreboard clock and score are blank for the whole match, and other text loses letters ("ADVANCE" shows as "A&nbsp;&nbsp;&nbsp;ANCE"), at any resolution. | The game writes glyphs into a font texture with the CPU as text first needs them. A CPU write landing between a texture's upload and the start of its write watch invalidated and unprotected the pages without telling the texture, which then kept the old data. Leaves such a texture outdated so it reloads on its next use. |

Upstream: the movie bug is [rexglue-sdk#364](https://github.com/rexglue/rexglue-sdk/issues/364);
[rexglue-sdk#426](https://github.com/rexglue/rexglue-sdk/pull/426) includes an equivalent fix.
Remove a patch once a ReXGlue release contains its fix.
