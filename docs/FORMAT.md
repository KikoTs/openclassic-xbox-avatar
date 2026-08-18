# `.ocavatar` v3 format

The avatar file is a binary, self-contained representation of the selected
editor avatar. It is designed for deterministic loading and direct network
transfer rather than general-purpose model interchange.

The file contains:

- a format header and version;
- body proportions and skin palette;
- the assembled 71-bone skeleton, hierarchy, bind pose, and inverse bind pose;
- skinned vertices, indices, UV channels, normals, and bone weights;
- component/category masks for body, outfit, sleeve, and hand classification;
- ordered material passes with base textures, RGB palette layers, decal UVs,
  tint values, and transparency semantics;
- both editor head halves;
- eyebrow, eye, mouth, facial-hair, skin-feature, and optional eye-shadow
  layers for neutral and animated expression frames.

The importer writes to a temporary file first. A failed conversion never
replaces the last working avatar, and the prior successful import is retained
as `avatar.previous.ocavatar`.

Readers accept older v1/v2 files for compatibility, but v3 is required for
exact combined-outfit, glove, sleeve, face-layer, and material-pass behavior.
