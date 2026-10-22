# Third-party notices

OTH-LAN's own source code is MIT-licensed (see `LICENSE`). This repository contains **no** third-party binaries and
**no** files from Over The Hill.

The release archives contain third-party components:
- `OTH-LAN-Setup.exe` embeds the unmodified official BepInEx build zip and is a self-contained .NET 8 app.
- `OTH-LAN-<ver>-manual.zip` contains the same BepInEx files, unpacked.

The licences below were checked on 2026-10-22 against each project's repository (GitHub licence API plus the LICENSE
file itself). Versions are those found in the shipped binaries. The full licence texts are in `LICENSES/`.

## Shipped components

| Component | Version shipped | Files | Licence | Source | Text |
|---|---|---|---|---|---|
| BepInEx (Unity IL2CPP) | 6.0.0-be.788+5b766a3 | `BepInEx/core/BepInEx.*.dll` | LGPL-2.1 | https://github.com/BepInEx/BepInEx | `LICENSES/LGPL-2.1-BepInEx.txt` |
| UnityDoorstop | 4.5.0 | `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` | LGPL-2.1 | https://github.com/NeighTools/UnityDoorstop | `LICENSES/LGPL-2.1-UnityDoorstop.txt` |
| Il2CppInterop | 1.5.3 | `Il2CppInterop.*.dll` | LGPL-3.0 (the file also includes GPL-3.0, which LGPL-3.0 incorporates) | https://github.com/BepInEx/Il2CppInterop | `LICENSES/LGPL-3.0-Il2CppInterop.txt` |
| HarmonyX | 2.10.2 | `0Harmony.dll` | MIT | https://github.com/BepInEx/HarmonyX | `LICENSES/MIT-HarmonyX.txt` |
| MonoMod | RuntimeDetour/Utils 22.07.31.01, Backports 1.1.2, ILHelpers | `MonoMod.*.dll` | MIT | https://github.com/MonoMod/MonoMod | `LICENSES/MIT-MonoMod.txt` |
| Cpp2IL, LibCpp2IL, WasmDisassembler, StableNameDotNet | 2022.1.0-development.1452 | `Cpp2IL.Core.dll`, `LibCpp2IL.dll`, `WasmDisassembler.dll`, `StableNameDotNet.dll` | MIT (one repository) | https://github.com/SamboyCoding/Cpp2IL | `LICENSES/MIT-Cpp2IL.txt` |
| Disarm | 2022.1.0-master.99 | `Disarm.dll` | MIT | https://github.com/SamboyCoding/Disarm | `LICENSES/MIT-Disarm.txt` |
| Iced | 1.21.0 | `Iced.dll` (the OTH-LAN plugin also calls it at runtime for code signatures) | MIT | https://github.com/icedland/iced | `LICENSES/MIT-iced.txt` |
| AsmResolver | 6.0.0-beta.5 | `AsmResolver*.dll` | MIT | https://github.com/Washi1337/AsmResolver | `LICENSES/MIT-AsmResolver.txt` |
| Mono.Cecil | 0.11.4 | `Mono.Cecil*.dll` | MIT | https://github.com/jbevain/cecil | `LICENSES/MIT-Cecil.txt` |
| Dobby (BepInEx fork) | as in be.788 | `dobby.dll` | Apache-2.0 | https://github.com/BepInEx/Dobby | `LICENSES/Apache-2.0-Dobby.txt` |
| Gee.External.Capstone (Capstone.NET) | 2.3.2 | `Gee.External.Capstone.dll` | MIT | https://github.com/9ee1/Capstone.NET | `LICENSES/MIT-CapstoneNET.txt` |
| SemanticVersioning | 2.0.2 | `SemanticVersioning.dll` | MIT | https://github.com/adamreeve/semver.net | `LICENSES/MIT-semver.txt` |
| AssetRipper.Primitives | 3.2.0 | `AssetRipper.Primitives.dll` | MIT | https://github.com/AssetRipper/AssetRipper.Primitives | `LICENSES/MIT-AssetRipper.Primitives.txt` |
| AssetRipper.CIL | 1.2.2 | `AssetRipper.CIL.dll` | MIT | https://github.com/AssetRipper/AssetRipper.CIL | `LICENSES/MIT-AssetRipper.CIL.txt` |
| .NET 6 runtime (BepInEx `dotnet/`) | 6.0.7 | `dotnet/*` | MIT, plus the runtime's own third-party notices | https://github.com/dotnet/runtime | `LICENSES/MIT-dotnet-runtime.txt` |
| Microsoft.Extensions.* (BepInEx `dotnet/`) | as shipped | `Microsoft.Extensions.*.dll` | MIT | https://github.com/dotnet/runtime (formerly dotnet/extensions) | `LICENSES/MIT-dotnet-extensions.txt` |
| .NET 8 runtime + Windows Forms (inside `OTH-LAN-Setup.exe`) | 8.0.x | single-file bundle | MIT, plus the runtime's own third-party notices | https://github.com/dotnet/runtime, https://github.com/dotnet/winforms | `LICENSES/MIT-dotnet-runtime.txt`, `LICENSES/MIT-dotnet-winforms.txt` |

The .NET runtimes include further third-party code under their own notices:
https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT

## Listed for completeness, not shipped

- **Capstone engine** (BSD-3-Clause, https://github.com/capstone-engine/capstone, `LICENSES/BSD-3-Clause-Capstone.txt`).
  `Gee.External.Capstone.dll` binds to it, but BepInEx be.788 ships no `capstone.dll`.
- **Unity base libraries** (`UnityEngine*.dll`, Unity Technologies, proprietary). They are not shipped. BepInEx
  downloads them itself on the first game start from its own mirror (`https://unity.bepinex.dev/libraries/<UnityVersion>.zip`).
- **Over The Hill**, Photon Fusion and all game files (Funselektor Labs / Exit Games, proprietary). They are not shipped.
  Interop assemblies are generated locally from your own copy of the game.

## LGPL compliance (BepInEx, UnityDoorstop, Il2CppInterop)

- These libraries are shipped unmodified, exactly as in the official BepInEx release
  `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip`
  (https://builds.bepinex.dev/projects/bepinex_be/788/, sha256
  `f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a`).
- Their complete source code is available from the repositories linked above, at the commit in the version string
  (BepInEx `5b766a3`, Il2CppInterop `dbda1cb`).
- OTH-LAN only links against them dynamically. You can replace any of these DLLs in `BepInEx/core/` with your own
  build.
