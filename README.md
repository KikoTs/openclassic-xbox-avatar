# OpenClassic Xbox Avatar

Xbox Original Avatar importing, rendering, first-person hands, and multiplayer
replication for the OpenClassic edition of CastleMiner Z.

The importer captures the avatar currently displayed by Microsoft's Xbox
Original Avatars app and converts it into a self-contained `.ocavatar` file.
OpenClassic renders the full rigged model, layered materials, facial details,
expressions, body proportions, clothing, sleeves, and gloves. Modded peers
exchange avatars automatically while unmodded peers retain the stock proxy
character.

## Repository boundary

This repository contains only original integration code:

- the external avatar renderer and multiplayer runtime;
- the one-click avatar importer;
- the native capture bridge and its injector;
- the local enable/disable manager;
- protocol and registration smoke tests.

It does **not** contain Castle Miner Z executables or libraries, decompiled
game source, Xbox app binaries or metadata, generated Microsoft API headers,
personal avatar files, textures, sounds, or other game assets.

## User workflow

Release packages are copied into the Castle Miner Z folder. The user then:

1. double-clicks `OpenClassic Xbox Avatar Manager.exe` once;
2. opens Xbox Original Avatars and selects an avatar;
3. double-clicks `Import Xbox Avatar.exe` and confirms the capture.

The manager patches five integration calls into the user's own installed
executable and keeps an exact recovery backup. No modified game executable is
distributed.

## Building

Requirements:

- Windows 10 or Windows 11 x64;
- PowerShell 7 or Windows PowerShell 5.1;
- .NET SDK 8 or newer;
- Visual Studio 2022 or newer with Desktop development with C++;
- a Windows 10/11 SDK containing `cppwinrt.exe`;
- Microsoft XNA Framework Redistributable 4.0;
- Xbox Original Avatars installed from Microsoft Store;
- a user-owned OpenClassic Castle Miner Z installation.

Run:

```powershell
./build.ps1 -GameDirectory "C:\Program Files (x86)\Steam\steamapps\common\CastleMiner Z"
```

Build products are written to `artifacts/bin`. To build and audit the complete
community package:

```powershell
./package.ps1 -GameDirectory "C:\Program Files (x86)\Steam\steamapps\common\CastleMiner Z"
```

The build reads Castle Miner Z references and Xbox metadata from the local
machine. Those dependencies are never copied into the repository or package.

See [architecture](docs/ARCHITECTURE.md), [installation](docs/INSTALL.md),
[avatar format](docs/FORMAT.md), and [network protocol](docs/PROTOCOL.md).

## Project status

The v3 avatar format supports the assembled 71-bone model, height and build
proportions, independent material/decal passes, facial layers and expressions,
outfit sleeves, bare hands, fingerless gloves, full gloves, first-person hand
projection, proportion-aware third-person held-item attachment, world
lighting, caching, and vanilla-safe capability-gated multiplayer transfer.

## License

The code in this repository is available under the MIT License. Castle Miner
Z, OpenClassic, Microsoft XNA, Xbox Original Avatars, and their assets remain
the property of their respective owners and are not included.
