# Third-party dependencies

This repository does not redistribute the dependencies below.

- **dnlib** — used by the local integration manager and restored through
  NuGet under dnlib's MIT license.
- **C++/WinRT** — supplied by the installed Windows SDK and used to generate
  projections on the developer's machine.
- **Microsoft XNA Framework Redistributable 4.0** — required by Castle Miner Z
  and referenced locally while compiling the renderer.
- **Xbox Original Avatars** — Microsoft's application supplies the avatar
  metadata and runtime used by the local capture bridge.
- **Castle Miner Z and OpenClassic** — the user supplies their own installed
  files for compilation and local patching.

No code, binaries, metadata, or assets from the last four dependencies are
committed to this repository or included in packages produced by
`package.ps1`.
