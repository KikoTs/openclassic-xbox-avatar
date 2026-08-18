# Architecture

The add-on is divided into four independent components.

## Capture bridge

`AvatarBridge.dll` is loaded into the Xbox Original Avatars process by
`AvatarBridgeInjector.exe`. It uses the app's installed WinRT metadata and live
editor state to export the selected assembled model, material parameters,
textures, bone data, proportions, component masks, and face layers into the
app's writable state directory.

Generated C++/WinRT projection headers are intentionally excluded from source
control. `build.ps1` regenerates them from the installed app with the Windows
SDK's `cppwinrt.exe`.

## Importer

`OpenClassicAvatarImporter.cs` launches or locates the editor, starts the
bridge, validates the export, converts it to the v3 `.ocavatar` format, and
atomically replaces the previous imported avatar.

## Runtime

`OpenClassicAvatarMod.cs` loads the avatar asset and provides:

- skinned third-person rendering;
- layered base, palette, and decal materials;
- animated face masks and expressions;
- first-person hand and sleeve projection;
- held-item attachment based on the live avatar skeleton;
- world and torch lighting;
- capability negotiation, reliable chunk transfer, validation, and caching.

## Local integration manager

The manager uses dnlib to add five calls to the user's own installed
`CastleMinerZ.exe`:

1. network message registration during startup;
2. external avatar creation from the player constructor;
3. avatar packet consumption before stock message handling;
4. join notification;
5. per-update transfer and attachment processing.

No renderer, model, texture, or avatar data is embedded into the executable.
The manager verifies every hook after writing and stores the original file in
`OpenClassic Addons/Xbox Avatar/Backups` for removal.
