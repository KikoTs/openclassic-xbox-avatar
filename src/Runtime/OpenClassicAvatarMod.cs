using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using DNA.Avatars;
using DNA.CastleMinerZ;
using DNA.CastleMinerZ.Net;
using DNA.CastleMinerZ.Terrain;
using DNA.Drawing;
using DNA.Drawing.Animation;
using DNA.Net;
using DNA.Net.GamerServices;
using DNA.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

// 1.9.9 moved the stock proxy model entity into the game assembly and
// renamed it. The type is otherwise identical - same SkinnedModelEntity
// base, same lighting fields, same .ctor(Model) - so an alias is enough.
// build.ps1 defines this symbol when the target client is 1.9.9 or later.
#if CMZ_MODERN_MODEL_ENTITY
using StockModelEntity = DNA.CastleMinerZ.PlayerModelEntity;
#else
using StockModelEntity = DNA.Avatars.AvatarModelEntity;
#endif

namespace OpenClassic.XboxAvatar
{
    public static class AvatarEntityFactory
    {
        public static StockModelEntity Create(Model fallbackModel, Avatar avatar, NetworkGamer gamer)
        {
            AvatarNetworkBridge.NotePlayer(gamer, avatar, fallbackModel);
            string assetPath = AvatarNetworkBridge.GetAssetPath(gamer);
            if (!string.IsNullOrEmpty(assetPath) && File.Exists(assetPath))
            {
                try
                {
                    return new ImportedAvatarModelEntity(fallbackModel, avatar, assetPath);
                }
                catch (Exception exception)
                {
                    ImportedAvatarModelEntity.WriteFailure(exception);
                }
            }
            return new StockModelEntity(fallbackModel);
        }
    }

    internal sealed class ImportedAvatarModelEntity : StockModelEntity
    {
        private readonly Avatar _avatar;
        private readonly AvatarAsset _asset;
        private readonly Matrix[] _runtimeLocalBones;
        private readonly Matrix[] _exportPoseBones;
        private readonly Matrix[] _avatarSkinTransforms;
        private readonly int[] _proxyBoneByAvatar;
        private readonly ProxyHandCarrier _firstPersonCarrier;
        private BasicEffect _effect;
        private bool _failed;
        private bool _firstPersonFailed;
        private bool _firstPersonLogged;

        public ImportedAvatarModelEntity(Model fallbackModel, Avatar avatar, string assetPath)
            : base(fallbackModel)
        {
            _avatar = avatar;
            _asset = AvatarAsset.Load(assetPath);
            _runtimeLocalBones = new Matrix[_asset.InverseBindPose.Length];
            _exportPoseBones = new Matrix[_asset.InverseBindPose.Length];
            _avatarSkinTransforms = new Matrix[_asset.InverseBindPose.Length];
            _proxyBoneByAvatar = new int[_asset.InverseBindPose.Length];
            for (int bone = 0; bone < _proxyBoneByAvatar.Length; bone++)
            {
                _proxyBoneByAvatar[bone] = FindProxyBone(bone);
                if (_proxyBoneByAvatar[bone] < 0)
                {
                    throw new InvalidDataException(
                        "ProxyBoy is missing Xbox avatar bone " + bone + ".");
                }
            }
            // The selected stock avatar can be SWATMale, whose combined mesh
            // has deliberately bulky first-person glove geometry.  Its 71-bone
            // skeleton matches ProxyBoy, so always use ProxyBoy's dedicated,
            // continuous hand topology as the Xbox surface carrier while the
            // live StockModelEntity continues to supply the current pose.
            Model handCarrierModel = fallbackModel;
            try
            {
                handCarrierModel = CastleMinerZGame.Instance.Content.Load<Model>(
                    "Character\\ProxyBoy");
            }
            catch
            {
                // Offline diagnostics do not create CastleMinerZGame.Instance;
                // their explicitly supplied ProxyBoy model remains valid.
            }
            _firstPersonCarrier = ProxyHandCarrier.Create(
                handCarrierModel,
                _asset,
                _proxyBoneByAvatar);
        }

        public override void Draw(GraphicsDevice device, GameTime gameTime, Matrix view, Matrix projection)
        {
            RefreshWorldLighting();
            if (_avatar.HideHead)
            {
                if (_firstPersonFailed)
                {
                    base.Draw(device, gameTime, view, projection);
                    return;
                }
                try
                {
                    DrawMappedFirstPerson(device, view, projection);
                }
                catch (Exception exception)
                {
                    _firstPersonFailed = true;
                    WriteFailure(exception);
                    base.Draw(device, gameTime, view, projection);
                }
                return;
            }

            if (_failed)
            {
                base.Draw(device, gameTime, view, projection);
                return;
            }

            try
            {
                EnsureGraphicsResources(device);
                BuildExportSpacePose();
                for (int bone = 0; bone < _avatarSkinTransforms.Length; bone++)
                {
                    _avatarSkinTransforms[bone] =
                        _asset.InverseBindPose[bone] *
                        _exportPoseBones[bone];
                }
                foreach (AvatarBatch batch in _asset.Batches)
                {
                    SkinBatch(batch, false);
                }
                if (_avatar.HideHead)
                {
                    PlaceFirstPersonHands();
                }
                if (_avatar.HideHead && !_firstPersonLogged)
                {
                    WriteFirstPersonStatus(view, projection);
                    _firstPersonLogged = true;
                }

                BlendState oldBlend = device.BlendState;
                DepthStencilState oldDepth = device.DepthStencilState;
                RasterizerState oldRasterizer = device.RasterizerState;
                SamplerState oldSampler = device.SamplerStates[0];
                try
                {
                    device.BlendState = BlendState.NonPremultiplied;
                    device.DepthStencilState = DepthStencilState.Default;
                    device.RasterizerState = RasterizerState.CullNone;
                    device.SamplerStates[0] = SamplerState.LinearWrap;

                    bool firstPerson = _avatar.HideHead;

                    // The Windows avatar runtime exports a left-handed mesh.
                    // Bone animation is converted into that space above, then
                    // the root reflection places the finished character in
                    // OpenClassic's world-facing convention.
                    _effect.World = RenderWorld;
                    _effect.View = view;
                    _effect.Projection = projection;
                    _effect.VertexColorEnabled = true;
                    // Lighting must stay enabled in first person too. Flat
                    // unlit skin made the curved palm and curled fingers read
                    // as disconnected 2-D blocks even when the mesh was
                    // correctly skinned around the handle.
                    _effect.LightingEnabled = true;
                    _effect.AmbientLightColor = BasicEffectAmbient(firstPerson);
                    _effect.DirectionalLight0.Enabled = true;
                    _effect.DirectionalLight0.Direction = DirectLightDirection[0];
                    _effect.DirectionalLight0.DiffuseColor = DirectLightColor[0];
                    _effect.DirectionalLight0.SpecularColor = Vector3.Zero;
                    _effect.DirectionalLight1.Enabled = true;
                    _effect.DirectionalLight1.Direction = DirectLightDirection[1];
                    _effect.DirectionalLight1.DiffuseColor = DirectLightColor[1];
                    _effect.DirectionalLight1.SpecularColor = Vector3.Zero;
                    _effect.DirectionalLight2.Enabled = false;

                    foreach (AvatarBatch batch in _asset.Batches)
                    {
                        if (!MatchesCurrentFaceExpression(batch))
                        {
                            continue;
                        }
                        if (firstPerson && !IsFirstPersonHandBatch(batch))
                        {
                            continue;
                        }
                        short[] indices = firstPerson
                            ? batch.FirstPersonIndices
                            : batch.ThirdPersonIndices;
                        if (indices.Length < 3)
                        {
                            continue;
                        }
#if AVATAR_BONE_COLORS
                        _effect.DiffuseColor = Vector3.One;
                        _effect.TextureEnabled = false;
#else
                        AvatarBatch material =
                            batch.IsBareHandShell &&
                            _asset.BaseBodyBatch != null
                                ? _asset.BaseBodyBatch
                                : batch;
                        // 03c8 carries grayscale shader-mask colors, not the
                        // visible skin color.  Suppress those vertex masks and
                        // reuse the solid skin material exported with body 02.
                        _effect.VertexColorEnabled =
                            !batch.IsBareHandShell;
                        _effect.DiffuseColor = material.DiffuseColor;
                        _effect.TextureEnabled = material.Texture != null;
                        _effect.Texture = material.Texture;
#endif

                        foreach (EffectPass pass in _effect.CurrentTechnique.Passes)
                        {
                            pass.Apply();
                            device.DrawUserIndexedPrimitives(
                                PrimitiveType.TriangleList,
                                batch.DrawVertices,
                                0,
                                batch.DrawVertices.Length,
                                indices,
                                0,
                                indices.Length / 3);
                        }
                    }
                }
                finally
                {
                    device.BlendState = oldBlend;
                    device.DepthStencilState = oldDepth;
                    device.RasterizerState = oldRasterizer;
                    device.SamplerStates[0] = oldSampler;
                }
            }
            catch (Exception exception)
            {
                _failed = true;
                WriteFailure(exception);
                base.Draw(device, gameTime, view, projection);
            }
        }

        private Matrix RenderWorld
        {
            get
            {
                // The previous Y rotation also flipped X, which swapped the
                // animated left/right wrists.  A Z reflection is the actual
                // Windows-avatar-to-XNA coordinate conversion: it preserves X
                // so the right grip follows OpenClassic's right-hand prop.
                Matrix world = Matrix.CreateScale(1f, 1f, -1f) *
                    _avatar.LocalToWorld;

                if (_avatar.HideHead)
                {
                    // CastleMiner Z attaches every first-person item to the
                    // stock ProxyBoy PropRight bone.  Retarget the imported
                    // avatar to that exact live anchor so its grip follows the
                    // weapon instead of relying on a hand-tuned camera offset.
                    int proxyProp = FindProxyBone((int)AvatarBone.PropRight);
                    if (proxyProp >= 0)
                    {
                        Vector3 importedProp = Vector3.Transform(
                            _exportPoseBones[(int)AvatarBone.PropRight].Translation,
                            world);
                        Vector3 stockProp = WorldBoneTransforms[proxyProp].Translation;
                        world *= Matrix.CreateTranslation(stockProp - importedProp);
                    }
                }

                return world;
            }
        }

        internal bool TryGetThirdPersonPropTranslation(out Vector3 translation)
        {
            translation = Vector3.Zero;
            if (_avatar == null || _avatar.HideHead)
            {
                return false;
            }
            try
            {
                // PropRight is an invisible Xbox attachment bone well below
                // the rendered palm. Castle Miner Z's proxy hand was authored
                // around that location, but a real Xbox avatar exposes its
                // articulated digits there instead. Attach to the live center
                // of the four finger bases plus the thumb so the final item is
                // inside the visible grip for every height and hand shape.
                BuildExportSpacePose();
                Vector3 importedProp = _exportPoseBones[
                    (int)AvatarBone.PropRight].Translation;
                importedProp.Z = -importedProp.Z;
                Vector3 index = ExportBoneTranslation(
                    AvatarBone.FingerIndexRight);
                Vector3 middle = ExportBoneTranslation(
                    AvatarBone.FingerMiddleRight);
                Vector3 ring = ExportBoneTranslation(
                    AvatarBone.FingerRingRight);
                Vector3 small = ExportBoneTranslation(
                    AvatarBone.FingerSmallRight);
                Vector3 thumb = ExportBoneTranslation(
                    AvatarBone.FingerThumbRight);
                translation = ComputeThirdPersonGripTranslation(
                    importedProp,
                    index,
                    middle,
                    ring,
                    small,
                    thumb);
                return IsFinite(translation);
            }
            catch (Exception exception)
            {
                WriteFailure(exception);
                return false;
            }
        }

        private Vector3 ExportBoneTranslation(AvatarBone bone)
        {
            Vector3 result = _exportPoseBones[(int)bone].Translation;
            result.Z = -result.Z;
            return result;
        }

        internal static Vector3 ComputeThirdPersonGripTranslation(
            Vector3 importedProp,
            Vector3 fingerIndex,
            Vector3 fingerMiddle,
            Vector3 fingerRing,
            Vector3 fingerSmall,
            Vector3 fingerThumb)
        {
            if (!IsFinite(importedProp) || !IsFinite(fingerIndex) ||
                !IsFinite(fingerMiddle) || !IsFinite(fingerRing) ||
                !IsFinite(fingerSmall) || !IsFinite(fingerThumb))
            {
                return importedProp;
            }

            Vector3 visibleGrip =
                (fingerIndex + fingerMiddle + fingerRing +
                 fingerSmall + fingerThumb) / 5f;
            Vector3 correction = visibleGrip - importedProp;

            // Bound malformed/custom skeletons without restricting the real
            // Xbox avatar range: PropRight commonly sits about 15 cm below the
            // visible grip on tall models.
            const float maximumCorrection = 0.22f;
            float lengthSquared = correction.LengthSquared();
            if (lengthSquared > maximumCorrection * maximumCorrection)
            {
                correction *= maximumCorrection /
                    (float)Math.Sqrt(lengthSquared);
            }
            return importedProp + correction;
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.X) &&
                !float.IsNaN(value.Y) &&
                !float.IsNaN(value.Z) &&
                !float.IsInfinity(value.X) &&
                !float.IsInfinity(value.Y) &&
                !float.IsInfinity(value.Z);
        }

        private int FindProxyBone(int avatarBone)
        {
            for (int index = 0; index < Skeleton.Count; index++)
            {
                int mapped;
                if (Avatar.boneNameLookup.TryGetValue(
                    Skeleton[index].Name,
                    out mapped) &&
                    mapped == avatarBone)
                {
                    return index;
                }
            }
            return -1;
        }

        private void RefreshWorldLighting()
        {
            // Player.UpdateMovement refreshes ProxyModelEntity lighting only
            // while the unavailable Xbox AvatarRenderer is non-null. On the
            // Windows/classic path that guard is false, leaving imported
            // avatars on their constructor light forever. Sample the exact
            // same terrain channels here so sunlight and placed torches affect
            // local and network players alike.
            try
            {
                Player player = _avatar.Tag as Player;
                BlockTerrain terrain = BlockTerrain.Instance;
                if (player == null || terrain == null)
                {
                    return;
                }

                Vector3 samplePosition = player.WorldPosition;
                samplePosition.Y += 1.2f;
                terrain.GetEnemyLighting(
                    samplePosition,
                    ref DirectLightDirection[0],
                    ref DirectLightColor[0],
                    ref DirectLightDirection[1],
                    ref DirectLightColor[1],
                    ref AmbientLight);
            }
            catch
            {
                // Offline diagnostics render before CastleMinerZGame and
                // BlockTerrain exist. Preserve their explicitly supplied test
                // lighting and retain the normal proxy fallback in that case.
            }
        }

        private Vector3 BasicEffectAmbient(bool firstPerson)
        {
            // DNA's avatar shader combines AmbientLight with a separate metal
            // term. BasicEffect has no equivalent, so give it a larger share
            // of the sampled terrain light while still allowing true darkness.
            float scale = firstPerson ? 0.85f : 0.75f;
            return new Vector3(
                MathHelper.Clamp(AmbientLight.X * scale, 0f, 1f),
                MathHelper.Clamp(AmbientLight.Y * scale, 0f, 1f),
                MathHelper.Clamp(AmbientLight.Z * scale, 0f, 1f));
        }

        private void DrawMappedFirstPerson(
            GraphicsDevice device,
            Matrix view,
            Matrix projection)
        {
            EnsureGraphicsResources(device);

            // Skin the exported Xbox geometry directly onto the live ProxyBoy
            // matrices.  Those matrices already contain CastleMiner Z's exact
            // pickaxe, compass, firearm and knife poses, including every
            // finger bone.  Pinning every Xbox bone to its named ProxyBoy bone
            // removes all hand-authored offsets, curls and palm scaling.
            for (int bone = 0; bone < _avatarSkinTransforms.Length; bone++)
            {
                Matrix target = WorldBoneTransforms[_proxyBoneByAvatar[bone]];
                Matrix shape = Matrix.CreateScale(
                    _asset.FirstPersonBoneScale[bone]);
                _avatarSkinTransforms[bone] =
                    _asset.InverseBindPose[bone] *
                    shape *
                    target;
            }
            foreach (AvatarBatch batch in _asset.Batches)
            {
                if (batch.MappedFirstPersonIndices.Length >= 3)
                {
                    SkinBatch(batch, true);
                }
            }
            _firstPersonCarrier.Skin(
                WorldBoneTransforms,
                _asset.FirstPersonBoneScale);

            BlendState oldBlend = device.BlendState;
            DepthStencilState oldDepth = device.DepthStencilState;
            RasterizerState oldRasterizer = device.RasterizerState;
            SamplerState oldSampler = device.SamplerStates[0];
            try
            {
                device.BlendState = BlendState.NonPremultiplied;
                device.DepthStencilState = DepthStencilState.Default;
                device.RasterizerState = RasterizerState.CullNone;
                device.SamplerStates[0] = SamplerState.LinearWrap;

                // DrawVertices are already in the same world space as the
                // stock item because WorldBoneTransforms includes LocalToWorld.
                _effect.World = Matrix.Identity;
                _effect.View = view;
                _effect.Projection = projection;
                _effect.VertexColorEnabled = true;
                _effect.LightingEnabled = true;
                _effect.AmbientLightColor = BasicEffectAmbient(true);
                _effect.DirectionalLight0.Enabled = true;
                _effect.DirectionalLight0.Direction = DirectLightDirection[0];
                _effect.DirectionalLight0.DiffuseColor = DirectLightColor[0];
                _effect.DirectionalLight0.SpecularColor = Vector3.Zero;
                _effect.DirectionalLight1.Enabled = true;
                _effect.DirectionalLight1.Direction = DirectLightDirection[1];
                _effect.DirectionalLight1.DiffuseColor = DirectLightColor[1];
                _effect.DirectionalLight1.SpecularColor = Vector3.Zero;
                _effect.DirectionalLight2.Enabled = false;

                int renderedBatches = 0;
                int renderedTriangles = 0;
                foreach (AvatarBatch batch in _asset.Batches)
                {
                    short[] indices = batch.MappedFirstPersonIndices;
                    if (indices.Length < 3)
                    {
                        continue;
                    }

#if AVATAR_BONE_COLORS
                    _effect.DiffuseColor = Vector3.One;
                    _effect.TextureEnabled = false;
#else
                    AvatarBatch material =
                        batch.IsBareHandShell &&
                        _asset.BaseBodyBatch != null
                            ? _asset.BaseBodyBatch
                            : batch;
                    // Both naked-hand layers use the base body's exported skin
                    // tint. Disabling their incompatible vertex masks makes
                    // the palm/finger seam one continuous skin tone.
                    _effect.VertexColorEnabled =
                        !batch.IsBareHandShell &&
                        !batch.IsBaseBody;
                    _effect.DiffuseColor = material.DiffuseColor;
                    _effect.TextureEnabled = material.Texture != null;
                    _effect.Texture = material.Texture;
#endif

                    foreach (EffectPass pass in _effect.CurrentTechnique.Passes)
                    {
                        pass.Apply();
                        device.DrawUserIndexedPrimitives(
                            PrimitiveType.TriangleList,
                            batch.DrawVertices,
                            0,
                            batch.DrawVertices.Length,
                            indices,
                            0,
                            indices.Length / 3);
                    }
                    renderedBatches++;
                    renderedTriangles += indices.Length / 3;
                }

                foreach (ProxyHandCarrierPart carrierPart in
                    _firstPersonCarrier.Parts)
                {
                    AvatarBatch carrierMaterial = carrierPart.Material;
                    _effect.VertexColorEnabled = carrierPart.UseVertexColor;
                    _effect.DiffuseColor = carrierMaterial.DiffuseColor;
                    _effect.TextureEnabled =
                        carrierPart.UseTexture &&
                        carrierMaterial.Texture != null;
                    _effect.Texture = carrierMaterial.Texture;
                    foreach (EffectPass pass in _effect.CurrentTechnique.Passes)
                    {
                        pass.Apply();
                        device.DrawUserIndexedPrimitives(
                            PrimitiveType.TriangleList,
                            carrierPart.DrawVertices,
                            0,
                            carrierPart.DrawVertices.Length,
                            carrierPart.Indices,
                            0,
                            carrierPart.Indices.Length / 3);
                    }
                    renderedBatches++;
                    renderedTriangles += carrierPart.Indices.Length / 3;
                }

                if (!_firstPersonLogged)
                {
                    WriteMappedFirstPersonStatus(
                        renderedBatches,
                        renderedTriangles,
                        view,
                        projection);
                    _firstPersonLogged = true;
                }
            }
            finally
            {
                device.BlendState = oldBlend;
                device.DepthStencilState = oldDepth;
                device.RasterizerState = oldRasterizer;
                device.SamplerStates[0] = oldSampler;
            }
        }

        private void WriteMappedFirstPersonStatus(
            int renderedBatches,
            int renderedTriangles,
            Matrix view,
            Matrix projection)
        {
            try
            {
                Matrix viewProjection = view * projection;
                Vector3 worldMinimum = new Vector3(float.MaxValue);
                Vector3 worldMaximum = new Vector3(float.MinValue);
                Vector3 viewMinimum = new Vector3(float.MaxValue);
                Vector3 viewMaximum = new Vector3(float.MinValue);
                Vector3 ndcMinimum = new Vector3(float.MaxValue);
                Vector3 ndcMaximum = new Vector3(float.MinValue);
                int verticesInFront = 0;
                int verticesInFrustum = 0;
                foreach (AvatarBatch batch in _asset.Batches)
                {
                    foreach (short rawIndex in batch.MappedFirstPersonIndices)
                    {
                        Vector3 world = batch.DrawVertices[(ushort)rawIndex].Position;
                        Vector3 viewPosition = Vector3.Transform(world, view);
                        Vector4 clip = Vector4.Transform(
                            new Vector4(world, 1f),
                            viewProjection);
                        worldMinimum = Vector3.Min(worldMinimum, world);
                        worldMaximum = Vector3.Max(worldMaximum, world);
                        viewMinimum = Vector3.Min(viewMinimum, viewPosition);
                        viewMaximum = Vector3.Max(viewMaximum, viewPosition);
                        if (clip.W <= 0f)
                        {
                            continue;
                        }
                        verticesInFront++;
                        Vector3 ndc = new Vector3(
                            clip.X / clip.W,
                            clip.Y / clip.W,
                            clip.Z / clip.W);
                        ndcMinimum = Vector3.Min(ndcMinimum, ndc);
                        ndcMaximum = Vector3.Max(ndcMaximum, ndc);
                        if (Math.Abs(ndc.X) <= 1f &&
                            Math.Abs(ndc.Y) <= 1f &&
                            ndc.Z >= 0f && ndc.Z <= 1f)
                        {
                            verticesInFrustum++;
                        }
                    }
                }
                string folder = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "OpenClassic Addons",
                    "Xbox Avatar");
                Directory.CreateDirectory(folder);
                string carrierStatus = string.Empty;
                for (int partIndex = 0;
                    partIndex < _firstPersonCarrier.Parts.Length;
                    partIndex++)
                {
                    ProxyHandCarrierPart part =
                        _firstPersonCarrier.Parts[partIndex];
                    Vector2 uvMinimum = new Vector2(float.MaxValue);
                    Vector2 uvMaximum = new Vector2(float.MinValue);
                    Vector4 averageColor = Vector4.Zero;
                    foreach (AvatarDrawVertex vertex in part.DrawVertices)
                    {
                        uvMinimum = Vector2.Min(
                            uvMinimum,
                            vertex.TextureCoordinate);
                        uvMaximum = Vector2.Max(
                            uvMaximum,
                            vertex.TextureCoordinate);
                        averageColor += vertex.Color.ToVector4();
                    }
                    if (part.DrawVertices.Length > 0)
                    {
                        averageColor /= part.DrawVertices.Length;
                    }
                    carrierStatus +=
                        "carrierPart" + partIndex + "=" +
                        part.Material.Name +
                        " triangles=" + (part.Indices.Length / 3) +
                        " uv=" + uvMinimum + ".." + uvMaximum +
                        " color=" + averageColor +
                        Environment.NewLine;
                }
                File.WriteAllText(
                    Path.Combine(folder, "renderer-status.log"),
                    "mode=proxy-bone-retarget" + Environment.NewLine +
                    "renderedBatches=" + renderedBatches + Environment.NewLine +
                    "renderedTriangles=" + renderedTriangles + Environment.NewLine +
                    "placement=live ProxyBoy matrices" + Environment.NewLine +
                    "manualOffsets=false" + Environment.NewLine +
                    "manualFingerCurl=false" + Environment.NewLine +
                    "world=" + worldMinimum + ".." + worldMaximum + Environment.NewLine +
                    "view=" + viewMinimum + ".." + viewMaximum + Environment.NewLine +
                    "ndc=" + ndcMinimum + ".." + ndcMaximum + Environment.NewLine +
                    "verticesInFront=" + verticesInFront + Environment.NewLine +
                    "verticesInFrustum=" + verticesInFrustum + Environment.NewLine +
                    carrierStatus +
                    "proxyWristLeftView=" + Vector3.Transform(
                        WorldBoneTransforms[_proxyBoneByAvatar[(int)AvatarBone.WristLeft]].Translation,
                        view) + Environment.NewLine +
                    "proxyWristRightView=" + Vector3.Transform(
                        WorldBoneTransforms[_proxyBoneByAvatar[(int)AvatarBone.WristRight]].Translation,
                        view) + Environment.NewLine);
            }
            catch
            {
            }
        }

        private void WriteFirstPersonStatus(Matrix view, Matrix projection)
        {
            try
            {
                Matrix worldViewProjection = RenderWorld * view * projection;
                int selectedIndices = 0;
                int verticesInFront = 0;
                int verticesInFrustum = 0;
                float minX = float.MaxValue;
                float maxX = float.MinValue;
                float minY = float.MaxValue;
                float maxY = float.MinValue;
                float minZ = float.MaxValue;
                float maxZ = float.MinValue;

                foreach (AvatarBatch batch in _asset.Batches)
                {
                    selectedIndices += batch.FirstPersonIndices.Length;
                    foreach (short rawIndex in batch.FirstPersonIndices)
                    {
                        int index = (ushort)rawIndex;
                        Vector4 clip = Vector4.Transform(
                            new Vector4(batch.DrawVertices[index].Position, 1f),
                            worldViewProjection);
                        if (clip.W <= 0f)
                        {
                            continue;
                        }
                        verticesInFront++;
                        float x = clip.X / clip.W;
                        float y = clip.Y / clip.W;
                        float z = clip.Z / clip.W;
                        minX = Math.Min(minX, x);
                        maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y);
                        maxY = Math.Max(maxY, y);
                        minZ = Math.Min(minZ, z);
                        maxZ = Math.Max(maxZ, z);
                        if (Math.Abs(x) <= 1f &&
                            Math.Abs(y) <= 1f &&
                            z >= 0f &&
                            z <= 1f)
                        {
                            verticesInFrustum++;
                        }
                    }
                }

                string folder = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "OpenClassic Addons",
                    "Xbox Avatar");
                File.WriteAllText(
                    Path.Combine(folder, "renderer-status.log"),
                    "hideHead=true" + Environment.NewLine +
                    "selectedIndices=" + selectedIndices + Environment.NewLine +
                    "verticesInFront=" + verticesInFront + Environment.NewLine +
                    "verticesInFrustum=" + verticesInFrustum + Environment.NewLine +
                    "ndcX=" + minX + ".." + maxX + Environment.NewLine +
                    "ndcY=" + minY + ".." + maxY + Environment.NewLine +
                    "ndcZ=" + minZ + ".." + maxZ + Environment.NewLine +
                    "avatarWorld=" + _avatar.LocalToWorld.Translation + Environment.NewLine +
                    "viewTranslation=" + view.Translation + Environment.NewLine +
                    BoneStatus((int)AvatarBone.WristLeft, view) + Environment.NewLine +
                    BoneStatus((int)AvatarBone.WristRight, view) + Environment.NewLine +
                    BoneStatus((int)AvatarBone.PropRight, view) + Environment.NewLine);
            }
            catch (Exception exception)
            {
                WriteFailure(exception);
            }
        }

        private string BoneStatus(int avatarBone, Matrix view)
        {
            int proxyBone = -1;
            for (int index = 0; index < Skeleton.Count; index++)
            {
                int mapped;
                if (Avatar.boneNameLookup.TryGetValue(
                    Skeleton[index].Name,
                    out mapped) &&
                    mapped == avatarBone)
                {
                    proxyBone = index;
                    break;
                }
            }
            Vector3 exportWorld = Vector3.Transform(
                _exportPoseBones[avatarBone].Translation,
                RenderWorld);
            Vector3 exportView = Vector3.Transform(exportWorld, view);
            Vector3 proxyView = proxyBone < 0
                ? new Vector3(float.NaN)
                : Vector3.Transform(
                    WorldBoneTransforms[proxyBone].Translation,
                    view);
            return "bone" + avatarBone +
                " exportView=" + exportView +
                " proxyView=" + proxyView +
                " delta=" + (proxyView - exportView);
        }

        private void BuildExportSpacePose()
        {
            _avatar.Skeleton.CopyTransformsTo(_runtimeLocalBones);
            if (_avatar.HideHead)
            {
                ApplyFirstPersonGrip();
            }

            // OpenClassic/XNA and Xbox Original Avatars use opposite Z
            // handedness for every local child transform. Conjugating by this
            // reflection changes handedness without turning rotations into
            // mirrored geometry. The root additionally carries XNA's built-in
            // 180-degree facing rotation, which the Windows export omits.
            Matrix reflectionZ = Matrix.CreateScale(1f, 1f, -1f);
            Matrix animatedRoot =
                _runtimeLocalBones[0] *
                Matrix.CreateRotationY(MathHelper.Pi);
            _exportPoseBones[0] = RetargetAnimatedLocal(
                animatedRoot,
                _asset.SourcePoseLocal[0]);
            for (int bone = 1; bone < _exportPoseBones.Length; bone++)
            {
                Matrix animatedLocal =
                    reflectionZ *
                    _runtimeLocalBones[bone] *
                    reflectionZ;

                // CastleMiner Z's clips carry useful rotation/scale but use
                // placeholder child translations.  The old importer restored
                // those translations from Avatar.DefaultBindPose, which only
                // fits one generic body and tears apart avatars with different
                // height, build or hand size.  The .ocavatar already contains
                // the exact bind skeleton used to export its mesh, so preserve
                // that avatar's own local bone offsets here.
                Matrix local = RetargetAnimatedLocal(
                    animatedLocal,
                    _asset.SourcePoseLocal[bone]);
                _exportPoseBones[bone] =
                    local *
                    _exportPoseBones[Avatar.DefaultParentBones[bone]];
            }
        }

        private static Matrix RetargetAnimatedLocal(
            Matrix animated,
            Matrix sourcePose)
        {
            Vector3 ignoredAnimatedScale;
            Vector3 ignoredAnimatedTranslation;
            Quaternion animatedRotation;
            if (!animated.Decompose(
                out ignoredAnimatedScale,
                out animatedRotation,
                out ignoredAnimatedTranslation))
            {
                animatedRotation = Quaternion.Identity;
            }

            Vector3 sourceScale;
            Vector3 ignoredSourceTranslation;
            Quaternion ignoredSourceRotation;
            if (!sourcePose.Decompose(
                out sourceScale,
                out ignoredSourceRotation,
                out ignoredSourceTranslation))
            {
                sourceScale = Vector3.One;
            }

            Matrix result =
                Matrix.CreateScale(sourceScale) *
                Matrix.CreateFromQuaternion(animatedRotation);
            result.Translation = sourcePose.Translation;
            return result;
        }

        private void ApplyFirstPersonGrip()
        {
            // CastleMiner Z's legacy FPS clips stop at the wrist because the
            // ProxyBoy model has no articulated fingers. Xbox hands do. Curl
            // the rendered PropRight hand around the rig's authored local Z
            // hinge. The negative sign is after the engine-to-export Z-space
            // conversion; the opposite sign opens the fingers off the item.
            CurlFingerSet(
                new[] { 44, 45, 46, 47 },
                new[] { 56, 57, 58, 59 },
                new[] { 66, 67, 68, 69 },
                50, 60, 70,
                -1f);
        }

        private void CurlFingerSet(
            int[] bases,
            int[] middles,
            int[] tips,
            int thumbBase,
            int thumbMiddle,
            int thumbTip,
            float direction)
        {
            foreach (int bone in bases)
            {
                CurlRuntimeBone(bone, 42f * direction);
            }
            foreach (int bone in middles)
            {
                CurlRuntimeBone(bone, 55f * direction);
            }
            foreach (int bone in tips)
            {
                CurlRuntimeBone(bone, 35f * direction);
            }
            CurlRuntimeBone(thumbBase, 18f * direction);
            CurlRuntimeBone(thumbMiddle, 28f * direction);
            CurlRuntimeBone(thumbTip, 16f * direction);
        }

        private void CurlRuntimeBone(int bone, float degrees)
        {
            _runtimeLocalBones[bone] =
                Matrix.CreateRotationZ(MathHelper.ToRadians(degrees)) *
                _runtimeLocalBones[bone];
        }

        private void EnsureGraphicsResources(GraphicsDevice device)
        {
            if (_effect == null)
            {
                _effect = new BasicEffect(device);
            }
            foreach (AvatarBatch batch in _asset.Batches)
            {
                if (batch.Texture == null && batch.TexturePng != null && batch.TexturePng.Length > 0)
                {
                    using (var stream = new MemoryStream(batch.TexturePng, false))
                    {
                        batch.Texture = Texture2D.FromStream(device, stream);
                    }
                }
            }
        }

        private bool MatchesCurrentFaceExpression(AvatarBatch batch)
        {
            if (batch.FaceTextureUsage < 0)
            {
                return true;
            }
            int frame;
            switch (batch.FaceTextureUsage)
            {
                case 7:
                    frame = (int)_avatar.Expression.LeftEyebrow;
                    break;
                case 8:
                    frame = (int)_avatar.Expression.RightEyebrow;
                    break;
                case 9:
                    frame = (int)_avatar.Expression.LeftEye;
                    break;
                case 10:
                    frame = (int)_avatar.Expression.RightEye;
                    break;
                case 12:
                    frame = (int)_avatar.Expression.Mouth;
                    break;
                default:
                    frame = 0;
                    break;
            }
            return batch.FaceFrame == frame;
        }

        private void SkinBatch(AvatarBatch batch, bool mappedFirstPerson)
        {
            for (int index = 0; index < batch.SourceVertices.Length; index++)
            {
                AvatarSourceVertex source = batch.SourceVertices[index];
                byte[] bindings = mappedFirstPerson &&
                    batch.MappedBindings != null &&
                    batch.MappedBindings[index] != null
                        ? batch.MappedBindings[index]
                        : source.Bindings;
                byte[] weights = mappedFirstPerson &&
                    batch.MappedWeights != null &&
                    batch.MappedWeights[index] != null
                        ? batch.MappedWeights[index]
                        : source.Weights;
                Vector3 position = Vector3.Zero;
                Vector3 normal = Vector3.Zero;
                float totalWeight = 0f;

                for (int influence = 0; influence < 4; influence++)
                {
                    float weight = weights[influence] / 255f;
                    int bone = bindings[influence];
                    if (weight <= 0f || bone < 0 || bone >= _avatarSkinTransforms.Length)
                    {
                        continue;
                    }
                    position += Vector3.Transform(source.Position, _avatarSkinTransforms[bone]) * weight;
                    normal += Vector3.TransformNormal(source.Normal, _avatarSkinTransforms[bone]) * weight;
                    totalWeight += weight;
                }

                if (totalWeight <= 0.0001f)
                {
                    position = source.Position;
                    normal = source.Normal;
                }
                else if (Math.Abs(totalWeight - 1f) > 0.0001f)
                {
                    position /= totalWeight;
                    normal /= totalWeight;
                }

                if (normal.LengthSquared() > 0.000001f)
                {
                    normal.Normalize();
                }

                batch.DrawVertices[index].Position = position;
                batch.DrawVertices[index].Normal = normal;
#if AVATAR_BONE_COLORS
                if (_avatar.HideHead)
                {
                    batch.DrawVertices[index].Color = DebugBoneColor(source);
                }
#endif
            }
        }

        private bool IsFirstPersonHandBatch(AvatarBatch batch)
        {
            if (_asset.HasOuterHandMesh)
            {
                // A complete outfit glove replaces both naked-hand layers.
                return !batch.IsBaseBody &&
                    !batch.IsBareHandShell &&
                    batch.HasFingerGeometry;
            }
            if (_asset.BareHandShell != null)
            {
                // Prefer the stable base-body naked hand. The 03c8 shell's
                // finger weights fan out when retargeted onto held-item poses.
                return _asset.BaseBodyBatch != null
                    ? batch.IsBaseBody
                    : batch.IsBareHandShell;
            }
            return batch.IsBaseBody;
        }

        private void PlaceFirstPersonHands()
        {
            PlaceFirstPersonHand(
                1,
                (int)AvatarBone.WristLeft,
                (int)AvatarBone.PropLeft,
                _asset.FirstPersonScaleLeft);
            PlaceFirstPersonHand(
                2,
                (int)AvatarBone.WristRight,
                (int)AvatarBone.PropRight,
                _asset.FirstPersonScaleRight);
        }

        private void PlaceFirstPersonHand(
            byte side,
            int wristBone,
            int propBone,
            float scale)
        {
            Vector3 palmCenter = Vector3.Zero;
            int palmVertices = 0;

            // PropRight/PropLeft are wrist-level item anchors.  Find the
            // actual palm surface after skinning so short, tall, slim and
            // heavy avatars all put the item through their own palm instead
            // of leaving the hand beside the handle.
            foreach (AvatarBatch batch in _asset.Batches)
            {
                if (!IsFirstPersonHandBatch(batch))
                {
                    continue;
                }
                for (int index = 0; index < batch.SourceVertices.Length; index++)
                {
                    if (!batch.FirstPersonUsed[index] ||
                        batch.FirstPersonSides[index] != side ||
                        AvatarBatch.DirectBoneWeight(
                            batch.SourceVertices[index],
                            wristBone) < 0.35f)
                    {
                        continue;
                    }
                    palmCenter += batch.DrawVertices[index].Position;
                    palmVertices++;
                }
            }

            // Some glove meshes bind their entire palm through helper bones.
            // Their visible hand centroid is still a safe deterministic grip
            // center and avoids avatar-specific hard-coded offsets.
            if (palmVertices == 0)
            {
                foreach (AvatarBatch batch in _asset.Batches)
                {
                    if (!IsFirstPersonHandBatch(batch))
                    {
                        continue;
                    }
                    for (int index = 0; index < batch.SourceVertices.Length; index++)
                    {
                        if (batch.FirstPersonUsed[index] &&
                            batch.FirstPersonSides[index] == side)
                        {
                            palmCenter += batch.DrawVertices[index].Position;
                            palmVertices++;
                        }
                    }
                }
            }
            if (palmVertices == 0)
            {
                return;
            }

            palmCenter /= palmVertices;
            Vector3 prop = _exportPoseBones[propBone].Translation;
            // The item model is offset from the Prop bone.  Preserve the
            // animated wrist-to-palm direction independently from the hand's
            // size normalization, otherwise small/fat hands collapse onto the
            // bone while tall/slim hands overshoot the visible handle.
            Vector3 targetPalmCenter = prop +
                ((palmCenter - prop) * 0.70f);
            foreach (AvatarBatch batch in _asset.Batches)
            {
                if (!IsFirstPersonHandBatch(batch))
                {
                    continue;
                }
                for (int index = 0; index < batch.SourceVertices.Length; index++)
                {
                    if (!batch.FirstPersonUsed[index] ||
                        batch.FirstPersonSides[index] != side)
                    {
                        continue;
                    }
                    AvatarDrawVertex vertex = batch.DrawVertices[index];
                    vertex.Position = targetPalmCenter +
                        ((vertex.Position - palmCenter) * scale);
                    batch.DrawVertices[index] = vertex;
                }
            }
        }

#if AVATAR_BONE_COLORS
        private static Color DebugBoneColor(AvatarSourceVertex source)
        {
            int dominant = 0;
            for (int influence = 1; influence < 4; influence++)
            {
                if (source.Weights[influence] > source.Weights[dominant])
                {
                    dominant = influence;
                }
            }
            int bone = source.Bindings[dominant];
            if (bone == 44 || bone == 56 || bone == 66)
            {
                return Color.Red;
            }
            if (bone == 45 || bone == 57 || bone == 67)
            {
                return Color.Lime;
            }
            if (bone == 46 || bone == 58 || bone == 68)
            {
                return Color.Blue;
            }
            if (bone == 47 || bone == 59 || bone == 69)
            {
                return Color.Yellow;
            }
            if (bone == 50 || bone == 60 || bone == 70)
            {
                return Color.Magenta;
            }
            return Color.White;
        }
#endif

        internal static void WriteFailure(Exception exception)
        {
            try
            {
                string folder = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "OpenClassic Addons",
                    "Xbox Avatar");
                Directory.CreateDirectory(folder);
                File.AppendAllText(
                    Path.Combine(folder, "renderer.log"),
                    DateTime.Now.ToString("s") + " " + exception + Environment.NewLine);
            }
            catch
            {
            }
        }
    }

    internal sealed class ProxyHandCarrier
    {
        private const float CarrierCuffRadius = 0.11f;
        private const float MaximumSurfaceProjection = 0.04f;
        private const float CarrierModelScale = 100f;

        private readonly Matrix[] _inverseBindPose;
        private readonly int[] _avatarBoneByProxy;

        internal ProxyHandCarrierPart[] Parts;

        private ProxyHandCarrier(
            ProxyHandCarrierPart[] parts,
            Matrix[] inverseBindPose,
            int[] avatarBoneByProxy)
        {
            Parts = parts;
            _inverseBindPose = inverseBindPose;
            _avatarBoneByProxy = avatarBoneByProxy;
        }

        internal static ProxyHandCarrier Create(
            Model model,
            AvatarAsset asset,
            int[] proxyBoneByAvatar)
        {
            ModelMesh bodyMesh = null;
            foreach (ModelMesh mesh in model.Meshes)
            {
                if (mesh.Name.IndexOf("_body_", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    bodyMesh = mesh;
                    break;
                }
            }
            if (bodyMesh == null)
            {
                // SWATMale is delivered as one combined skinned mesh rather
                // than ProxyBoy's named body/top/bottom meshes. The carrier
                // filter below selects only wrist/finger triangles, so the
                // combined mesh is a valid topology source as well.
                foreach (ModelMesh mesh in model.Meshes)
                {
                    if (mesh.MeshParts.Count != 1)
                    {
                        continue;
                    }
                    bool hasBlendIndices = false;
                    foreach (VertexElement element in
                        mesh.MeshParts[0].VertexBuffer.VertexDeclaration.GetVertexElements())
                    {
                        if (element.VertexElementUsage ==
                            VertexElementUsage.BlendIndices)
                        {
                            hasBlendIndices = true;
                            break;
                        }
                    }
                    if (hasBlendIndices)
                    {
                        bodyMesh = mesh;
                        break;
                    }
                }
            }
            if (bodyMesh == null || bodyMesh.MeshParts.Count != 1)
            {
                throw new InvalidDataException(
                    "ProxyBoy body mesh is unavailable for first-person hand conversion.");
            }

            ModelMeshPart part = bodyMesh.MeshParts[0];
            int stride = part.VertexBuffer.VertexDeclaration.VertexStride;
            if (stride < 56)
            {
                throw new InvalidDataException(
                    "ProxyBoy body vertex layout is too small for skinning data.");
            }

            byte[] vertexBytes = new byte[
                part.VertexBuffer.VertexCount * stride];
            part.VertexBuffer.GetData(vertexBytes);

            var allVertices = new CarrierSourceVertex[part.NumVertices];
            int[] avatarBoneByProxy = new int[71];
            for (int index = 0; index < avatarBoneByProxy.Length; index++)
            {
                avatarBoneByProxy[index] = -1;
            }
            for (int avatarBone = 0;
                avatarBone < proxyBoneByAvatar.Length;
                avatarBone++)
            {
                int proxyBone = proxyBoneByAvatar[avatarBone];
                if (proxyBone >= 0 && proxyBone < avatarBoneByProxy.Length)
                {
                    avatarBoneByProxy[proxyBone] = avatarBone;
                }
            }

            Vector3 wristLeft = asset.BindPoseAbsolute[
                (int)AvatarBone.WristLeft].Translation;
            Vector3 wristRight = asset.BindPoseAbsolute[
                (int)AvatarBone.WristRight].Translation;
            byte[] sides = new byte[allVertices.Length];
            for (int index = 0; index < allVertices.Length; index++)
            {
                int offset = (part.VertexOffset + index) * stride;
                CarrierSourceVertex vertex = ReadCarrierVertex(
                    vertexBytes,
                    offset);
                allVertices[index] = vertex;

                float leftWeight = CarrierHandWeight(
                    vertex,
                    avatarBoneByProxy,
                    (int)AvatarBone.WristLeft);
                float rightWeight = CarrierHandWeight(
                    vertex,
                    avatarBoneByProxy,
                    (int)AvatarBone.WristRight);
                float leftDistance = Vector3.Distance(
                    vertex.Position,
                    wristLeft);
                float rightDistance = Vector3.Distance(
                    vertex.Position,
                    wristRight);
                float strongest = Math.Max(leftWeight, rightWeight);
                float nearest = Math.Min(leftDistance, rightDistance);
                if (strongest >= 0.05f || nearest <= CarrierCuffRadius)
                {
                    sides[index] = strongest >= 0.05f
                        ? (leftWeight >= rightWeight ? (byte)1 : (byte)2)
                        : (leftDistance <= rightDistance ? (byte)1 : (byte)2);
                }
            }

            int[] sourceIndices = ReadPartIndices(part);
            var selectedIndices = new List<int>();
            for (int triangle = 0;
                triangle < sourceIndices.Length;
                triangle += 3)
            {
                int index0 = sourceIndices[triangle];
                int index1 = sourceIndices[triangle + 1];
                int index2 = sourceIndices[triangle + 2];
                if (index0 < 0 || index0 >= sides.Length ||
                    index1 < 0 || index1 >= sides.Length ||
                    index2 < 0 || index2 >= sides.Length)
                {
                    throw new InvalidDataException(
                        "ProxyBoy body mesh contains an invalid hand index.");
                }
                byte side = sides[index0];
                // Preserve both stock first-person hand branches.  Each
                // carrier vertex retains ProxyBoy's original left/right wrist
                // weights, so the support hand follows PropLeft while the
                // trigger hand follows PropRight instead of being duplicated
                // at the weapon anchor.
                if (side != 0 &&
                    sides[index1] == side &&
                    sides[index2] == side)
                {
                    selectedIndices.Add(index0);
                    selectedIndices.Add(index1);
                    selectedIndices.Add(index2);
                }
            }

            var builders = new Dictionary<AvatarBatch, CarrierPartBuilder>();
            for (int triangle = 0;
                triangle < selectedIndices.Count;
                triangle += 3)
            {
                int index0 = selectedIndices[triangle];
                int index1 = selectedIndices[triangle + 1];
                int index2 = selectedIndices[triangle + 2];
                byte side = sides[index0];
                AvatarBatch surface = ChooseCarrierSurface(
                    allVertices[index0],
                    allVertices[index1],
                    allVertices[index2],
                    side,
                    asset,
                    avatarBoneByProxy);
                AvatarBatch material = asset.IsOuterHandBatch(surface)
                    ? surface
                    : asset.BaseBodyBatch ?? asset.BareHandShell;
                if (surface == null || material == null)
                {
                    throw new InvalidDataException(
                        "Xbox Avatar asset has no hand material.");
                }

                CarrierPartBuilder builder;
                if (!builders.TryGetValue(material, out builder))
                {
                    bool outer = asset.IsOuterHandBatch(material);
                    builder = new CarrierPartBuilder(material, outer);
                    builders.Add(material, builder);
                }
                AddCarrierTriangle(
                    builder,
                    allVertices[index0],
                    allVertices[index1],
                    allVertices[index2],
                    side,
                    surface,
                    avatarBoneByProxy);
            }
            if (builders.Count == 0)
            {
                throw new InvalidDataException(
                    "ProxyBoy first-person hand carrier has no triangles.");
            }

            var parts = new List<ProxyHandCarrierPart>(builders.Count);
            foreach (CarrierPartBuilder builder in builders.Values)
            {
                parts.Add(builder.Build());
            }
            var skinning = (SkinedAnimationData)model.Tag;
            return new ProxyHandCarrier(
                parts.ToArray(),
                skinning.InverseBindPose,
                avatarBoneByProxy);
        }

        internal void Skin(
            Matrix[] worldBoneTransforms,
            Vector3[] avatarShapeScales)
        {
            var transforms = new Matrix[_inverseBindPose.Length];
            for (int proxyBone = 0;
                proxyBone < transforms.Length;
                proxyBone++)
            {
                int avatarBone = proxyBone < _avatarBoneByProxy.Length
                    ? _avatarBoneByProxy[proxyBone]
                    : -1;
                Vector3 shape = avatarBone >= 0 &&
                    avatarBone < avatarShapeScales.Length
                        ? avatarShapeScales[avatarBone]
                        : Vector3.One;
                transforms[proxyBone] =
                    _inverseBindPose[proxyBone] *
                    Matrix.CreateScale(shape) *
                    worldBoneTransforms[proxyBone];
            }

            foreach (ProxyHandCarrierPart part in Parts)
            {
                for (int index = 0; index < part.SourceVertices.Length; index++)
                {
                    CarrierSourceVertex source = part.SourceVertices[index];
                    Vector3 position = Vector3.Zero;
                    Vector3 normal = Vector3.Zero;
                    float total = 0f;
                    for (int influence = 0; influence < 4; influence++)
                    {
                        float weight = source.Weights[influence];
                        int bone = source.Bindings[influence];
                        if (weight > 0f &&
                            bone >= 0 &&
                            bone < transforms.Length)
                        {
                            position += Vector3.Transform(
                                source.Position,
                                transforms[bone]) * weight;
                            normal += Vector3.TransformNormal(
                                source.Normal,
                                transforms[bone]) * weight;
                            total += weight;
                        }
                    }
                    if (total > 0.0001f && Math.Abs(total - 1f) > 0.0001f)
                    {
                        position /= total;
                        normal /= total;
                    }
                    if (normal.LengthSquared() > 0.000001f)
                    {
                        normal.Normalize();
                    }
                    AvatarDrawVertex draw = part.DrawVertices[index];
                    draw.Position = position;
                    draw.Normal = normal;
                    part.DrawVertices[index] = draw;
                }
            }
        }

        private static CarrierSourceVertex ReadCarrierVertex(
            byte[] data,
            int offset)
        {
            var result = new CarrierSourceVertex();
            result.Position = new Vector3(
                BitConverter.ToSingle(data, offset),
                BitConverter.ToSingle(data, offset + 4),
                BitConverter.ToSingle(data, offset + 8));
            result.Bindings = new byte[]
            {
                data[offset + 12],
                data[offset + 13],
                data[offset + 14],
                data[offset + 15]
            };
            result.Weights = new float[]
            {
                BitConverter.ToSingle(data, offset + 16),
                BitConverter.ToSingle(data, offset + 20),
                BitConverter.ToSingle(data, offset + 24),
                BitConverter.ToSingle(data, offset + 28)
            };
            result.Normal = new Vector3(
                BitConverter.ToSingle(data, offset + 32),
                BitConverter.ToSingle(data, offset + 36),
                BitConverter.ToSingle(data, offset + 40));
            result.TextureCoordinate = new Vector2(
                BitConverter.ToSingle(data, offset + 44),
                BitConverter.ToSingle(data, offset + 48));
            result.Color = new Color(
                data[offset + 52],
                data[offset + 53],
                data[offset + 54],
                data[offset + 55]);
            return result;
        }

        private static int[] ReadPartIndices(ModelMeshPart part)
        {
            int count = part.PrimitiveCount * 3;
            var result = new int[count];
            if (part.IndexBuffer.IndexElementSize == IndexElementSize.SixteenBits)
            {
                var indices = new ushort[part.IndexBuffer.IndexCount];
                part.IndexBuffer.GetData(indices);
                for (int index = 0; index < count; index++)
                {
                    result[index] = indices[part.StartIndex + index];
                }
            }
            else
            {
                var indices = new int[part.IndexBuffer.IndexCount];
                part.IndexBuffer.GetData(indices);
                Array.Copy(indices, part.StartIndex, result, 0, count);
            }
            return result;
        }

        private static AvatarBatch ChooseCarrierSurface(
            CarrierSourceVertex vertex0,
            CarrierSourceVertex vertex1,
            CarrierSourceVertex vertex2,
            byte side,
            AvatarAsset asset,
            int[] avatarBoneByProxy)
        {
            AvatarBatch best = null;
            float bestScore = float.MaxValue;
            foreach (AvatarBatch candidate in asset.OuterHandBatches)
            {
                float score = CarrierTriangleProjectionScore(
                    candidate,
                    vertex0,
                    vertex1,
                    vertex2,
                    side,
                    avatarBoneByProxy);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            if (best != null)
            {
                return best;
            }
            return asset.BaseBodyBatch ?? asset.BareHandShell;
        }

        private static float CarrierTriangleProjectionScore(
            AvatarBatch batch,
            CarrierSourceVertex vertex0,
            CarrierSourceVertex vertex1,
            CarrierSourceVertex vertex2,
            byte side,
            int[] avatarBoneByProxy)
        {
            CarrierSourceVertex[] vertices =
                { vertex0, vertex1, vertex2 };
            float score = 0f;
            for (int index = 0; index < vertices.Length; index++)
            {
                int region = CarrierFingerRegion(
                    vertices[index],
                    side,
                    avatarBoneByProxy);
                SurfacePoint point;
                if (!FindCarrierSurfacePoint(
                    batch,
                    ToXboxExportPosition(vertices[index].Position),
                    side,
                    region,
                    out point) ||
                    point.Distance > MaximumSurfaceProjection)
                {
                    return float.MaxValue;
                }
                score += point.Distance;
            }
            return score;
        }

        private static void AddCarrierTriangle(
            CarrierPartBuilder builder,
            CarrierSourceVertex vertex0,
            CarrierSourceVertex vertex1,
            CarrierSourceVertex vertex2,
            byte side,
            AvatarBatch surface,
            int[] avatarBoneByProxy)
        {
            CarrierSourceVertex[] vertices =
                { vertex0, vertex1, vertex2 };
            for (int index = 0; index < vertices.Length; index++)
            {
                CarrierSourceVertex vertex = vertices[index];
                MorphCarrierVertex(
                    ref vertex,
                    side,
                    surface,
                    avatarBoneByProxy);
                builder.Add(vertex);
            }
        }

        private static void MorphCarrierVertex(
            ref CarrierSourceVertex vertex,
            byte side,
            AvatarBatch surface,
            int[] avatarBoneByProxy)
        {
            int region = CarrierFingerRegion(
                vertex,
                side,
                avatarBoneByProxy);
            SurfacePoint point;
            bool found = FindCarrierSurfacePoint(
                surface,
                ToXboxExportPosition(vertex.Position),
                side,
                region,
                out point);
            if (!found || point.Distance > MaximumSurfaceProjection)
            {
                return;
            }

            vertex.Position = ToProxyPosition(point.Position);
            vertex.Normal = ToProxyDirection(point.Normal);
            vertex.TextureCoordinate = point.TextureCoordinate;
            vertex.Color = point.Color;
        }

        private static Vector3 ToXboxExportPosition(Vector3 value)
        {
            return new Vector3(
                value.X * CarrierModelScale,
                value.Y * CarrierModelScale,
                -value.Z * CarrierModelScale);
        }

        private static Vector3 ToProxyPosition(Vector3 value)
        {
            return new Vector3(
                value.X / CarrierModelScale,
                value.Y / CarrierModelScale,
                -value.Z / CarrierModelScale);
        }

        private static Vector3 ToProxyDirection(Vector3 value)
        {
            return new Vector3(value.X, value.Y, -value.Z);
        }

        private static bool FindCarrierSurfacePoint(
            AvatarBatch batch,
            Vector3 position,
            byte side,
            int desiredRegion,
            out SurfacePoint closest)
        {
            if (FindClosestSurfacePoint(
                batch,
                position,
                side,
                desiredRegion,
                out closest))
            {
                return true;
            }
            // Some combined outfits bind their glove overlay to wrist/arm
            // bones even though the surface itself covers a finger. Spatial
            // proximity remains unambiguous within one hand, so fall back to
            // the nearest triangle on the same side when semantic weights do
            // not describe the visible garment layer.
            return FindClosestSurfacePoint(
                batch,
                position,
                side,
                int.MinValue,
                out closest);
        }

        private sealed class CarrierPartBuilder
        {
            private readonly List<CarrierSourceVertex> _sourceVertices =
                new List<CarrierSourceVertex>();
            private readonly List<AvatarDrawVertex> _drawVertices =
                new List<AvatarDrawVertex>();
            private readonly List<short> _indices = new List<short>();
            private readonly AvatarBatch _material;
            private readonly bool _outer;

            internal CarrierPartBuilder(AvatarBatch material, bool outer)
            {
                _material = material;
                _outer = outer;
            }

            internal void Add(CarrierSourceVertex vertex)
            {
                if (_sourceVertices.Count >= ushort.MaxValue)
                {
                    throw new InvalidDataException(
                        "ProxyBoy hand material group is too large.");
                }
                _indices.Add(unchecked((short)_sourceVertices.Count));
                _sourceVertices.Add(vertex);
                _drawVertices.Add(new AvatarDrawVertex(
                    vertex.Position,
                    vertex.Normal,
                    vertex.TextureCoordinate,
                    vertex.Color));
            }

            internal ProxyHandCarrierPart Build()
            {
                return new ProxyHandCarrierPart(
                    _sourceVertices.ToArray(),
                    _drawVertices.ToArray(),
                    _indices.ToArray(),
                    _material,
                    _outer && _material.TexturePng != null &&
                        _material.TexturePng.Length > 0,
                    _outer);
            }
        }

        private static bool FindClosestSurfacePoint(
            AvatarBatch batch,
            Vector3 position,
            byte side,
            int desiredRegion,
            out SurfacePoint closest)
        {
            closest = new SurfacePoint();
            closest.Distance = float.MaxValue;
            bool found = false;
            for (int triangle = 0;
                triangle < batch.Indices.Length;
                triangle += 3)
            {
                int index0 = (ushort)batch.Indices[triangle];
                int index1 = (ushort)batch.Indices[triangle + 1];
                int index2 = (ushort)batch.Indices[triangle + 2];
                if (batch.FirstPersonSides[index0] != side ||
                    batch.FirstPersonSides[index1] != side ||
                    batch.FirstPersonSides[index2] != side)
                {
                    continue;
                }
                int region0 = SurfaceFingerRegion(
                    batch.SourceVertices[index0],
                    side);
                int region1 = SurfaceFingerRegion(
                    batch.SourceVertices[index1],
                    side);
                int region2 = SurfaceFingerRegion(
                    batch.SourceVertices[index2],
                    side);
                bool compatible = desiredRegion == int.MinValue ||
                    (desiredRegion >= 0
                    ? region0 == desiredRegion ||
                      region1 == desiredRegion ||
                      region2 == desiredRegion
                    : region0 < 0 || region1 < 0 || region2 < 0);
                if (!compatible)
                {
                    continue;
                }

                Vector3 barycentric;
                Vector3 surface = ClosestPointOnTriangle(
                    position,
                    batch.SourceVertices[index0].Position,
                    batch.SourceVertices[index1].Position,
                    batch.SourceVertices[index2].Position,
                    out barycentric);
                float distance = Vector3.Distance(position, surface);
                if (distance >= closest.Distance)
                {
                    continue;
                }

                AvatarDrawVertex draw0 = batch.DrawVertices[index0];
                AvatarDrawVertex draw1 = batch.DrawVertices[index1];
                AvatarDrawVertex draw2 = batch.DrawVertices[index2];
                Vector3 normal =
                    batch.SourceVertices[index0].Normal * barycentric.X +
                    batch.SourceVertices[index1].Normal * barycentric.Y +
                    batch.SourceVertices[index2].Normal * barycentric.Z;
                if (normal.LengthSquared() > 0.000001f)
                {
                    normal.Normalize();
                }
                Vector4 color =
                    draw0.Color.ToVector4() * barycentric.X +
                    draw1.Color.ToVector4() * barycentric.Y +
                    draw2.Color.ToVector4() * barycentric.Z;
                closest.Position = surface;
                closest.Normal = normal;
                closest.TextureCoordinate =
                    draw0.TextureCoordinate * barycentric.X +
                    draw1.TextureCoordinate * barycentric.Y +
                    draw2.TextureCoordinate * barycentric.Z;
                closest.Color = new Color(color);
                closest.Distance = distance;
                found = true;
            }
            return found;
        }

        private static Vector3 ClosestPointOnTriangle(
            Vector3 point,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            out Vector3 barycentric)
        {
            Vector3 ab = b - a;
            Vector3 ac = c - a;
            Vector3 ap = point - a;
            float d1 = Vector3.Dot(ab, ap);
            float d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f)
            {
                barycentric = new Vector3(1f, 0f, 0f);
                return a;
            }

            Vector3 bp = point - b;
            float d3 = Vector3.Dot(ab, bp);
            float d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3)
            {
                barycentric = new Vector3(0f, 1f, 0f);
                return b;
            }

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float value = d1 / (d1 - d3);
                barycentric = new Vector3(1f - value, value, 0f);
                return a + ab * value;
            }

            Vector3 cp = point - c;
            float d5 = Vector3.Dot(ab, cp);
            float d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6)
            {
                barycentric = new Vector3(0f, 0f, 1f);
                return c;
            }

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float value = d2 / (d2 - d6);
                barycentric = new Vector3(1f - value, 0f, value);
                return a + ac * value;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f &&
                d4 - d3 >= 0f &&
                d5 - d6 >= 0f)
            {
                float value = (d4 - d3) /
                    ((d4 - d3) + (d5 - d6));
                barycentric = new Vector3(0f, 1f - value, value);
                return b + (c - b) * value;
            }

            float denominator = 1f / (va + vb + vc);
            float v = vb * denominator;
            float w = vc * denominator;
            barycentric = new Vector3(1f - v - w, v, w);
            return a + ab * v + ac * w;
        }

        private static float CarrierHandWeight(
            CarrierSourceVertex vertex,
            int[] avatarBoneByProxy,
            int wristBone)
        {
            float result = 0f;
            for (int influence = 0; influence < 4; influence++)
            {
                int proxyBone = vertex.Bindings[influence];
                int avatarBone = proxyBone >= 0 &&
                    proxyBone < avatarBoneByProxy.Length
                        ? avatarBoneByProxy[proxyBone]
                        : -1;
                if (IsDescendantOf(avatarBone, wristBone))
                {
                    result += vertex.Weights[influence];
                }
            }
            return result;
        }

        private static int CarrierFingerRegion(
            CarrierSourceVertex vertex,
            byte side,
            int[] avatarBoneByProxy)
        {
            var totals = new float[5];
            for (int influence = 0; influence < 4; influence++)
            {
                int proxyBone = vertex.Bindings[influence];
                int avatarBone = proxyBone >= 0 &&
                    proxyBone < avatarBoneByProxy.Length
                        ? avatarBoneByProxy[proxyBone]
                        : -1;
                int region = FingerRegionForBone(avatarBone, side);
                if (region >= 0)
                {
                    totals[region] += vertex.Weights[influence];
                }
            }
            int best = -1;
            float bestWeight = 0f;
            for (int region = 0; region < totals.Length; region++)
            {
                if (totals[region] > bestWeight)
                {
                    bestWeight = totals[region];
                    best = region;
                }
            }
            return bestWeight >= 0.15f ? best : -1;
        }

        private static int SurfaceFingerRegion(
            AvatarSourceVertex vertex,
            byte side)
        {
            var totals = new int[5];
            for (int influence = 0; influence < 4; influence++)
            {
                int region = FingerRegionForBone(
                    vertex.Bindings[influence],
                    side);
                if (region >= 0)
                {
                    totals[region] += vertex.Weights[influence];
                }
            }
            int best = -1;
            int bestWeight = 0;
            for (int region = 0; region < totals.Length; region++)
            {
                if (totals[region] > bestWeight)
                {
                    bestWeight = totals[region];
                    best = region;
                }
            }
            return bestWeight >= 38 ? best : -1;
        }

        private static int FingerRegionForBone(int bone, byte side)
        {
            int firstBase = side == 1
                ? (int)AvatarBone.FingerIndexLeft
                : (int)AvatarBone.FingerIndexRight;
            int secondBase = side == 1
                ? (int)AvatarBone.FingerIndex2Left
                : (int)AvatarBone.FingerIndex2Right;
            int thirdBase = side == 1
                ? (int)AvatarBone.FingerIndex3Left
                : (int)AvatarBone.FingerIndex3Right;
            for (int region = 0; region < 4; region++)
            {
                if (bone == firstBase + region ||
                    bone == secondBase + region ||
                    bone == thirdBase + region)
                {
                    return region;
                }
            }
            int thumbBase = side == 1
                ? (int)AvatarBone.FingerThumbLeft
                : (int)AvatarBone.FingerThumbRight;
            int thumbSecond = side == 1
                ? (int)AvatarBone.FingerThumb2Left
                : (int)AvatarBone.FingerThumb2Right;
            int thumbThird = side == 1
                ? (int)AvatarBone.FingerThumb3Left
                : (int)AvatarBone.FingerThumb3Right;
            return bone == thumbBase ||
                bone == thumbSecond ||
                bone == thumbThird
                    ? 4
                    : -1;
        }

        private static bool IsDescendantOf(int bone, int ancestor)
        {
            while (bone >= 0 && bone < Avatar.DefaultParentBones.Count)
            {
                if (bone == ancestor)
                {
                    return true;
                }
                bone = Avatar.DefaultParentBones[bone];
            }
            return false;
        }

        internal struct CarrierSourceVertex
        {
            internal Vector3 Position;
            internal Vector3 Normal;
            internal Vector2 TextureCoordinate;
            internal Color Color;
            internal byte[] Bindings;
            internal float[] Weights;
        }

        private struct SurfacePoint
        {
            internal Vector3 Position;
            internal Vector3 Normal;
            internal Vector2 TextureCoordinate;
            internal Color Color;
            internal float Distance;
        }
    }

    internal sealed class ProxyHandCarrierPart
    {
        internal ProxyHandCarrier.CarrierSourceVertex[] SourceVertices;
        internal AvatarDrawVertex[] DrawVertices;
        internal short[] Indices;
        internal AvatarBatch Material;
        internal bool UseTexture;
        internal bool UseVertexColor;

        internal ProxyHandCarrierPart(
            ProxyHandCarrier.CarrierSourceVertex[] sourceVertices,
            AvatarDrawVertex[] drawVertices,
            short[] indices,
            AvatarBatch material,
            bool useTexture,
            bool useVertexColor)
        {
            SourceVertices = sourceVertices;
            DrawVertices = drawVertices;
            Indices = indices;
            Material = material;
            UseTexture = useTexture;
            UseVertexColor = useVertexColor;
        }
    }

    internal sealed class AvatarAsset
    {
        // First-person items have a fixed stock grip size, while Xbox avatars
        // can have very different hand proportions.  Normalize the exported
        // mesh around its own prop anchor instead of applying one hard-coded
        // scale to every avatar.
        private const float TargetFirstPersonHandRadius = 0.08f;
        internal Matrix[] InverseBindPose;
        internal Matrix[] BindPoseAbsolute;
        internal Matrix[] BindPoseLocal;
        internal Matrix[] SourcePoseLocal;
        internal Vector3[] FirstPersonBoneScale;
        internal AvatarBatch[] Batches;
        internal AvatarBatch BaseBodyBatch;
        internal AvatarBatch BareHandShell;
        internal AvatarBatch OuterHandBatch;
        internal readonly List<AvatarBatch> OuterHandBatches =
            new List<AvatarBatch>();
        internal bool HasOuterHandMesh;
        internal float FirstPersonScaleLeft;
        internal float FirstPersonScaleRight;

        internal static AvatarAsset Load(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadUInt32() != 0x5641434f)
                {
                    throw new InvalidDataException("Xbox Avatar asset has an invalid signature.");
                }
                int version = reader.ReadInt32();
                if (version != 1 && version != 2 && version != 3)
                {
                    throw new InvalidDataException("Unsupported Xbox Avatar asset version " + version + ".");
                }
                int boneCount = reader.ReadInt32();
                if (boneCount != 71)
                {
                    throw new InvalidDataException("Xbox Avatar asset must contain 71 bones.");
                }

                var asset = new AvatarAsset();
                asset.InverseBindPose = new Matrix[boneCount];
                asset.BindPoseAbsolute = new Matrix[boneCount];
                asset.BindPoseLocal = new Matrix[boneCount];
                for (int index = 0; index < boneCount; index++)
                {
                    asset.InverseBindPose[index] = ReadMatrix(reader);
                    asset.BindPoseAbsolute[index] =
                        Matrix.Invert(asset.InverseBindPose[index]);
                }
                asset.BindPoseLocal[0] = asset.BindPoseAbsolute[0];
                for (int bone = 1; bone < boneCount; bone++)
                {
                    asset.BindPoseLocal[bone] =
                        asset.BindPoseAbsolute[bone] *
                        Matrix.Invert(asset.BindPoseAbsolute[
                            Avatar.DefaultParentBones[bone]]);
                }
                asset.SourcePoseLocal = new Matrix[boneCount];
                if (version >= 2)
                {
                    for (int bone = 0; bone < boneCount; bone++)
                    {
                        asset.SourcePoseLocal[bone] = ReadMatrix(reader);
                    }
                }
                else
                {
                    Array.Copy(
                        asset.BindPoseLocal,
                        asset.SourcePoseLocal,
                        boneCount);
                }
                asset.FirstPersonBoneScale = BuildCumulativeShapeScales(
                    asset.SourcePoseLocal);

                int batchCount = reader.ReadInt32();
                if (batchCount <= 0 || batchCount > 128)
                {
                    throw new InvalidDataException("Xbox Avatar asset has an invalid batch count.");
                }
                asset.Batches = new AvatarBatch[batchCount];
                for (int index = 0; index < batchCount; index++)
                {
                    asset.Batches[index] = AvatarBatch.Read(reader, version);
                    if (asset.Batches[index].IsBaseBody)
                    {
                        asset.BaseBodyBatch = asset.Batches[index];
                    }
                    asset.Batches[index].BuildFirstPersonGeometry(
                        asset.BindPoseAbsolute[(int)AvatarBone.WristLeft].Translation,
                        asset.BindPoseAbsolute[(int)AvatarBone.WristRight].Translation);
                }
                foreach (AvatarBatch batch in asset.Batches)
                {
                    if (batch.IsHandComponent && batch.HasFingerGeometry)
                    {
                        // Version 3 carries the hand component's material
                        // palette. A palette matching the base body is a naked
                        // hand shell; a differing palette is an equipped glove.
                        // Legacy assets did not retain that distinction and keep
                        // the conservative naked-hand behavior.
                        bool combinedClothing =
                            (batch.CategoryMask & 0x00000008u) != 0 ||
                            (batch.CategoryMask & 0x00800000u) != 0;
                        bool naked = !combinedClothing &&
                            (version < 3 ||
                             batch.MaterialMatchesSkin(asset.BaseBodyBatch));
                        batch.IsBareHandShell = naked;
                        if (naked)
                        {
                            asset.BareHandShell = batch;
                        }
                        else
                        {
                            asset.AddOuterHandBatch(batch);
                        }
                    }
                    else if (!batch.IsBaseBody && batch.HasFingerGeometry)
                    {
                        asset.AddOuterHandBatch(batch);
                    }

                    // First person uses the continuous ProxyBoy hand topology as
                    // a carrier, morphed to the exported Xbox surface. Suppress
                    // every exported hand layer here so duplicate palm/finger
                    // shells cannot overlap or separate during animation.
                    if (batch.IsHandComponent ||
                        (!batch.IsBaseBody && batch.HasFingerGeometry))
                    {
                        // Combined Xbox styles can put shirt sleeves and gloves
                        // into one model (for example category 00000ab8). Keep
                        // its lower-arm/sleeve triangles, but leave palm and
                        // fingers to the continuous ProxyBoy hand carrier.
                        bool containsSleeves =
                            (batch.CategoryMask & 0x00000008u) != 0 ||
                            (batch.CategoryMask & 0x00800000u) != 0;
                        if (containsSleeves)
                        {
                            batch.BuildMappedFirstPersonGeometry(
                                true,
                                false);
                            continue;
                        }
                        batch.MappedFirstPersonIndices = new short[0];
                        continue;
                    }
                    batch.BuildMappedFirstPersonGeometry(
                        batch.IsBaseBody,
                        false);
                }
                if (asset.HasOuterHandMesh && asset.BaseBodyBatch != null)
                {
                    var outerHands = new List<AvatarBatch>();
                    foreach (AvatarBatch batch in asset.Batches)
                    {
                        if (!batch.IsBaseBody &&
                            !batch.IsBareHandShell &&
                            batch.HasFingerGeometry)
                        {
                            outerHands.Add(batch);
                        }
                    }
                    asset.BaseBodyBatch.RemoveCoveredThirdPersonHandGeometry(
                        outerHands);
                }
                asset.FirstPersonScaleLeft = ComputeFirstPersonScale(
                    asset,
                    (int)AvatarBone.WristLeft,
                    (int)AvatarBone.PropLeft);
                asset.FirstPersonScaleRight = ComputeFirstPersonScale(
                    asset,
                    (int)AvatarBone.WristRight,
                    (int)AvatarBone.PropRight);
                return asset;
            }
        }

        private void AddOuterHandBatch(AvatarBatch batch)
        {
            HasOuterHandMesh = true;
            if (!OuterHandBatches.Contains(batch))
            {
                OuterHandBatches.Add(batch);
            }
            if (OuterHandBatch == null)
            {
                OuterHandBatch = batch;
            }
        }

        internal bool IsOuterHandBatch(AvatarBatch batch)
        {
            return batch != null && OuterHandBatches.Contains(batch);
        }

        private static Vector3[] BuildCumulativeShapeScales(
            Matrix[] sourcePoseLocal)
        {
            var result = new Vector3[sourcePoseLocal.Length];
            for (int bone = 0; bone < sourcePoseLocal.Length; bone++)
            {
                Vector3 localScale;
                Quaternion ignoredRotation;
                Vector3 ignoredTranslation;
                if (!sourcePoseLocal[bone].Decompose(
                    out localScale,
                    out ignoredRotation,
                    out ignoredTranslation))
                {
                    localScale = Vector3.One;
                }
                localScale = new Vector3(
                    SafeShapeScale(localScale.X),
                    SafeShapeScale(localScale.Y),
                    SafeShapeScale(localScale.Z));
                if (bone == 0)
                {
                    result[bone] = localScale;
                }
                else
                {
                    Vector3 parent = result[Avatar.DefaultParentBones[bone]];
                    result[bone] = new Vector3(
                        parent.X * localScale.X,
                        parent.Y * localScale.Y,
                        parent.Z * localScale.Z);
                }
            }
            return result;
        }

        private static float SafeShapeScale(float value)
        {
            value = Math.Abs(value);
            return value >= 0.25f && value <= 4f ? value : 1f;
        }

        private static float ComputeFirstPersonScale(
            AvatarAsset asset,
            int wristBone,
            int propBone)
        {
            var distances = new List<float>();
            Vector3 prop = asset.BindPoseAbsolute[propBone].Translation;
            int otherWrist = wristBone == (int)AvatarBone.WristLeft
                ? (int)AvatarBone.WristRight
                : (int)AvatarBone.WristLeft;

            foreach (AvatarBatch batch in asset.Batches)
            {
                bool rendered = asset.HasOuterHandMesh
                    ? !batch.IsBaseBody &&
                      !batch.IsBareHandShell &&
                      batch.HasFingerGeometry
                    : asset.BaseBodyBatch != null
                        ? batch.IsBaseBody
                        : batch.IsBareHandShell;
                if (!rendered)
                {
                    continue;
                }

                var seen = new HashSet<int>();
                foreach (short rawIndex in batch.FirstPersonIndices)
                {
                    int index = (ushort)rawIndex;
                    if (!seen.Add(index))
                    {
                        continue;
                    }
                    byte expectedSide = wristBone == (int)AvatarBone.WristLeft
                        ? (byte)1
                        : (byte)2;
                    if (batch.FirstPersonSides[index] == expectedSide)
                    {
                        distances.Add(Vector3.Distance(
                            batch.SourceVertices[index].Position,
                            prop));
                    }
                }
            }

            if (distances.Count == 0)
            {
                return 0.70f;
            }
            distances.Sort();
            int percentile = (int)Math.Round((distances.Count - 1) * 0.95f);
            float radius = distances[Math.Max(0, Math.Min(
                distances.Count - 1,
                percentile))];
            if (radius <= 0.0001f)
            {
                return 0.70f;
            }
            return MathHelper.Clamp(
                TargetFirstPersonHandRadius / radius,
                0.35f,
                1.10f);
        }

        private static Matrix ReadMatrix(BinaryReader reader)
        {
            return new Matrix(
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
    }

    internal sealed class AvatarBatch
    {
        internal string Name;
        internal AvatarSourceVertex[] SourceVertices;
        internal AvatarDrawVertex[] DrawVertices;
        internal short[] Indices;
        internal short[] ThirdPersonIndices;
        internal short[] FirstPersonIndices;
        internal short[] MappedFirstPersonIndices;
        internal byte[] FirstPersonSides;
        internal bool[] FirstPersonUsed;
        internal byte[][] MappedBindings;
        internal byte[][] MappedWeights;
        internal Vector3 DiffuseColor;
        internal byte[] TexturePng;
        internal Texture2D Texture;
        internal uint CategoryMask;
        internal int ShaderId;
        internal byte PaletteMask;
        internal Vector4[] Palette;
        internal bool IsBaseBody;
        internal bool IsHandComponent;
        internal bool IsBareHandShell;
        internal bool HasFingerGeometry;
        internal int FaceTextureUsage = -1;
        internal int FaceFrame = -1;

        internal static AvatarBatch Read(BinaryReader reader, int version)
        {
            var batch = new AvatarBatch();
            batch.Name = reader.ReadString();
            batch.ParseFaceLayerName();
            batch.Palette = new Vector4[3];
            if (version >= 3)
            {
                batch.CategoryMask = reader.ReadUInt32();
                batch.ShaderId = reader.ReadInt32();
                batch.PaletteMask = reader.ReadByte();
                for (int paletteIndex = 0; paletteIndex < 3; paletteIndex++)
                {
                    batch.Palette[paletteIndex] = new Vector4(
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle());
                }
            }
            batch.IsBaseBody =
                batch.Name.StartsWith("00000002-", StringComparison.OrdinalIgnoreCase) &&
                batch.Name.IndexOf(":body:", StringComparison.OrdinalIgnoreCase) >= 0;
            batch.IsHandComponent = version >= 3
                ? (batch.CategoryMask & 0x00000080u) != 0
                : batch.Name.StartsWith(
                    "00000880-",
                    StringComparison.OrdinalIgnoreCase);
            int vertexCount = reader.ReadInt32();
            if (vertexCount <= 0 || vertexCount > 100000)
            {
                throw new InvalidDataException("Invalid vertex count in " + batch.Name + ".");
            }
            batch.SourceVertices = new AvatarSourceVertex[vertexCount];
            batch.DrawVertices = new AvatarDrawVertex[vertexCount];
            for (int index = 0; index < vertexCount; index++)
            {
                var source = new AvatarSourceVertex();
                source.Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                source.Normal = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                Vector2 uv = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                source.Bindings = reader.ReadBytes(4);
                source.Weights = reader.ReadBytes(4);
                Color color = new Color(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                batch.SourceVertices[index] = source;
                batch.DrawVertices[index] = new AvatarDrawVertex(source.Position, source.Normal, uv, color);
            }

            int indexCount = reader.ReadInt32();
            if (indexCount <= 0 || indexCount % 3 != 0 || indexCount > 1000000)
            {
                throw new InvalidDataException("Invalid index count in " + batch.Name + ".");
            }
            batch.Indices = new short[indexCount];
            for (int index = 0; index < indexCount; index++)
            {
                batch.Indices[index] = unchecked((short)reader.ReadUInt16());
            }
            batch.ThirdPersonIndices = batch.Indices;
            batch.HasFingerGeometry = HasFingerTriangles(
                batch.SourceVertices,
                batch.Indices);
            batch.FirstPersonIndices = new short[0];
            batch.MappedFirstPersonIndices = new short[0];
            batch.FirstPersonSides = new byte[vertexCount];
            batch.FirstPersonUsed = new bool[vertexCount];
            batch.DiffuseColor = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            int textureLength = reader.ReadInt32();
            if (textureLength < 0 || textureLength > 32 * 1024 * 1024)
            {
                throw new InvalidDataException("Invalid texture size in " + batch.Name + ".");
            }
            batch.TexturePng = reader.ReadBytes(textureLength);
            return batch;
        }

        private void ParseFaceLayerName()
        {
            const string marker = ":face-layer-";
            const string frameMarker = "-frame-";
            int usageStart = Name.IndexOf(
                marker,
                StringComparison.OrdinalIgnoreCase);
            if (usageStart < 0)
            {
                return;
            }
            usageStart += marker.Length;
            int frameStart = Name.IndexOf(
                frameMarker,
                usageStart,
                StringComparison.OrdinalIgnoreCase);
            if (frameStart < 0 ||
                !int.TryParse(
                    Name.Substring(usageStart, frameStart - usageStart),
                    out FaceTextureUsage))
            {
                FaceTextureUsage = -1;
                return;
            }
            frameStart += frameMarker.Length;
            int frameEnd = Name.IndexOf(':', frameStart);
            string frameText = frameEnd < 0
                ? Name.Substring(frameStart)
                : Name.Substring(frameStart, frameEnd - frameStart);
            if (!int.TryParse(frameText, out FaceFrame))
            {
                FaceTextureUsage = -1;
                FaceFrame = -1;
            }
        }

        internal void RemoveCoveredThirdPersonHandGeometry(
            IList<AvatarBatch> outerHands)
        {
            if (!IsBaseBody || outerHands == null || outerHands.Count == 0)
            {
                return;
            }

            // Xbox outfits can replace only part of the naked hand
            // (fingerless gloves) or all of it (racing/tactical gloves).
            // Remove base-body triangles only where an equipped outer surface
            // is actually present. This avoids both z-fighting/skin-colored
            // gloves and the missing fingertips caused by deleting the whole
            // base hand for every glove category.
            const float maximumSurfaceDistance = 0.012f;
            const float maximumSurfaceDistanceSquared =
                maximumSurfaceDistance * maximumSurfaceDistance;
            var covered = new bool[SourceVertices.Length];
            for (int vertex = 0; vertex < SourceVertices.Length; vertex++)
            {
                byte side = FirstPersonSides[vertex];
                if (side == 0)
                {
                    continue;
                }
                Vector3 point = SourceVertices[vertex].Position;
                foreach (AvatarBatch outer in outerHands)
                {
                    for (int triangle = 0;
                        triangle < outer.Indices.Length;
                        triangle += 3)
                    {
                        int index0 = (ushort)outer.Indices[triangle];
                        int index1 = (ushort)outer.Indices[triangle + 1];
                        int index2 = (ushort)outer.Indices[triangle + 2];
                        if (outer.FirstPersonSides[index0] != side ||
                            outer.FirstPersonSides[index1] != side ||
                            outer.FirstPersonSides[index2] != side)
                        {
                            continue;
                        }
                        Vector3 closest = ClosestPointOnTriangle(
                            point,
                            outer.SourceVertices[index0].Position,
                            outer.SourceVertices[index1].Position,
                            outer.SourceVertices[index2].Position);
                        if (Vector3.DistanceSquared(point, closest) <=
                            maximumSurfaceDistanceSquared)
                        {
                            covered[vertex] = true;
                            break;
                        }
                    }
                    if (covered[vertex])
                    {
                        break;
                    }
                }
            }

            var visible = new List<short>(Indices.Length);
            for (int triangle = 0; triangle < Indices.Length; triangle += 3)
            {
                short index0 = Indices[triangle];
                short index1 = Indices[triangle + 1];
                short index2 = Indices[triangle + 2];
                if (covered[(ushort)index0] &&
                    covered[(ushort)index1] &&
                    covered[(ushort)index2])
                {
                    continue;
                }
                visible.Add(index0);
                visible.Add(index1);
                visible.Add(index2);
            }
            ThirdPersonIndices = visible.ToArray();
        }

        private static Vector3 ClosestPointOnTriangle(
            Vector3 point,
            Vector3 a,
            Vector3 b,
            Vector3 c)
        {
            Vector3 ab = b - a;
            Vector3 ac = c - a;
            Vector3 ap = point - a;
            float d1 = Vector3.Dot(ab, ap);
            float d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f)
            {
                return a;
            }

            Vector3 bp = point - b;
            float d3 = Vector3.Dot(ab, bp);
            float d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3)
            {
                return b;
            }

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                return a + ab * (d1 / (d1 - d3));
            }

            Vector3 cp = point - c;
            float d5 = Vector3.Dot(ab, cp);
            float d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6)
            {
                return c;
            }

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                return a + ac * (d2 / (d2 - d6));
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f &&
                d4 - d3 >= 0f &&
                d5 - d6 >= 0f)
            {
                return b + (c - b) *
                    ((d4 - d3) /
                     ((d4 - d3) + (d5 - d6)));
            }

            float denominator = 1f / (va + vb + vc);
            float v = vb * denominator;
            float w = vc * denominator;
            return a + ab * v + ac * w;
        }

        internal bool MaterialMatchesSkin(AvatarBatch body)
        {
            if (body == null ||
                (PaletteMask & 1) == 0 ||
                (body.PaletteMask & 1) == 0)
            {
                return false;
            }
            Vector3 hand = new Vector3(
                Palette[0].X,
                Palette[0].Y,
                Palette[0].Z);
            Vector3 skin = new Vector3(
                body.Palette[0].X,
                body.Palette[0].Y,
                body.Palette[0].Z);
            return Vector3.DistanceSquared(hand, skin) <= 0.01f;
        }

        internal void BuildMappedFirstPersonGeometry(
            bool excludeDuplicateBaseHand,
            bool retainStableBaseFingers)
        {
            // Select complete lower-arm branches by skin weights.  This keeps
            // sleeves, wrists, palms and every finger layer while excluding
            // the torso and head.  A small threshold includes elbow seam
            // vertices blended with the upper arm, preventing cut-off cuffs.
            const float minimumBranchWeight = 0.15f;
            var sides = new byte[SourceVertices.Length];
            for (int index = 0; index < SourceVertices.Length; index++)
            {
                float left = HandWeight(
                    SourceVertices[index],
                    (int)AvatarBone.ElbowLeft);
                float right = HandWeight(
                    SourceVertices[index],
                    (int)AvatarBone.ElbowRight);
                float strongest = Math.Max(left, right);
                if (strongest >= minimumBranchWeight)
                {
                    sides[index] = left >= right ? (byte)1 : (byte)2;
                }
            }

            var result = new List<short>();
            for (int triangle = 0; triangle < Indices.Length; triangle += 3)
            {
                short index0 = Indices[triangle];
                short index1 = Indices[triangle + 1];
                short index2 = Indices[triangle + 2];
                byte branchSide = sides[(ushort)index0];
                bool completeArmTriangle =
                    branchSide != 0 &&
                    sides[(ushort)index1] == branchSide &&
                    sides[(ushort)index2] == branchSide;

                // A real equipped glove replaces the entire naked base-body
                // hand. With the 03c8 no-gloves shell, remove only base palm
                // triangles and retain the base mesh's stable finger chains.
                if (excludeDuplicateBaseHand && completeArmTriangle)
                {
                    int wristBone = branchSide == 1
                        ? (int)AvatarBone.WristLeft
                        : (int)AvatarBone.WristRight;
                    const float completeHandWeight = 0.15f;
                    bool duplicateHandTriangle =
                        HandWeight(SourceVertices[(ushort)index0], wristBone) >= completeHandWeight &&
                        HandWeight(SourceVertices[(ushort)index1], wristBone) >= completeHandWeight &&
                        HandWeight(SourceVertices[(ushort)index2], wristBone) >= completeHandWeight;
                    bool stableFingerTriangle =
                        retainStableBaseFingers &&
                        FingerWeight(SourceVertices[(ushort)index0]) >= completeHandWeight &&
                        FingerWeight(SourceVertices[(ushort)index1]) >= completeHandWeight &&
                        FingerWeight(SourceVertices[(ushort)index2]) >= completeHandWeight;
                    if (duplicateHandTriangle && !stableFingerTriangle)
                    {
                        continue;
                    }
                }

                // A small set of base-body palm/cuff vertices is authored to
                // the forearm helper above the elbow branch.  The branch-only
                // pass therefore leaves a visible ring-shaped hole between
                // the palm and the finger roots.  BuildFirstPersonGeometry
                // has already classified the compact watertight hand volume
                // on both sides; union it here to restore those seam faces.
                byte handSide = FirstPersonSides[(ushort)index0];
                bool completeHandTriangle =
                    !excludeDuplicateBaseHand &&
                    handSide != 0 &&
                    FirstPersonSides[(ushort)index1] == handSide &&
                    FirstPersonSides[(ushort)index2] == handSide;
                if (completeArmTriangle || completeHandTriangle)
                {
                    result.Add(index0);
                    result.Add(index1);
                    result.Add(index2);
                }
            }
            MappedFirstPersonIndices = result.ToArray();
        }

        internal void BuildMappedPalmFillGeometry()
        {
            // The no-gloves shell supplies the continuous palm and the seam at
            // each finger root, but never a complete finger triangle. The base
            // body supplies those stable finger chains instead. Keeping normal
            // skin weights on the crossing seam makes the two parts articulate
            // together without the rigid mitten shape or distal shell spikes.
            const float completeFingerWeight = 0.15f;
            var result = new List<short>();
            for (int triangle = 0; triangle < Indices.Length; triangle += 3)
            {
                short index0 = Indices[triangle];
                short index1 = Indices[triangle + 1];
                short index2 = Indices[triangle + 2];
                bool completeFingerTriangle =
                    FingerWeight(SourceVertices[(ushort)index0]) >= completeFingerWeight &&
                    FingerWeight(SourceVertices[(ushort)index1]) >= completeFingerWeight &&
                    FingerWeight(SourceVertices[(ushort)index2]) >= completeFingerWeight;
                if (completeFingerTriangle)
                {
                    continue;
                }
                result.Add(index0);
                result.Add(index1);
                result.Add(index2);
            }
            MappedFirstPersonIndices = result.ToArray();
        }

        internal void TransferMappedSkinWeightsFrom(AvatarBatch donor)
        {
            // The Xbox body and 03c8 bare-hand layer are exported in the same
            // bind space. Their vertices are not index-compatible, so transfer
            // only skin bindings/weights from the nearest donor vertex on the
            // same hand and digit. The shell's positions, normals, UVs and
            // topology stay unchanged, and the original weights remain intact
            // for third-person rendering.
            MappedBindings = new byte[SourceVertices.Length][];
            MappedWeights = new byte[SourceVertices.Length][];
            for (int index = 0; index < SourceVertices.Length; index++)
            {
                MappedBindings[index] =
                    (byte[])SourceVertices[index].Bindings.Clone();
                MappedWeights[index] =
                    (byte[])SourceVertices[index].Weights.Clone();

                byte side = FirstPersonSides[index];
                if (side == 0)
                {
                    continue;
                }

                int region = FingerRegion(SourceVertices[index], side);
                int nearest = FindNearestDonorVertex(
                    donor,
                    index,
                    side,
                    region,
                    -1,
                    true);
                if (nearest < 0)
                {
                    nearest = FindNearestDonorVertex(
                        donor,
                        index,
                        side,
                        region,
                        -1,
                        false);
                }
                if (nearest < 0)
                {
                    continue;
                }

                MappedBindings[index] =
                    (byte[])donor.SourceVertices[nearest].Bindings.Clone();
                MappedWeights[index] =
                    (byte[])donor.SourceVertices[nearest].Weights.Clone();
            }
        }

        private int FindNearestDonorVertex(
            AvatarBatch donor,
            int sourceIndex,
            byte side,
            int region,
            int requiredDominantFingerBone,
            bool requireSameRegion)
        {
            int nearest = -1;
            float nearestDistanceSquared = float.MaxValue;
            Vector3 sourcePosition = SourceVertices[sourceIndex].Position;
            for (int candidate = 0;
                candidate < donor.SourceVertices.Length;
                candidate++)
            {
                if (donor.FirstPersonSides[candidate] != side)
                {
                    continue;
                }
                int candidateRegion = FingerRegion(
                    donor.SourceVertices[candidate],
                    side);
                if (requireSameRegion && candidateRegion != region)
                {
                    continue;
                }
                if (requiredDominantFingerBone >= 0 &&
                    DominantFingerBone(
                        donor.SourceVertices[candidate],
                        side) != requiredDominantFingerBone)
                {
                    continue;
                }

                float distanceSquared = Vector3.DistanceSquared(
                    sourcePosition,
                    donor.SourceVertices[candidate].Position);
                if (distanceSquared < nearestDistanceSquared)
                {
                    nearestDistanceSquared = distanceSquared;
                    nearest = candidate;
                }
            }
            return nearest;
        }

        private static int FingerRegion(AvatarSourceVertex vertex, byte side)
        {
            int firstBase = side == 1
                ? (int)AvatarBone.FingerIndexLeft
                : (int)AvatarBone.FingerIndexRight;
            int secondBase = side == 1
                ? (int)AvatarBone.FingerIndex2Left
                : (int)AvatarBone.FingerIndex2Right;
            int thirdBase = side == 1
                ? (int)AvatarBone.FingerIndex3Left
                : (int)AvatarBone.FingerIndex3Right;
            int thumbBase = side == 1
                ? (int)AvatarBone.FingerThumbLeft
                : (int)AvatarBone.FingerThumbRight;
            int thumbSecond = side == 1
                ? (int)AvatarBone.FingerThumb2Left
                : (int)AvatarBone.FingerThumb2Right;
            int thumbThird = side == 1
                ? (int)AvatarBone.FingerThumb3Left
                : (int)AvatarBone.FingerThumb3Right;

            int bestRegion = -1;
            float bestWeight = 0f;
            for (int region = 0; region < 4; region++)
            {
                float weight = BoneChainWeight(
                    vertex,
                    firstBase + region,
                    secondBase + region,
                    thirdBase + region);
                if (weight > bestWeight)
                {
                    bestWeight = weight;
                    bestRegion = region;
                }
            }
            float thumbWeight = BoneChainWeight(
                vertex,
                thumbBase,
                thumbSecond,
                thumbThird);
            if (thumbWeight > bestWeight)
            {
                bestWeight = thumbWeight;
                bestRegion = 4;
            }
            return bestWeight >= 0.15f ? bestRegion : -1;
        }

        private static int DominantFingerBone(
            AvatarSourceVertex vertex,
            byte side)
        {
            int bestBone = -1;
            byte bestWeight = 0;
            for (int influence = 0; influence < 4; influence++)
            {
                int bone = vertex.Bindings[influence];
                if (FingerRegionForBone(bone, side) >= 0 &&
                    vertex.Weights[influence] > bestWeight)
                {
                    bestWeight = vertex.Weights[influence];
                    bestBone = bone;
                }
            }
            return bestWeight >= 38 ? bestBone : -1;
        }

        private static int FingerRegionForBone(int bone, byte side)
        {
            int firstBase = side == 1
                ? (int)AvatarBone.FingerIndexLeft
                : (int)AvatarBone.FingerIndexRight;
            int secondBase = side == 1
                ? (int)AvatarBone.FingerIndex2Left
                : (int)AvatarBone.FingerIndex2Right;
            int thirdBase = side == 1
                ? (int)AvatarBone.FingerIndex3Left
                : (int)AvatarBone.FingerIndex3Right;
            for (int region = 0; region < 4; region++)
            {
                if (bone == firstBase + region ||
                    bone == secondBase + region ||
                    bone == thirdBase + region)
                {
                    return region;
                }
            }
            int thumbBase = side == 1
                ? (int)AvatarBone.FingerThumbLeft
                : (int)AvatarBone.FingerThumbRight;
            int thumbSecond = side == 1
                ? (int)AvatarBone.FingerThumb2Left
                : (int)AvatarBone.FingerThumb2Right;
            int thumbThird = side == 1
                ? (int)AvatarBone.FingerThumb3Left
                : (int)AvatarBone.FingerThumb3Right;
            return bone == thumbBase || bone == thumbSecond || bone == thumbThird
                ? 4
                : -1;
        }

        internal void BuildFirstPersonGeometry(
            Vector3 wristLeft,
            Vector3 wristRight)
        {
            // Clothing meshes do not use a consistent weight boundary at the
            // palm: some perfectly visible palm vertices are shared with the
            // lower-arm bone.  Classify the compact bind-space volume around
            // each wrist, then keep only triangles wholly belonging to one
            // hand.  This preserves a watertight palm/thumb/finger surface and
            // still excludes sleeves, torso and the opposite arm.
            const float handRadius = 0.225f;
            FirstPersonSides = new byte[SourceVertices.Length];
            FirstPersonUsed = new bool[SourceVertices.Length];
            for (int index = 0; index < SourceVertices.Length; index++)
            {
                float leftDistance = Vector3.Distance(
                    SourceVertices[index].Position,
                    wristLeft);
                float rightDistance = Vector3.Distance(
                    SourceVertices[index].Position,
                    wristRight);
                float nearest = Math.Min(leftDistance, rightDistance);
                if (nearest <= handRadius)
                {
                    FirstPersonSides[index] = leftDistance <= rightDistance
                        ? (byte)1
                        : (byte)2;
                }
            }

            var result = new List<short>();
            for (int triangle = 0; triangle < Indices.Length; triangle += 3)
            {
                short index0 = Indices[triangle];
                short index1 = Indices[triangle + 1];
                short index2 = Indices[triangle + 2];
                byte side = FirstPersonSides[(ushort)index0];
                // Keep both hands on their native skeleton branches.  The
                // earlier right-only filter removed the support hand entirely;
                // retaining side identity here lets PropLeft and PropRight pose
                // independently without merging their fingers.
                if (side != 0 &&
                    FirstPersonSides[(ushort)index1] == side &&
                    FirstPersonSides[(ushort)index2] == side)
                {
                    result.Add(index0);
                    result.Add(index1);
                    result.Add(index2);
                    FirstPersonUsed[(ushort)index0] = true;
                    FirstPersonUsed[(ushort)index1] = true;
                    FirstPersonUsed[(ushort)index2] = true;
                }
            }
            FirstPersonIndices = result.ToArray();
        }

        private static float ArmWeight(AvatarSourceVertex vertex)
        {
            return HandWeight(vertex, (int)AvatarBone.WristLeft) +
                HandWeight(vertex, (int)AvatarBone.WristRight);
        }

        internal static float HandWeight(AvatarSourceVertex vertex, int wristBone)
        {
            float result = 0f;
            for (int influence = 0; influence < 4; influence++)
            {
                if (IsDescendantOf(vertex.Bindings[influence], wristBone))
                {
                    result += vertex.Weights[influence] / 255f;
                }
            }
            return result;
        }

        internal static float DirectBoneWeight(
            AvatarSourceVertex vertex,
            int bone)
        {
            float result = 0f;
            for (int influence = 0; influence < 4; influence++)
            {
                if (vertex.Bindings[influence] == bone)
                {
                    result += vertex.Weights[influence] / 255f;
                }
            }
            return result;
        }

        internal static float BoneChainWeight(
            AvatarSourceVertex vertex,
            int first,
            int second,
            int third)
        {
            float result = 0f;
            for (int influence = 0; influence < 4; influence++)
            {
                int bone = vertex.Bindings[influence];
                if (bone == first || bone == second || bone == third)
                {
                    result += vertex.Weights[influence] / 255f;
                }
            }
            return result;
        }

        private static bool HasFingerTriangles(
            AvatarSourceVertex[] vertices,
            short[] indices)
        {
            int fingerTriangles = 0;
            for (int triangle = 0; triangle < indices.Length; triangle += 3)
            {
                if (FingerWeight(vertices[(ushort)indices[triangle]]) >= 0.5f &&
                    FingerWeight(vertices[(ushort)indices[triangle + 1]]) >= 0.5f &&
                    FingerWeight(vertices[(ushort)indices[triangle + 2]]) >= 0.5f)
                {
                    fingerTriangles++;
                }
            }
            return fingerTriangles >= 10;
        }

        private static float FingerWeight(AvatarSourceVertex vertex)
        {
            float result = 0f;
            for (int influence = 0; influence < 4; influence++)
            {
                int bone = vertex.Bindings[influence];
                if (bone >= (int)AvatarBone.FingerIndexLeft &&
                    bone <= (int)AvatarBone.FingerThumb3Right &&
                    bone != (int)AvatarBone.PropLeft &&
                    bone != (int)AvatarBone.SpecialLeft &&
                    bone != (int)AvatarBone.PropRight &&
                    bone != (int)AvatarBone.SpecialRight)
                {
                    result += vertex.Weights[influence] / 255f;
                }
            }
            return result;
        }

        private static float ThumbWeight(AvatarSourceVertex vertex)
        {
            float result = 0f;
            for (int influence = 0; influence < 4; influence++)
            {
                int bone = vertex.Bindings[influence];
                if (bone == (int)AvatarBone.FingerThumbLeft ||
                    bone == (int)AvatarBone.FingerThumbRight ||
                    bone == (int)AvatarBone.FingerThumb2Left ||
                    bone == (int)AvatarBone.FingerThumb2Right ||
                    bone == (int)AvatarBone.FingerThumb3Left ||
                    bone == (int)AvatarBone.FingerThumb3Right)
                {
                    result += vertex.Weights[influence] / 255f;
                }
            }
            return result;
        }

        private static bool IsArmBone(int bone)
        {
            return IsDescendantOf(bone, (int)AvatarBone.WristLeft) ||
                IsDescendantOf(bone, (int)AvatarBone.WristRight);
        }

        private static bool IsDescendantOf(int bone, int ancestor)
        {
            while (bone >= 0 && bone < Avatar.DefaultParentBones.Count)
            {
                if (bone == ancestor)
                {
                    return true;
                }
                bone = Avatar.DefaultParentBones[bone];
            }
            return false;
        }
    }

    internal struct AvatarSourceVertex
    {
        internal Vector3 Position;
        internal Vector3 Normal;
        internal byte[] Bindings;
        internal byte[] Weights;
    }

    internal struct AvatarDrawVertex : IVertexType
    {
        internal Vector3 Position;
        internal Vector3 Normal;
        internal Vector2 TextureCoordinate;
        internal Color Color;

        private static readonly VertexDeclaration Declaration = new VertexDeclaration(
            new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
            new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
            new VertexElement(24, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
            new VertexElement(32, VertexElementFormat.Color, VertexElementUsage.Color, 0));

        internal AvatarDrawVertex(Vector3 position, Vector3 normal, Vector2 uv, Color color)
        {
            Position = position;
            Normal = normal;
            TextureCoordinate = uv;
            Color = color;
        }

        VertexDeclaration IVertexType.VertexDeclaration
        {
            get { return Declaration; }
        }
    }

    // Multiplayer protocol v1. Capability discovery uses a decorated copy of
    // the stock PlayerExistsMessage, which vanilla and unmodified OpenClassic
    // understand and safely ignore as a duplicate. The custom message type is
    // never sent until a peer proves support with that stock-safe marker.
    public static class AvatarNetworkBridge
    {
        private const byte ProtocolVersion = 1;
        private const byte HelloPacket = 1;
        private const byte ManifestPacket = 2;
        private const byte RequestPacket = 3;
        private const byte ChunkPacket = 4;
        private const int ChunkPayload = 3000;
        private const int MaximumAssetBytes = 4 * 1024 * 1024;
        private const int MaximumIncomingTransfers = 8;
        private const int ChunksPerUpdate = 2;
        private static readonly byte[] CapabilityMarker =
        {
            0x4f, 0x43, 0x58, 0x41, 0x43, 0x41, 0x50,
            ProtocolVersion
        };
        private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(45);
        private static readonly Dictionary<byte, PlayerBinding> Players =
            new Dictionary<byte, PlayerBinding>();
        private static readonly Dictionary<byte, string> RemoteAssetPaths =
            new Dictionary<byte, string>();
        private static readonly Dictionary<string, OutgoingOffer> Offers =
            new Dictionary<string, OutgoingOffer>(StringComparer.Ordinal);
        private static readonly Dictionary<string, IncomingTransfer> Incoming =
            new Dictionary<string, IncomingTransfer>(StringComparer.Ordinal);
        private static readonly Queue<OutgoingTransfer> Outgoing =
            new Queue<OutgoingTransfer>();
        private static readonly Dictionary<byte, NetworkGamer> PendingHello =
            new Dictionary<byte, NetworkGamer>();
        private static readonly Dictionary<byte, NetworkGamer> HelloSent =
            new Dictionary<byte, NetworkGamer>();
        private static readonly Dictionary<byte, NetworkGamer> PeerReady =
            new Dictionary<byte, NetworkGamer>();
        private static uint _nextTransferId = 1;
        private static DateTime _nextCleanupUtc = DateTime.MinValue;
        private static LocalSnapshot _localSnapshot;
        private static bool _capabilityAdvertisementPending = true;

        public static void Register()
        {
            Assembly entry = Assembly.GetEntryAssembly();
            Assembly current = Assembly.GetExecutingAssembly();
            ReflectionTools.RegisterAssembly(entry ?? current, current);
            _capabilityAdvertisementPending = true;
        }

        internal static void NotePlayer(
            NetworkGamer gamer,
            Avatar avatar,
            Model fallbackModel)
        {
            if (gamer == null || avatar == null || fallbackModel == null)
            {
                return;
            }
            Players[gamer.Id] = new PlayerBinding
            {
                Gamer = gamer,
                Avatar = avatar,
                FallbackModel = fallbackModel
            };
        }

        internal static string GetAssetPath(NetworkGamer gamer)
        {
            if (gamer == null)
            {
                return null;
            }
            if (gamer.IsLocal)
            {
                return LocalAssetPath;
            }
            string path;
            return RemoteAssetPaths.TryGetValue(gamer.Id, out path)
                ? path
                : null;
        }

        public static void OnGamerJoined(NetworkGamer gamer)
        {
            if (gamer == null)
            {
                return;
            }

            _capabilityAdvertisementPending = true;
            if (!gamer.IsLocal)
            {
                // Gamer IDs can be reused after a disconnect. Do not let a new
                // player inherit the previous occupant's cached model binding
                // or protocol capability.
                PlayerBinding binding;
                if (Players.TryGetValue(gamer.Id, out binding) &&
                    binding.Gamer != gamer)
                {
                    Players.Remove(gamer.Id);
                    RemoteAssetPaths.Remove(gamer.Id);
                }
                PendingHello.Remove(gamer.Id);
                HelloSent.Remove(gamer.Id);
                PeerReady.Remove(gamer.Id);
            }
        }

        public static bool OnMessage(Message message)
        {
            PlayerExistsMessage playerExists = message as PlayerExistsMessage;
            if (playerExists != null)
            {
                if (playerExists.Sender != null &&
                    !playerExists.Sender.IsLocal)
                {
                    byte[] cleanDescription;
                    if (TryStripCapabilityMarker(
                        playerExists.AvatarDescriptionData,
                        out cleanDescription))
                    {
                        // Never let the add-on marker leak into normal game
                        // avatar state, even though duplicate PlayerExists
                        // packets are ignored after the player is constructed.
                        playerExists.AvatarDescriptionData = cleanDescription;
                        MarkPeerCapable(playerExists.Sender);
                    }
                }
                return false;
            }

            ZZOpenClassicAvatarSyncMessage packet =
                message as ZZOpenClassicAvatarSyncMessage;
            if (packet == null)
            {
                return false;
            }
            if (packet.Protocol != ProtocolVersion ||
                packet.Sender == null || packet.Sender.IsLocal)
            {
                return true;
            }
            LocalNetworkGamer local = LocalGamer;
            if (local == null)
            {
                // Consume the mod packet even before local-player setup. The
                // stock handler dereferences MyNetworkGamer unconditionally.
                return true;
            }

            if (!IsPeerCapable(packet.Sender))
            {
                // A custom packet without the stock-safe capability marker is
                // stale, spoofed, or from an incompatible protocol revision.
                return true;
            }

            switch (packet.Kind)
            {
                case HelloPacket:
                    HandleHello(packet.Sender);
                    break;
                case ManifestPacket:
                    HandleManifest(packet);
                    break;
                case RequestPacket:
                    HandleRequest(packet);
                    break;
                case ChunkPacket:
                    HandleChunk(packet);
                    break;
            }
            return true;
        }

        public static void Update()
        {
            LocalNetworkGamer local = LocalGamer;
            if (local == null)
            {
                return;
            }

            FlushCapabilityAdvertisement(local);
            FlushPendingHello(local);

            int sent = 0;
            while (sent < ChunksPerUpdate && Outgoing.Count > 0)
            {
                OutgoingTransfer transfer = Outgoing.Peek();
                if (transfer.Target == null || transfer.Target.HasLeftSession ||
                    transfer.NextChunk >= transfer.ChunkCount)
                {
                    Outgoing.Dequeue();
                    continue;
                }

                int offset = transfer.NextChunk * ChunkPayload;
                int length = Math.Min(
                    ChunkPayload,
                    transfer.Bytes.Length - offset);
                byte[] payload = new byte[length];
                Buffer.BlockCopy(transfer.Bytes, offset, payload, 0, length);
                ZZOpenClassicAvatarSyncMessage.SendPacket(
                    local,
                    transfer.Target,
                    ChunkPacket,
                    transfer.TransferId,
                    transfer.Bytes.Length,
                    (ushort)transfer.NextChunk,
                    (ushort)transfer.ChunkCount,
                    transfer.Hash,
                    payload);
                transfer.NextChunk++;
                sent++;
                if (transfer.NextChunk >= transfer.ChunkCount)
                {
                    Outgoing.Dequeue();
                }
            }

            DateTime now = DateTime.UtcNow;
            if (now >= _nextCleanupUtc)
            {
                Cleanup(now);
                _nextCleanupUtc = now.AddSeconds(5);
            }

            ApplyThirdPersonItemAnchors();
        }

        private static void QueueHello(NetworkGamer gamer)
        {
            if (gamer == null || gamer.IsLocal || gamer.HasLeftSession)
            {
                return;
            }
            NetworkGamer sent;
            if (HelloSent.TryGetValue(gamer.Id, out sent) && sent == gamer)
            {
                return;
            }
            HelloSent.Remove(gamer.Id);
            PendingHello[gamer.Id] = gamer;
        }

        private static void MarkPeerCapable(NetworkGamer gamer)
        {
            if (gamer == null || gamer.IsLocal || gamer.HasLeftSession)
            {
                return;
            }
            PeerReady[gamer.Id] = gamer;
            QueueHello(gamer);
        }

        internal static bool IsPeerCapable(NetworkGamer gamer)
        {
            if (gamer == null || gamer.IsLocal || gamer.HasLeftSession)
            {
                return false;
            }
            NetworkGamer ready;
            return PeerReady.TryGetValue(gamer.Id, out ready) &&
                ready == gamer;
        }

        internal static byte[] AppendCapabilityMarker(byte[] description)
        {
            int descriptionLength = description == null
                ? 0
                : description.Length;
            byte[] decorated = new byte[
                descriptionLength + CapabilityMarker.Length];
            if (descriptionLength > 0)
            {
                Buffer.BlockCopy(
                    description,
                    0,
                    decorated,
                    0,
                    descriptionLength);
            }
            Buffer.BlockCopy(
                CapabilityMarker,
                0,
                decorated,
                descriptionLength,
                CapabilityMarker.Length);
            return decorated;
        }

        internal static bool TryStripCapabilityMarker(
            byte[] decorated,
            out byte[] description)
        {
            description = decorated;
            if (decorated == null ||
                decorated.Length < CapabilityMarker.Length)
            {
                return false;
            }
            int markerOffset = decorated.Length - CapabilityMarker.Length;
            for (int index = 0; index < CapabilityMarker.Length; index++)
            {
                if (decorated[markerOffset + index] !=
                    CapabilityMarker[index])
                {
                    return false;
                }
            }
            description = new byte[markerOffset];
            if (markerOffset > 0)
            {
                Buffer.BlockCopy(
                    decorated,
                    0,
                    description,
                    0,
                    markerOffset);
            }
            return true;
        }

        private static void FlushCapabilityAdvertisement(
            LocalNetworkGamer local)
        {
            if (!_capabilityAdvertisementPending || local == null)
            {
                return;
            }
            CastleMinerZGame game = CastleMinerZGame.Instance;
            Player player = game == null ? null : game.LocalPlayer;
            AvatarDescription description =
                player == null || player.Avatar == null
                    ? null
                    : player.Avatar.Description;
            if (description == null || description.Description == null)
            {
                return;
            }

            SendCapabilityAdvertisement(local, description.Description);
            _capabilityAdvertisementPending = false;
        }

        internal static void SendCapabilityAdvertisement(
            LocalNetworkGamer local,
            byte[] description)
        {
            if (local == null || description == null)
            {
                return;
            }
            PlayerExistsMessage.Send(
                local,
                new AvatarDescription(AppendCapabilityMarker(description)),
                false);
        }

        private static void FlushPendingHello(LocalNetworkGamer local)
        {
            var ids = new List<byte>(PendingHello.Keys);
            foreach (byte id in ids)
            {
                NetworkGamer peer = PendingHello[id];
                if (peer == null || peer.HasLeftSession)
                {
                    PendingHello.Remove(id);
                    HelloSent.Remove(id);
                    PeerReady.Remove(id);
                    continue;
                }
                ZZOpenClassicAvatarSyncMessage.SendHello(local, peer);
                PendingHello.Remove(id);
                HelloSent[id] = peer;
            }
        }

        private static void ApplyThirdPersonItemAnchors()
        {
            foreach (PlayerBinding binding in Players.Values)
            {
                if (binding == null || binding.Avatar == null)
                {
                    continue;
                }
                var imported = binding.Avatar.ProxyModelEntity as
                    ImportedAvatarModelEntity;
                Vector3 target;
                if (imported == null ||
                    !imported.TryGetThirdPersonPropTranslation(out target))
                {
                    continue;
                }

                Entity itemAnchor = binding.Avatar.GetAvatarPart(
                    AvatarBone.PropRight);
                Matrix transform = binding.Avatar.GetBoneToAvatar(
                    AvatarBone.PropRight);
                transform.Translation = target;
                itemAnchor.LocalToParent = transform;
            }
        }

        private static void HandleHello(NetworkGamer target)
        {
            LocalNetworkGamer local = LocalGamer;
            LocalSnapshot snapshot = GetLocalSnapshot();
            if (local == null || snapshot == null || target.HasLeftSession)
            {
                return;
            }

            uint transferId = NextTransferId();
            int chunkCount = ChunkCount(snapshot.Bytes.Length);
            string key = TransferKey(target.Id, transferId);
            Offers[key] = new OutgoingOffer
            {
                Target = target,
                Snapshot = snapshot,
                TransferId = transferId,
                ExpiresUtc = DateTime.UtcNow + TransferTimeout
            };
            ZZOpenClassicAvatarSyncMessage.SendPacket(
                local,
                target,
                ManifestPacket,
                transferId,
                snapshot.Bytes.Length,
                0,
                (ushort)chunkCount,
                snapshot.Hash,
                EmptyBytes);
        }

        private static void HandleManifest(ZZOpenClassicAvatarSyncMessage packet)
        {
            if (!ValidManifest(packet))
            {
                return;
            }

            string cached = CachePath(packet.Hash);
            if (FileMatches(cached, packet.TotalLength, packet.Hash))
            {
                SetRemoteAsset(packet.Sender, cached, packet.Hash);
                return;
            }
            if (Incoming.Count >= MaximumIncomingTransfers)
            {
                return;
            }

            RemoveIncomingFor(packet.Sender.Id);
            string key = TransferKey(packet.Sender.Id, packet.TransferId);
            Incoming[key] = new IncomingTransfer
            {
                Sender = packet.Sender,
                TransferId = packet.TransferId,
                TotalLength = packet.TotalLength,
                ChunkCount = packet.ChunkCount,
                Hash = (byte[])packet.Hash.Clone(),
                Bytes = new byte[packet.TotalLength],
                Received = new BitArray(packet.ChunkCount),
                ExpiresUtc = DateTime.UtcNow + TransferTimeout
            };
            ZZOpenClassicAvatarSyncMessage.SendPacket(
                LocalGamer,
                packet.Sender,
                RequestPacket,
                packet.TransferId,
                packet.TotalLength,
                0,
                packet.ChunkCount,
                packet.Hash,
                EmptyBytes);
        }

        private static void HandleRequest(ZZOpenClassicAvatarSyncMessage packet)
        {
            string key = TransferKey(packet.Sender.Id, packet.TransferId);
            OutgoingOffer offer;
            if (!Offers.TryGetValue(key, out offer) ||
                offer.Target != packet.Sender ||
                DateTime.UtcNow > offer.ExpiresUtc ||
                packet.TotalLength != offer.Snapshot.Bytes.Length ||
                packet.ChunkCount != ChunkCount(offer.Snapshot.Bytes.Length) ||
                !HashesEqual(packet.Hash, offer.Snapshot.Hash))
            {
                return;
            }
            Offers.Remove(key);
            Outgoing.Enqueue(new OutgoingTransfer
            {
                Target = packet.Sender,
                TransferId = packet.TransferId,
                Bytes = offer.Snapshot.Bytes,
                Hash = offer.Snapshot.Hash,
                ChunkCount = packet.ChunkCount,
                NextChunk = 0
            });
        }

        private static void HandleChunk(ZZOpenClassicAvatarSyncMessage packet)
        {
            string key = TransferKey(packet.Sender.Id, packet.TransferId);
            IncomingTransfer transfer;
            if (!Incoming.TryGetValue(key, out transfer) ||
                transfer.Sender != packet.Sender ||
                packet.TotalLength != transfer.TotalLength ||
                packet.ChunkCount != transfer.ChunkCount ||
                packet.ChunkIndex >= transfer.ChunkCount ||
                !HashesEqual(packet.Hash, transfer.Hash))
            {
                return;
            }

            int offset = packet.ChunkIndex * ChunkPayload;
            int expected = Math.Min(ChunkPayload, transfer.TotalLength - offset);
            if (packet.Payload == null || packet.Payload.Length != expected)
            {
                return;
            }
            if (!transfer.Received[packet.ChunkIndex])
            {
                Buffer.BlockCopy(
                    packet.Payload,
                    0,
                    transfer.Bytes,
                    offset,
                    packet.Payload.Length);
                transfer.Received[packet.ChunkIndex] = true;
                transfer.ReceivedCount++;
            }
            transfer.ExpiresUtc = DateTime.UtcNow + TransferTimeout;
            if (transfer.ReceivedCount != transfer.ChunkCount)
            {
                return;
            }

            Incoming.Remove(key);
            byte[] actual = ComputeHash(transfer.Bytes);
            if (!HashesEqual(actual, transfer.Hash))
            {
                return;
            }
            string cached = InstallCacheFile(
                transfer.Bytes,
                transfer.Hash,
                transfer.TransferId);
            SetRemoteAsset(transfer.Sender, cached, transfer.Hash);
        }

        private static bool ValidManifest(ZZOpenClassicAvatarSyncMessage packet)
        {
            if (packet.TotalLength <= 0 ||
                packet.TotalLength > MaximumAssetBytes ||
                packet.Hash == null || packet.Hash.Length != 32)
            {
                return false;
            }
            int chunks = ChunkCount(packet.TotalLength);
            return packet.ChunkCount == chunks && chunks <= ushort.MaxValue;
        }

        private static void SetRemoteAsset(
            NetworkGamer gamer,
            string path,
            byte[] hash)
        {
            if (gamer == null || string.IsNullOrEmpty(path))
            {
                return;
            }
            RemoteAssetPaths[gamer.Id] = path;
            PlayerBinding binding;
            if (!Players.TryGetValue(gamer.Id, out binding) ||
                binding.Gamer != gamer || binding.Avatar == null ||
                binding.FallbackModel == null)
            {
                return;
            }
            string hashText = HashText(hash);
            if (string.Equals(
                binding.AppliedHash,
                hashText,
                StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                StockModelEntity previous =
                    binding.Avatar.ProxyModelEntity as StockModelEntity;
                var replacement = new ImportedAvatarModelEntity(
                    binding.FallbackModel,
                    binding.Avatar,
                    path);
                if (previous != null)
                {
                    replacement.AmbientLight = previous.AmbientLight;
                    Array.Copy(
                        previous.DirectLightColor,
                        replacement.DirectLightColor,
                        Math.Min(previous.DirectLightColor.Length,
                            replacement.DirectLightColor.Length));
                    Array.Copy(
                        previous.DirectLightDirection,
                        replacement.DirectLightDirection,
                        Math.Min(previous.DirectLightDirection.Length,
                            replacement.DirectLightDirection.Length));
                }
                binding.Avatar.ProxyModelEntity = replacement;
                binding.AppliedHash = hashText;
            }
            catch (Exception exception)
            {
                ImportedAvatarModelEntity.WriteFailure(exception);
            }
        }

        private static LocalSnapshot GetLocalSnapshot()
        {
            string path = LocalAssetPath;
            if (!File.Exists(path))
            {
                return null;
            }
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaximumAssetBytes)
            {
                return null;
            }
            if (_localSnapshot != null &&
                _localSnapshot.Length == info.Length &&
                _localSnapshot.LastWriteUtc == info.LastWriteTimeUtc)
            {
                return _localSnapshot;
            }
            byte[] bytes = File.ReadAllBytes(path);
            _localSnapshot = new LocalSnapshot
            {
                Bytes = bytes,
                Hash = ComputeHash(bytes),
                Length = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc
            };
            return _localSnapshot;
        }

        private static string InstallCacheFile(
            byte[] bytes,
            byte[] hash,
            uint transferId)
        {
            string folder = CacheFolder;
            Directory.CreateDirectory(folder);
            string finalPath = CachePath(hash);
            if (FileMatches(finalPath, bytes.Length, hash))
            {
                return finalPath;
            }
            string temporary = Path.Combine(
                folder,
                HashText(hash) + "." + transferId.ToString("x8") + ".part");
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(finalPath))
            {
                File.Replace(
                    temporary,
                    finalPath,
                    finalPath + ".previous",
                    true);
            }
            else
            {
                File.Move(temporary, finalPath);
            }
            return finalPath;
        }

        private static bool FileMatches(string path, int length, byte[] hash)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != length)
                {
                    return false;
                }
                using (FileStream stream = File.OpenRead(path))
                using (SHA256 algorithm = SHA256.Create())
                {
                    return HashesEqual(algorithm.ComputeHash(stream), hash);
                }
            }
            catch
            {
                return false;
            }
        }

        private static void Cleanup(DateTime now)
        {
            var expiredOffers = new List<string>();
            foreach (KeyValuePair<string, OutgoingOffer> pair in Offers)
            {
                if (pair.Value.ExpiresUtc < now ||
                    pair.Value.Target == null || pair.Value.Target.HasLeftSession)
                {
                    expiredOffers.Add(pair.Key);
                }
            }
            foreach (string key in expiredOffers)
            {
                Offers.Remove(key);
            }

            var expiredIncoming = new List<string>();
            foreach (KeyValuePair<string, IncomingTransfer> pair in Incoming)
            {
                if (pair.Value.ExpiresUtc < now ||
                    pair.Value.Sender == null || pair.Value.Sender.HasLeftSession)
                {
                    expiredIncoming.Add(pair.Key);
                }
            }
            foreach (string key in expiredIncoming)
            {
                Incoming.Remove(key);
            }
        }

        private static void RemoveIncomingFor(byte gamerId)
        {
            string prefix = gamerId.ToString("x2") + ":";
            var keys = new List<string>();
            foreach (string key in Incoming.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    keys.Add(key);
                }
            }
            foreach (string key in keys)
            {
                Incoming.Remove(key);
            }
        }

        private static int ChunkCount(int length)
        {
            return (length + ChunkPayload - 1) / ChunkPayload;
        }

        private static uint NextTransferId()
        {
            uint result = _nextTransferId++;
            if (_nextTransferId == 0)
            {
                _nextTransferId = 1;
            }
            return result;
        }

        private static string TransferKey(byte gamerId, uint transferId)
        {
            return gamerId.ToString("x2") + ":" + transferId.ToString("x8");
        }

        private static byte[] ComputeHash(byte[] bytes)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                return algorithm.ComputeHash(bytes);
            }
        }

        private static bool HashesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }
            int difference = 0;
            for (int index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }
            return difference == 0;
        }

        private static string HashText(byte[] hash)
        {
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private static string CachePath(byte[] hash)
        {
            return Path.Combine(CacheFolder, HashText(hash) + ".ocavatar");
        }

        private static LocalNetworkGamer LocalGamer
        {
            get
            {
                CastleMinerZGame game = CastleMinerZGame.Instance;
                if (game == null)
                {
                    return null;
                }
                return game.MyNetworkGamer;
            }
        }

        private static string AvatarFolder
        {
            get
            {
                return Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "OpenClassic Addons",
                    "Xbox Avatar");
            }
        }

        private static string LocalAssetPath
        {
            get { return Path.Combine(AvatarFolder, "avatar.ocavatar"); }
        }

        private static string CacheFolder
        {
            get { return Path.Combine(AvatarFolder, "Cache"); }
        }

        private static readonly byte[] EmptyBytes = new byte[0];

        private sealed class PlayerBinding
        {
            internal NetworkGamer Gamer;
            internal Avatar Avatar;
            internal Model FallbackModel;
            internal string AppliedHash;
        }

        private sealed class LocalSnapshot
        {
            internal byte[] Bytes;
            internal byte[] Hash;
            internal long Length;
            internal DateTime LastWriteUtc;
        }

        private sealed class OutgoingOffer
        {
            internal NetworkGamer Target;
            internal LocalSnapshot Snapshot;
            internal uint TransferId;
            internal DateTime ExpiresUtc;
        }

        private sealed class OutgoingTransfer
        {
            internal NetworkGamer Target;
            internal uint TransferId;
            internal byte[] Bytes;
            internal byte[] Hash;
            internal int ChunkCount;
            internal int NextChunk;
        }

        private sealed class IncomingTransfer
        {
            internal NetworkGamer Sender;
            internal uint TransferId;
            internal int TotalLength;
            internal ushort ChunkCount;
            internal byte[] Hash;
            internal byte[] Bytes;
            internal BitArray Received;
            internal int ReceivedCount;
            internal DateTime ExpiresUtc;
        }
    }

    public sealed class ZZOpenClassicAvatarSyncMessage : CastleMinerZMessage
    {
        internal byte Protocol;
        internal byte Kind;
        internal uint TransferId;
        internal int TotalLength;
        internal ushort ChunkIndex;
        internal ushort ChunkCount;
        internal byte[] Hash;
        internal byte[] Payload;

        private ZZOpenClassicAvatarSyncMessage()
        {
            Hash = new byte[32];
            Payload = new byte[0];
        }

        public override MessageTypes MessageType
        {
            get { return MessageTypes.System; }
        }

        protected override SendDataOptions SendDataOptions
        {
            get { return SendDataOptions.ReliableInOrder; }
        }

        protected override void RecieveData(BinaryReader reader)
        {
            Protocol = reader.ReadByte();
            Kind = reader.ReadByte();
            TransferId = reader.ReadUInt32();
            TotalLength = reader.ReadInt32();
            ChunkIndex = reader.ReadUInt16();
            ChunkCount = reader.ReadUInt16();
            Hash = reader.ReadBytes(32);
            int payloadLength = reader.ReadUInt16();
            if (payloadLength < 0 || payloadLength > 3000)
            {
                throw new InvalidDataException("Invalid OpenClassic avatar chunk length.");
            }
            Payload = reader.ReadBytes(payloadLength);
            if (Hash.Length != 32 || Payload.Length != payloadLength)
            {
                throw new EndOfStreamException("Truncated OpenClassic avatar packet.");
            }
        }

        protected override void SendData(BinaryWriter writer)
        {
            writer.Write(Protocol);
            writer.Write(Kind);
            writer.Write(TransferId);
            writer.Write(TotalLength);
            writer.Write(ChunkIndex);
            writer.Write(ChunkCount);
            writer.Write(Hash, 0, 32);
            writer.Write((ushort)Payload.Length);
            writer.Write(Payload);
        }

        internal static void SendHello(
            LocalNetworkGamer from,
            NetworkGamer to)
        {
            SendPacket(
                from,
                to,
                1,
                0,
                0,
                0,
                0,
                new byte[32],
                new byte[0]);
        }

        internal static void SendPacket(
            LocalNetworkGamer from,
            NetworkGamer to,
            byte kind,
            uint transferId,
            int totalLength,
            ushort chunkIndex,
            ushort chunkCount,
            byte[] hash,
            byte[] payload)
        {
            if (from == null || to == null || to.HasLeftSession ||
                !AvatarNetworkBridge.IsPeerCapable(to) ||
                hash == null || hash.Length != 32 || payload == null ||
                payload.Length > 3000)
            {
                return;
            }
            ZZOpenClassicAvatarSyncMessage packet =
                GetSendInstance<ZZOpenClassicAvatarSyncMessage>();
            packet.Protocol = 1;
            packet.Kind = kind;
            packet.TransferId = transferId;
            packet.TotalLength = totalLength;
            packet.ChunkIndex = chunkIndex;
            packet.ChunkCount = chunkCount;
            packet.Hash = hash;
            packet.Payload = payload;
            packet.DoSend(from, to);
        }
    }
}
