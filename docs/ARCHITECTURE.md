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
- first-person hands: the arm follows the game's own ProxyBoy first-person
  animation bone for bone, each bone frame converted from the Xbox rig's
  handedness to XNA's; below each wrist, ProxyBoy's joint rotations are
  applied to the avatar's own bone offsets, exactly as third person retargets
  every animated bone, so the fingers land where the game's clip puts them
  (the pickaxe fist, the trigger finger) with the glove whole. A tunable grip
  blends that towards the open hand;
- proportion-aware held-item attachment targeting the imported avatar's live
  finger-and-thumb grip center instead of its low invisible prop bone;
- world and torch lighting;
- stock-message capability negotiation, strictly gated reliable chunk
  transfer, validation, and caching.

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

## Offline renderers

Two tools in `tools/` draw an avatar without the game, through the runtime's
own loader, so rendering faults can be reproduced and fixes judged from a PNG
instead of a screenshot.

`AvatarRenderProbe` rasterises the asset in its bind pose, or any selection
of it, and can also draw the OBJ files the runtime dumps while drawing the
first-person hand.

`FirstPersonProbe` goes further. First person skins every vertex linearly
against one matrix per bone, so the two dumps the runtime writes
(`first-person-mesh.obj` in world space and `first-person-view.obj` through
the player's camera) are enough to recover the exact live bone matrices and
the exact camera by least squares. The tool then re-skins the asset from those
- reproducing the game's output to a few micrometres - and can re-pose the hand
(`--hand straight|curl|runtime`), call the runtime's own posing code, colour
by batch, bone or edge stretch, and render the textured hand at any zoom. The
knuckle tear and the mirrored hand behind it were found, measured and fixed
against it without launching the game; the final posing reproduces the game's
own fingertip positions to a millimetre. `FirstPersonHandSmoke` in the build
guards the fix.
