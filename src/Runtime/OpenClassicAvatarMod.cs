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

// The standalone installer ships this runtime under its own name, so the
// namespace, the game-folder layout and the user-facing product name are all
// selected here. build.ps1 defines XBOX_AVATAR_BRAND for that build.
//
// The namespace is part of the brand because patching writes it into the game
// assembly's metadata: an OpenClassic-named hook would be visible in any
// decompiler looking at a standalone install.
#if XBOX_AVATAR_BRAND
namespace XboxAvatar
#else
namespace OpenClassic.XboxAvatar
#endif
{
    /// <summary>
    /// Editable per-height nudge for the third-person held item.
    ///
    /// The grip is computed from the avatar's own finger bones and is already
    /// proportional to build, so this is zero by default and exists for tuning
    /// the last centimetre by eye without a rebuild. The file is re-read while
    /// the game runs, so a value can be changed and seen on the next frame.
    ///
    /// Rows are "build offsetX offsetY offsetZ", and the offset for a build
    /// between two rows is interpolated, so a handful of rows covers the whole
    /// height range smoothly rather than in steps.
    /// </summary>
    internal static class ItemTuning
    {
        private const string FileName = "item-tuning.txt";

        /// <summary>
        /// Which point the held item is anchored to.
        ///
        /// The item anchor and the imported mesh end up in the same avatar
        /// space but are driven from two different skeletons: the anchor from
        /// the stock 1.6 m rig's bind pose, the mesh from the imported
        /// avatar's own. Which correction closes that gap is a question about
        /// the rendered result, so it is switchable at runtime and can be
        /// answered by looking instead of by argument.
        /// </summary>
        internal enum Placement
        {
            /// <summary>
            /// The imported avatar's prop bone, rotation as well as position.
            ///
            /// The only one that also fixes the anchor's orientation, so it is
            /// the only one that can seat every item at once: each item is
            /// offset from the anchor by a different amount along the anchor's
            /// own axes, so a wrong rotation misplaces each of them
            /// differently and moves them all as the arm pitches.
            /// </summary>
            Hand,
            /// <summary>The imported avatar's finger-centre grip.</summary>
            Grip,
            /// <summary>The imported avatar's own PropRight attach bone.</summary>
            Prop,
            /// <summary>Stock anchor nudged by the imported hand's prop-to-grip offset.</summary>
            Shift,
            /// <summary>Whatever the unmodded game does, as a baseline.</summary>
            Stock,
        }

        private static readonly List<KeyValuePair<float, Vector3>> Rows =
            new List<KeyValuePair<float, Vector3>>();

        /// <summary>
        /// Per-item-type nudges, keyed by the held entity's class name, as
        /// printed in anchor-status.log. Each item sits at its own distance
        /// from the anchor, so until the anchor is exactly right they need
        /// different corrections; when it is, they all want zero.
        /// </summary>
        private static readonly Dictionary<string, Vector3> Items =
            new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);

        private static Placement _mode = Placement.Hand;
        private static Vector3 _global;
        private static bool _handSpace = true;

        /// <summary>
        /// Whether first person keeps the skin an equipped glove covers. A
        /// fingerless glove needs it, because its openings are meant to show
        /// skin; a full glove does not, because the skin would fight it.
        /// </summary>
        private static bool _keepCoveredSkin;
        /// <summary>
        /// How the first-person hand is built.
        ///
        /// This is the one real difference between the two views: third person
        /// draws the hand the avatar has, first person builds a different hand
        /// to match it, because the hand has to be posed by the game's own
        /// first-person animation to hold an item. Everything that has looked
        /// wrong in first person and right in third comes from that rebuild.
        /// </summary>
        internal enum HandBuild
        {
            /// <summary>
            /// The game's hand, every vertex moved onto the nearest point of
            /// the avatar's surface. Follows the avatar's shape, but nearest
            /// point is not a continuous mapping, so neighbouring vertices can
            /// land on opposite sides of a finger and tear the mesh.
            /// </summary>
            Carrier,
            /// <summary>
            /// The game's hand, unmoved, wearing the avatar's textures.
            ///
            /// Takes the half of the carrier that works - deciding, per
            /// triangle, which of the avatar's materials covers it, and
            /// carrying that material's texture and coordinates across - and
            /// drops the half that does not, which is moving the vertices.
            /// A fingerless glove therefore comes out gloved across the palm
            /// and bare on the fingers, with the garment's own detail on it,
            /// and nothing can tear because nothing moves.
            /// </summary>
            Hybrid,
            /// <summary>
            /// The game's hand, unmoved, in one flat colour per material.
            /// Cannot tear either, but loses the texture with it, so the hand
            /// reads as untextured.
            /// </summary>
            Tinted,
            /// <summary>
            /// The avatar's own hand mesh. Correct shape and texture, but its
            /// fingers come apart in the item-holding pose - which is why the
            /// carrier exists. Kept for comparison.
            /// </summary>
            Mesh,
        }

        private static HandBuild _hands = HandBuild.Mesh;

        internal static HandBuild Hands
        {
            get
            {
                Refresh();
                return _hands;
            }
        }
        private static DateTime _stampUtc;
        private static DateTime _nextCheckUtc = DateTime.MinValue;
        private static bool _loaded;

        internal static Placement Mode
        {
            get
            {
                Refresh();
                return _mode;
            }
        }

        /// <summary>
        /// Whether a nudge is measured along the hand's own axes rather than
        /// the world's. In hand space one value stays correct at every view
        /// pitch; in avatar space the same value drifts as the arm swings,
        /// which is what "it changes when I look up" was.
        /// </summary>
        /// <summary>Whether to keep the skin a glove covers.</summary>
        internal static bool KeepCoveredSkin
        {
            get
            {
                Refresh();
                return _keepCoveredSkin;
            }
        }

        internal static bool NudgeInHandSpace
        {
            get
            {
                Refresh();
                return _handSpace;
            }
        }

        /// <summary>The nudge for one held item class, on top of the rest.</summary>
        internal static Vector3 OffsetForItem(string itemTypeName)
        {
            Refresh();
            Vector3 offset;
            return itemTypeName != null &&
                Items.TryGetValue(itemTypeName, out offset)
                ? offset
                : Vector3.Zero;
        }

        /// <summary>What the tuning file currently holds, for the status log.</summary>
        internal static string Describe()
        {
            Refresh();
            var text = new System.Text.StringBuilder();
            text.Append("mode=").Append(_mode.ToString().ToLowerInvariant());
            text.Append(" hands=").Append(_hands.ToString().ToLowerInvariant());
            text.Append(" space=").Append(_handSpace ? "hand" : "avatar");
            text.Append(" skin=").Append(_keepCoveredSkin ? "full" : "covered");
            text.Append(" offset=").Append(_global);
            foreach (KeyValuePair<string, Vector3> item in Items)
            {
                text.Append(" ").Append(item.Key).Append("=").Append(item.Value);
            }
            if (Rows.Count == 0)
            {
                text.Append(" (no per-build rows)");
                return text.ToString();
            }
            text.Append(" ").Append(Rows.Count).Append(" rows:");
            foreach (KeyValuePair<float, Vector3> row in Rows)
            {
                text.Append(" ").Append(row.Key.ToString("F2"))
                    .Append("=").Append(row.Value.Y.ToString("F3"));
            }
            return text.ToString();
        }

        /// <summary>
        /// The nudge in force for this build: the unconditional "offset" line
        /// plus whatever the per-build rows interpolate to.
        ///
        /// The two are kept separate because a row only ever applies to the
        /// build it is keyed to. A row written at a build no avatar has moves
        /// nothing, which is indistinguishable from the file being ignored, so
        /// there has to be a way to move every avatar at once.
        /// </summary>
        internal static Vector3 OffsetFor(float build)
        {
            Refresh();
            return _global + RowOffsetFor(build);
        }

        private static Vector3 RowOffsetFor(float build)
        {
            if (Rows.Count == 0)
            {
                return Vector3.Zero;
            }
            if (build <= Rows[0].Key)
            {
                return Rows[0].Value;
            }
            if (build >= Rows[Rows.Count - 1].Key)
            {
                return Rows[Rows.Count - 1].Value;
            }
            for (int index = 1; index < Rows.Count; index++)
            {
                if (build > Rows[index].Key)
                {
                    continue;
                }
                KeyValuePair<float, Vector3> low = Rows[index - 1];
                KeyValuePair<float, Vector3> high = Rows[index];
                float span = high.Key - low.Key;
                float t = span <= 1e-6f ? 0f : (build - low.Key) / span;
                return Vector3.Lerp(low.Value, high.Value, t);
            }
            return Vector3.Zero;
        }

        private static void Refresh()
        {
            DateTime now = DateTime.UtcNow;
            if (now < _nextCheckUtc)
            {
                return;
            }
            _nextCheckUtc = now.AddSeconds(1);

            try
            {
                string path = Path.Combine(
                    Branding.AvatarFolder(AppDomain.CurrentDomain.BaseDirectory),
                    FileName);

                if (!File.Exists(path))
                {
                    if (!_loaded)
                    {
                        WriteTemplate(path);
                        _loaded = true;
                    }
                    return;
                }

                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (_loaded && stamp == _stampUtc)
                {
                    return;
                }
                _stampUtc = stamp;
                _loaded = true;
                Parse(File.ReadAllLines(path));
            }
            catch
            {
                // Tuning is a convenience; never let it disturb rendering.
            }
        }

        private static void Parse(string[] lines)
        {
            Rows.Clear();
            Items.Clear();
            _mode = Placement.Hand;
            _global = Vector3.Zero;
            _handSpace = true;
            _keepCoveredSkin = false;
            _hands = HandBuild.Mesh;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                int comment = line.IndexOf('#');
                if (comment >= 0)
                {
                    line = line.Substring(0, comment).Trim();
                }
                if (line.Length == 0)
                {
                    continue;
                }

                string[] parts = line.Split(
                    new[] { ' ', '\t', ',' },
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    continue;
                }

                if (string.Equals(parts[0], "mode",
                        StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        _mode = (Placement)Enum.Parse(
                            typeof(Placement), parts[1], true);
                    }
                    catch
                    {
                        // An unrecognised name keeps the current mode rather
                        // than silently reverting placement mid-session.
                    }
                    continue;
                }

                if (string.Equals(parts[0], "offset",
                        StringComparison.OrdinalIgnoreCase))
                {
                    _global = ParseVector(parts);
                    continue;
                }

                if (string.Equals(parts[0], "hands",
                        StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        _hands = (HandBuild)Enum.Parse(
                            typeof(HandBuild), parts[1], true);
                    }
                    catch
                    {
                        // Unrecognised name keeps the current build.
                    }
                    continue;
                }

                if (string.Equals(parts[0], "skin",
                        StringComparison.OrdinalIgnoreCase))
                {
                    _keepCoveredSkin = string.Equals(parts[1], "full",
                        StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (string.Equals(parts[0], "space",
                        StringComparison.OrdinalIgnoreCase))
                {
                    _handSpace = !string.Equals(parts[1], "avatar",
                        StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (string.Equals(parts[0], "item",
                        StringComparison.OrdinalIgnoreCase) &&
                    parts.Length >= 3)
                {
                    // "item BlockEntity 0 -0.02 0": shift the columns along by
                    // one so the shared vector parser sees the same shape.
                    var tail = new string[parts.Length - 1];
                    Array.Copy(parts, 1, tail, 0, tail.Length);
                    Items[parts[1]] = ParseVector(tail);
                    continue;
                }

                float build, x = 0f, y = 0f, z = 0f;
                if (!TryParse(parts[0], out build)) { continue; }
                if (parts.Length >= 4)
                {
                    if (!TryParse(parts[1], out x)) { continue; }
                    if (!TryParse(parts[2], out y)) { continue; }
                    if (!TryParse(parts[3], out z)) { continue; }
                }
                else if (!TryParse(parts[1], out y))
                {
                    // Two columns means "build height", the common case.
                    continue;
                }

                Rows.Add(new KeyValuePair<float, Vector3>(
                    build, new Vector3(x, y, z)));
            }
            Rows.Sort(delegate(
                KeyValuePair<float, Vector3> a,
                KeyValuePair<float, Vector3> b)
            {
                return a.Key.CompareTo(b.Key);
            });
        }

        /// <summary>
        /// "offset 0.05" means Y only, "offset 0 0.05 0" means all three.
        /// </summary>
        private static Vector3 ParseVector(string[] parts)
        {
            float x, y, z;
            if (parts.Length >= 4 &&
                TryParse(parts[1], out x) &&
                TryParse(parts[2], out y) &&
                TryParse(parts[3], out z))
            {
                return new Vector3(x, y, z);
            }
            if (TryParse(parts[1], out y))
            {
                return new Vector3(0f, y, 0f);
            }
            return Vector3.Zero;
        }

        private static bool TryParse(string text, out float value)
        {
            return float.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
        }

        private static void WriteTemplate(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, new[]
            {
                "# Third-person held-item tuning.",
                "#",
                "# Only affects other players as YOU see them, and only in",
                "# third person. Saved changes apply within a second, no",
                "# restart. Everything in force is echoed to anchor-status.log",
                "# next to this file, so you can confirm a change was read.",
                "",
                "# 1. Which point the item is anchored to.",
                "#",
                "#      mode hand    the avatar's hand, rotation too  (default)",
                "#      mode grip    the avatar's fingers, position only",
                "#      mode prop    the avatar's attach bone, position only",
                "#      mode shift   stock anchor, nudged by the hand offset",
                "#      mode stock   untouched game placement, as a baseline",
                "#",
                "# 'hand' is the only one that also corrects the anchor's",
                "# ROTATION, and only that can seat every item at once. Each",
                "# item sits a different distance out from the anchor - a gun at",
                "# 0 cm, a pickaxe at 11, a block at 7 - so a wrong rotation",
                "# misplaces each of them by a different amount and swings them",
                "# all as the arm pitches. If one item is right and the others",
                "# are not, or things shift when you look up, that is rotation,",
                "# and no amount of nudging below will fix it.",
                "",
                "mode hand",
                "",
                "# 2. A nudge applied to every avatar, whatever its height.",
                "# +Y raises the item, -Y lowers it. Metres, so 0.01 is 1 cm.",
                "# Use a big value like 1.0 first if you just want to prove the",
                "# file is being read - the item should fly a metre upwards.",
                "#",
                "#     offset  Y            or      offset  X  Y  Z",
                "",
                "offset 0.0",
                "",
                "# Nudges are measured along the hand's own axes, so one value",
                "# stays right at every view pitch. 'space avatar' measures them",
                "# along the world's axes instead, which drifts as the arm",
                "# swings.",
                "#",
                "#     space hand           or      space avatar",
                "",
                "space hand",
                "",
                "# The first-person hand. This is the one real difference",
                "# between the two views: third person draws the hand your",
                "# avatar has, while first person throws it away and rebuilds",
                "# the game's own hand to match its surface. Anything that",
                "# looks wrong in first person but right in third comes from",
                "# that rebuild.",
                "#",
                "#     hands mesh      your own hand and glove  (default)",
                "#     hands hybrid    the game's hand, your textures",
                "#     hands tinted    the game's hand, one flat colour per material",
                "#     hands carrier   the game's hand pulled onto your surface",

                "#",
                "# 'hybrid' keeps the game's hand shape and dresses it in your",
                "# avatar's materials, deciding per triangle which one covers",
                "# it - so a fingerless glove comes out gloved across the palm",
                "# and bare on the fingers, with the garment's own texture on",
                "# it. Nothing moves, so it cannot tear.",
                "#",
                "# 'tinted' is the same without the textures, one flat colour",
                "# per material. 'carrier' pulls the shape onto your avatar's",
                "# as well, closer when it works and torn when it does not.",
                "# 'mesh' draws your own hand, whose fingers come apart in the",
                "# pose used to hold an item - which is why the others exist.",
                "",
                "hands mesh",
                "",
                "# 3. Per-item nudges, on top of the one above, for when one",
                "# item still sits differently from the rest. The name is the",
                "# class printed as child0= in anchor-status.log.",
                "#",
                "#     item  BlockEntity           0  -0.02  0",
                "#     item  CastleMinerToolModel  0  -0.01  0",
                "#     item  GunEntity             0   0     0",
                "",
                "# 4. Per-height nudges, on top of the rest. One row per build,",
                "# interpolated in between so the item does not jump.",
                "#",
                "#     build  offsetY       or      build  offsetX  offsetY  offsetZ",
                "#",
                "# Careful: a row only applies to the build it is keyed to, and",
                "# real avatars sit between about 0.80 and 1.20. A row at 10.0",
                "# will never do anything. Each player's build is printed in",
                "# anchor-status.log.",
                "",
                "0.80   0.000",
                "1.00   0.000",
                "1.20   0.000",
                "1.50   0.000",
                "2.00   0.000",
            });
        }
    }

    internal static class Branding
    {
#if XBOX_AVATAR_BRAND
        internal const string ProductName = "Xbox Avatar";
        private static readonly string[] AvatarSegments = { "Xbox Avatar" };
        private static readonly string[] BridgeSegments = { "Xbox Avatar", "Bridge" };
#else
        internal const string ProductName = "OpenClassic Xbox Avatar";
        private static readonly string[] AvatarSegments = { "OpenClassic Addons", "Xbox Avatar" };
        private static readonly string[] BridgeSegments = { "OpenClassic Addons", "Xbox Avatar Bridge" };
#endif

        /// <summary>Where the imported avatar, caches, backups and logs live.</summary>
        internal static string AvatarFolder(string gameFolder)
        {
            return Combine(gameFolder, AvatarSegments);
        }

        /// <summary>Where the native capture bridge lives.</summary>
        internal static string BridgeFolder(string gameFolder)
        {
            return Combine(gameFolder, BridgeSegments);
        }

        private static string Combine(string root, string[] segments)
        {
            string path = root;
            for (int index = 0; index < segments.Length; index++)
            {
                path = Path.Combine(path, segments[index]);
            }
            return path;
        }
    }

    public static class AvatarEntityFactory
    {
        // Returns the shared SkinnedModelEntity base rather than the concrete
        // stock entity, and that matters on 1.9.9. The patcher inserts a call to
        // this method into Player..ctor, which means the call's signature is
        // written into the game assembly's metadata. From 1.9.9 the concrete
        // type lives in the game assembly itself, and importing a signature that
        // names a type from the target module emits a member reference scoped to
        // the wrong assembly - the CLR then cannot resolve it and Player..ctor
        // fails to JIT the moment a player is built. SkinnedModelEntity lives in
        // DNA.Common on every client, so the reference stays resolvable.
        // It is also exactly what Avatar.set_ProxyModelEntity takes, so the call
        // site needs no cast.
        public static SkinnedModelEntity Create(Model fallbackModel, Avatar avatar, NetworkGamer gamer)
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
        private string _firstPersonBatches = string.Empty;

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
                    // Default for this pass. Each batch overrides it below: only
                    // face layers want clamping, and clothing needs wrapping
                    // to index its palette and decal atlases correctly.
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

                        // Only face layers want clamping. Each covers the whole
                        // head half while just one small feature is opaque, and
                        // the mask ships a transparent border, so vertices away
                        // from the feature carry UVs outside [0,1] and are meant
                        // to sample nothing; wrapping folds that art back onto
                        // the head and duplicates features on the far cheek.
                        // Clothing is the opposite: its palette and decal
                        // overlays index small atlases with deliberately
                        // out-of-range coordinates and need wrapping to reach
                        // the right entry, so clamping everything discoloured
                        // the outfit.
                        device.SamplerStates[0] = batch.FaceTextureUsage >= 0
                            ? SamplerState.LinearClamp
                            : SamplerState.LinearWrap;

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

        /// <summary>
        /// Correct the held item's anchor here, inside the avatar's own update.
        ///
        /// Avatar.UpdateParts rewrites that anchor from the stock rig at the
        /// top of every avatar update, and the held item snapshots its own
        /// world matrix into ModelEntity._worldBoneTransforms further down the
        /// same child walk - Draw only replays that snapshot. So a correction
        /// made after the walk, for instance at the end of the game update, is
        /// written into a matrix nothing reads and is overwritten before the
        /// next walk begins. It never reaches the screen whatever value it
        /// holds, which is why every earlier attempt to move the item, and
        /// every tuning offset however large, appeared to do nothing at all.
        ///
        /// This model entity is a child of the avatar and is ordered ahead of
        /// the prop part, so running here lands between the two.
        /// </summary>
        protected override void OnUpdate(GameTime gameTime)
        {
            base.OnUpdate(gameTime);
            try
            {
                AvatarNetworkBridge.ApplyItemAnchor(_avatar, this);
            }
            catch (Exception exception)
            {
                WriteFailure(exception);
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

        /// <summary>
        /// The editor's cumulative build scale at the right prop bone: 1.0 for a
        /// default avatar, larger for a taller one.
        ///
        /// The game has no height knowledge at all — Avatar.AvatarHeight is a
        /// hard-coded 1.6 and its bind pose is the stock rig — so anything that
        /// has to follow the imported body's proportions must take them from
        /// here. Already computed at load time for the first-person hands.
        /// </summary>
        internal float AvatarShapeScale
        {
            get
            {
                if (_asset == null ||
                    _asset.FirstPersonBoneScale == null ||
                    _asset.FirstPersonBoneScale.Length <= (int)AvatarBone.PropRight)
                {
                    return 1f;
                }
                Vector3 scale = _asset.FirstPersonBoneScale[(int)AvatarBone.PropRight];
                float average = (scale.X + scale.Y + scale.Z) / 3f;
                return average > 0.01f && !float.IsNaN(average) ? average : 1f;
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
                    thumb,
                    AvatarShapeScale);
                return IsFinite(translation);
            }
            catch (Exception exception)
            {
                WriteFailure(exception);
                return false;
            }
        }

        /// <summary>
        /// A bone of the imported skeleton as a full transform in avatar space:
        /// its rotation as well as its position.
        ///
        /// Every held item is offset and rotated from the anchor along the
        /// anchor's own axes, so the anchor's rotation matters as much as its
        /// position - a rotation taken from the stock rig turns into a
        /// position error proportional to each item's own offset. That is why
        /// a gun, which sits exactly at the anchor, looked right while a
        /// pickaxe 11 cm out and a block 7 cm out each needed a different
        /// correction, and why all of them shifted when the arm pitched.
        ///
        /// The imported mesh is drawn through RenderWorld, which is avatar
        /// space with Z negated. Conjugating by that reflection rather than
        /// merely applying it keeps the result a true rotation instead of a
        /// mirror, which would turn the item model inside out.
        /// </summary>
        internal bool TryGetAvatarSpaceBoneTransform(
            AvatarBone bone,
            out Matrix transform)
        {
            transform = Matrix.Identity;
            if (_avatar == null || _exportPoseBones == null ||
                (int)bone >= _exportPoseBones.Length)
            {
                return false;
            }
            try
            {
                BuildExportSpacePose();
                Matrix reflectionZ = Matrix.CreateScale(1f, 1f, -1f);
                transform =
                    reflectionZ * _exportPoseBones[(int)bone] * reflectionZ;
                return IsFinite(transform.Translation) &&
                    IsFinite(transform.Forward) &&
                    IsFinite(transform.Up);
            }
            catch
            {
                return false;
            }
        }

        private Vector3 ExportBoneTranslation(AvatarBone bone)
        {
            Vector3 result = _exportPoseBones[(int)bone].Translation;
            result.Z = -result.Z;
            return result;
        }

        /// <summary>
        /// A bone of the imported skeleton in the same space the item anchor
        /// uses, so the two can be compared directly. Exposed for diagnostics:
        /// the item lands exactly where it is aimed, so when it still looks
        /// wrong the question is which bone the visible hand actually follows.
        /// </summary>
        internal bool TryGetAvatarSpaceBone(AvatarBone bone, out Vector3 position)
        {
            position = Vector3.Zero;
            if (_avatar == null || _exportPoseBones == null ||
                (int)bone >= _exportPoseBones.Length)
            {
                return false;
            }
            try
            {
                BuildExportSpacePose();
                position = ExportBoneTranslation(bone);
                return IsFinite(position);
            }
            catch
            {
                return false;
            }
        }

        internal static Vector3 ComputeThirdPersonGripTranslation(
            Vector3 importedProp,
            Vector3 fingerIndex,
            Vector3 fingerMiddle,
            Vector3 fingerRing,
            Vector3 fingerSmall,
            Vector3 fingerThumb)
        {
            return ComputeThirdPersonGripTranslation(
                importedProp,
                fingerIndex,
                fingerMiddle,
                fingerRing,
                fingerSmall,
                fingerThumb,
                1f);
        }

        internal static Vector3 ComputeThirdPersonGripTranslation(
            Vector3 importedProp,
            Vector3 fingerIndex,
            Vector3 fingerMiddle,
            Vector3 fingerRing,
            Vector3 fingerSmall,
            Vector3 fingerThumb,
            float shapeScale)
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
            //
            // The correction is proportional to the body, so the bound has to be
            // too. As a fixed 0.22 m it started truncating real avatars from a
            // build of about 1.61 upwards, which pulled the item back towards
            // the prop bone exactly on the tall avatars this is meant to serve.
            float maximumCorrection = 0.22f * shapeScale;
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
            // Skin whatever this hand build is going to draw, which is not
            // always what the carrier build draws.
            //
            // A glove's mapped indices are deliberately empty, because the
            // carrier stands in for it. Gating the skinning on those indices
            // therefore left the glove's vertices in third-person space while
            // "hands mesh" went ahead and drew 700 triangles of them, which
            // land nowhere near the camera. That is the missing glove: the
            // geometry was selected, textured and drawn, and simply never
            // moved into first person.
            ItemTuning.HandBuild hands = ItemTuning.Hands;
            bool meshHands = hands == ItemTuning.HandBuild.Mesh;
            foreach (AvatarBatch batch in _asset.Batches)
            {
                short[] drawn = !meshHands
                    ? batch.MappedFirstPersonIndices
                    : (ItemTuning.KeepCoveredSkin
                        ? batch.MappedFirstPersonSkinIndices
                        : batch.MappedFirstPersonHandIndices);
                if (drawn != null && drawn.Length >= 3)
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
                // The first-person path draws hands and the carrier only, never a
                // face layer, and the glove atlas relies on wrapping for its
                // out-of-range island. Clamping here discoloured the hands.
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
                // "mesh" draws the avatar's own hand, the same geometry third
                // person uses; "carrier" rebuilds ProxyBoy's hand against it.
                foreach (AvatarBatch batch in _asset.Batches)
                {
                    short[] indices = !meshHands
                        ? batch.MappedFirstPersonIndices
                        : (ItemTuning.KeepCoveredSkin
                            ? batch.MappedFirstPersonSkinIndices
                            : batch.MappedFirstPersonHandIndices);
                    if (indices == null || indices.Length < 3)
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
                    if (!_firstPersonLogged)
                    {
                        // Name every batch first person actually puts on
                        // screen. The hand is only one of them, and a stray
                        // slab of garment near the hand looks exactly like a
                        // broken hand until the list says otherwise.
                        _firstPersonBatches +=
                            "fpBatch=" + batch.Name +
                            " triangles=" + (indices.Length / 3) +
                            Environment.NewLine;
                    }
                }

                foreach (ProxyHandCarrierPart carrierPart in
                    meshHands
                        ? new ProxyHandCarrierPart[0]
                        : _firstPersonCarrier.Parts)
                {
                    // The same triangles once per layer, as third person draws
                    // them: the base surface and then each overlay pass that
                    // tints it. Drawing only the first layer left the hand on
                    // the untinted base texture.
                    foreach (AvatarBatch carrierMaterial in carrierPart.Layers)
                    {
                        if (carrierMaterial == null)
                        {
                            continue;
                        }
                        if (hands == ItemTuning.HandBuild.Tinted)
                        {
                            // The game's hand shape carries the game's own UVs,
                            // which mean nothing in the avatar's atlas, so the
                            // avatar's contribution here is its colour rather
                            // than its texture. One flat pass, and the overlay
                            // layers would only repaint the same thing.
                            if (carrierMaterial != carrierPart.Material)
                            {
                                continue;
                            }
                            _effect.VertexColorEnabled = false;
                            _effect.TextureEnabled = false;
                            _effect.DiffuseColor =
                                carrierMaterial.AverageColor();
                        }
                        else
                        {
                            _effect.VertexColorEnabled = carrierPart.UseVertexColor;
                            _effect.DiffuseColor = carrierMaterial.DiffuseColor;
                            _effect.TextureEnabled =
                                carrierPart.UseTexture &&
                                carrierMaterial.Texture != null;
                            _effect.Texture = carrierMaterial.Texture;
                        }
                        // Same addressing rule third person uses, rather than
                        // inheriting whatever the last batch happened to set.
                        device.SamplerStates[0] =
                            carrierMaterial.FaceTextureUsage >= 0
                                ? SamplerState.LinearClamp
                                : SamplerState.LinearWrap;
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
                }

                // Report the pass the player actually sees.
                //
                // The hand is drawn twice a frame: once in the world view,
                // where the local player's own hands sit at or behind the
                // camera and contribute nothing, and once through the
                // viewmodel camera the game keeps for first person. Reporting
                // the first draw meant reporting the world pass, whose numbers
                // are meaningless here - not one vertex inside the frustum,
                // and an ndc line running to five figures. Every conclusion
                // drawn about where this hand ends up came from that pass.
                if (!_firstPersonLogged &&
                    AnyVertexOnScreen(view * projection, meshHands))
                {
                    WriteMappedFirstPersonStatus(
                        renderedBatches,
                        renderedTriangles,
                        view,
                        projection);
                    DumpFirstPersonMesh(meshHands, hands, view, projection);
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

        /// <summary>
        /// Write the first-person hand exactly as it was drawn - posed, in
        /// world space - as a Wavefront OBJ.
        ///
        /// The offline probe can rasterise the avatar, but only in bind pose,
        /// so it can prove which triangles are selected and nothing about how
        /// they are placed. Everything still wrong with the hand is in the
        /// placement. This dumps the vertices the GPU actually received, so
        /// the drawn result can be measured and rendered away from the game
        /// instead of being judged from a screenshot of a hand a hundred
        /// pixels across.
        /// </summary>
        /// <summary>
        /// Whether any of the geometry this pass draws lands inside the
        /// viewing frustum. Tells the viewmodel pass apart from the world
        /// pass, which draws the same hand from a camera it sits behind.
        /// </summary>
        private bool AnyVertexOnScreen(Matrix camera, bool meshHands)
        {
            foreach (AvatarBatch batch in _asset.Batches)
            {
                short[] indices = meshHands
                    ? batch.MappedFirstPersonHandIndices
                    : batch.MappedFirstPersonIndices;
                if (indices == null || indices.Length < 3)
                {
                    continue;
                }
                foreach (short index in indices)
                {
                    Vector4 clip = Vector4.Transform(
                        new Vector4(
                            batch.DrawVertices[(ushort)index].Position, 1f),
                        camera);
                    if (clip.W > 0.0001f &&
                        Math.Abs(clip.X) <= clip.W &&
                        Math.Abs(clip.Y) <= clip.W &&
                        clip.Z >= 0f && clip.Z <= clip.W)
                    {
                        return true;
                    }
                }
            }
            if (meshHands || _firstPersonCarrier == null)
            {
                return false;
            }
            foreach (ProxyHandCarrierPart part in _firstPersonCarrier.Parts)
            {
                foreach (AvatarDrawVertex vertex in part.DrawVertices)
                {
                    Vector4 clip = Vector4.Transform(
                        new Vector4(vertex.Position, 1f), camera);
                    if (clip.W > 0.0001f &&
                        Math.Abs(clip.X) <= clip.W &&
                        Math.Abs(clip.Y) <= clip.W &&
                        clip.Z >= 0f && clip.Z <= clip.W)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private void DumpFirstPersonMesh(
            bool meshHands,
            ItemTuning.HandBuild build,
            Matrix view,
            Matrix projection)
        {
            try
            {
                string folder = Branding.AvatarFolder(
                    AppDomain.CurrentDomain.BaseDirectory);
                Directory.CreateDirectory(folder);

                // Twice: once in world space, and once through the player's own
                // camera. The camera copy makes the offline render the same
                // picture the player is looking at, so a fault reported from a
                // screenshot can be found in a file rather than guessed at from
                // a hand a hundred pixels across.
                Matrix camera = view * projection;
                WriteFirstPersonObj(
                    Path.Combine(folder, "first-person-mesh.obj"),
                    build, meshHands, null);
                WriteFirstPersonObj(
                    Path.Combine(folder, "first-person-view.obj"),
                    build, meshHands, camera);
            }
            catch (Exception exception)
            {
                WriteFailure(exception);
            }
        }

        private void WriteFirstPersonObj(
            string path,
            ItemTuning.HandBuild build,
            bool meshHands,
            Matrix? camera)
        {
            var text = new System.Text.StringBuilder();
            text.Append("# first-person hand as drawn, build=")
                .Append(build.ToString().ToLowerInvariant())
                .Append(camera.HasValue ? ", camera space" : ", world space")
                .Append(Environment.NewLine);
            int written = 0;

            foreach (AvatarBatch batch in _asset.Batches)
            {
                short[] indices = meshHands
                    ? batch.MappedFirstPersonHandIndices
                    : batch.MappedFirstPersonIndices;
                if (indices == null || indices.Length < 3)
                {
                    continue;
                }
                written += AppendObjGroup(
                    text, batch.Name, batch.DrawVertices, indices,
                    written, camera);
            }

            if (!meshHands)
            {
                int part = 0;
                foreach (ProxyHandCarrierPart carrierPart in
                    _firstPersonCarrier.Parts)
                {
                    written += AppendObjGroup(
                        text,
                        "carrier" + part + "-" + carrierPart.Material.Name,
                        carrierPart.DrawVertices,
                        carrierPart.Indices,
                        written,
                        camera);
                    part++;
                }
            }
            File.WriteAllText(path, text.ToString());
        }

        /// <summary>
        /// One OBJ group. Returns how many vertices it wrote, because OBJ face
        /// indices are one-based and run across the whole file.
        /// </summary>
        private static int AppendObjGroup(
            System.Text.StringBuilder text,
            string name,
            AvatarDrawVertex[] vertices,
            short[] indices,
            int alreadyWritten,
            Matrix? camera)
        {
            text.Append("g ").Append(name).Append(Environment.NewLine);
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            foreach (AvatarDrawVertex vertex in vertices)
            {
                Vector3 position = vertex.Position;
                if (camera.HasValue)
                {
                    // Perspective divide, so the dump is what reached the
                    // screen rather than where it was in the world. Y is
                    // negated because screen space counts downwards.
                    Vector4 clip = Vector4.Transform(
                        new Vector4(position, 1f), camera.Value);
                    float w = Math.Abs(clip.W) < 1e-6f ? 1e-6f : clip.W;
                    position = new Vector3(
                        clip.X / w, -clip.Y / w, clip.Z / w);
                }
                text.Append("v ")
                    .Append(position.X.ToString("R", culture)).Append(' ')
                    .Append(position.Y.ToString("R", culture)).Append(' ')
                    .Append(position.Z.ToString("R", culture))
                    .Append(Environment.NewLine);
            }
            for (int triangle = 0; triangle + 2 < indices.Length; triangle += 3)
            {
                text.Append("f ")
                    .Append(alreadyWritten + (ushort)indices[triangle] + 1).Append(' ')
                    .Append(alreadyWritten + (ushort)indices[triangle + 1] + 1).Append(' ')
                    .Append(alreadyWritten + (ushort)indices[triangle + 2] + 1)
                    .Append(Environment.NewLine);
            }
            return vertices.Length;
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
                string folder = Branding.AvatarFolder(AppDomain.CurrentDomain.BaseDirectory);
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
                        _firstPersonBatches +
                        "carrierUnmorphed=" + _firstPersonCarrier.UnmorphedVertices +
                        "/" + _firstPersonCarrier.TotalVertices +
                        " carrierReverted=" + _firstPersonCarrier.RevertedVertices +
                        " largestShapeScale=" + _firstPersonCarrier.LargestShapeScale.ToString("F3") +
                        Environment.NewLine +
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

                string folder = Branding.AvatarFolder(AppDomain.CurrentDomain.BaseDirectory);
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

        /// <summary>
        /// Give back the textures and effect this model owns.
        ///
        /// Every avatar applied builds a fresh asset with its own textures -
        /// there is no shared cache, so nothing else can be holding these - and
        /// XNA keeps a device-side reference to each one, so dropping the
        /// managed object does not reclaim any of it. A player rejoining or
        /// changing avatar therefore leaked a whole texture set each time, and
        /// the game is a 32-bit process: over a long session that is what runs
        /// it out of address space and kills it with no exception to catch.
        ///
        /// Only ever called on a model already detached from the avatar, so no
        /// draw can be in flight against it.
        /// </summary>
        internal void ReleaseGraphicsResources()
        {
            try
            {
                if (_effect != null)
                {
                    _effect.Dispose();
                    _effect = null;
                }
                if (_asset == null || _asset.Batches == null)
                {
                    return;
                }
                foreach (AvatarBatch batch in _asset.Batches)
                {
                    if (batch != null && batch.Texture != null)
                    {
                        batch.Texture.Dispose();
                        batch.Texture = null;
                    }
                }
            }
            catch (Exception exception)
            {
                // Never let tidying up take the game down with it.
                WriteFailure(exception);
            }
        }

        private void EnsureGraphicsResources(GraphicsDevice device)
        {
            if (_effect == null)
            {
                _effect = new BasicEffect(device);
            }
            foreach (AvatarBatch batch in _asset.Batches)
            {
                if (batch.Texture != null ||
                    batch.TextureUnavailable ||
                    batch.TexturePng == null ||
                    batch.TexturePng.Length == 0)
                {
                    continue;
                }
                try
                {
                    using (var stream = new MemoryStream(batch.TexturePng, false))
                    {
                        batch.Texture = Texture2D.FromStream(device, stream);
                    }
                }
                catch (Exception exception)
                {
                    // One texture that will not decode must not cost the whole
                    // avatar.
                    //
                    // This ran inside the draw's own try, so a single failure
                    // set _failed and dropped the player to the stock model for
                    // the rest of the session - the avatar disappearing
                    // entirely because one image out of dozens would not load.
                    // Give up on this batch alone, and remember that, so the
                    // next frame does not try again and log the same failure
                    // sixty times a second.
                    batch.TextureUnavailable = true;
                    WriteFailure(new InvalidOperationException(
                        "Texture failed to load for batch " + batch.Name,
                        exception));
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
                string folder = Branding.AvatarFolder(AppDomain.CurrentDomain.BaseDirectory);
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

        /// <summary>
        /// How many times the typical displacement a vertex may move before
        /// its projection is treated as a mistake rather than a fit.
        /// </summary>
        private const float OutlierDisplacementFactor = 4f;
        private const float CarrierModelScale = 100f;

        private readonly Matrix[] _inverseBindPose;
        private readonly int[] _avatarBoneByProxy;

        internal ProxyHandCarrierPart[] Parts;

        /// <summary>
        /// How many carrier vertices could not be projected onto the avatar's
        /// surface, out of how many were tried. A high proportion means the
        /// hand on screen is largely the game's own shape wearing the avatar's
        /// texture, which is what a torn-looking glove actually is.
        /// </summary>
        internal int UnmorphedVertices;
        internal int TotalVertices;

        /// <summary>
        /// How many projections were rejected as outliers and put back on the
        /// game's own hand. A large number means the avatar's hand is far
        /// enough from ProxyBoy's that most of the projection is guesswork.
        /// </summary>
        internal int RevertedVertices;

        /// <summary>Largest per-bone shape scale the skinning applies.</summary>
        internal float LargestShapeScale;

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

            int unmorphedVertices = 0;
            int totalVertices = 0;
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
                //
                // All three vertices have to agree. Accepting a majority and
                // letting the odd vertex follow the triangle looked like a way
                // to close the notch at the base of the thumb, but a vertex
                // with no hand of its own is usually forearm: morphing it onto
                // the hand surface tore a gloved hand apart. The notch is the
                // lesser problem, and this is not the place to fix it.
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
                    builder = new CarrierPartBuilder(
                        material,
                        asset.MaterialLayersFor(material),
                        outer);
                    builders.Add(material, builder);
                }
                AddCarrierTriangle(
                    builder,
                    allVertices[index0],
                    allVertices[index1],
                    allVertices[index2],
                    side,
                    surface,
                    avatarBoneByProxy,
                    ref unmorphedVertices,
                    ref totalVertices);
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
            var carrier = new ProxyHandCarrier(
                parts.ToArray(),
                skinning.InverseBindPose,
                avatarBoneByProxy);
            carrier.UnmorphedVertices = unmorphedVertices;
            carrier.TotalVertices = totalVertices;
            carrier.RevertedVertices = RevertProjectionOutliers(carrier.Parts);
            return carrier;
        }

        /// <summary>
        /// Undo the projections that disagree with everything around them.
        ///
        /// Nearest point is not a continuous mapping. Two vertices that are
        /// neighbours on the game's hand can land on opposite sides of a
        /// finger, and the triangle between them is then stretched across the
        /// gap - the spikes and flat wedges that shoot out of a gloved hand.
        /// The projection is only trustworthy where it agrees with its
        /// surroundings, so measure how far each vertex moved, and put back
        /// any that moved far more than the rest did. A vertex returned to the
        /// game's own hand is no worse placed than its neighbours; a vertex
        /// left on the wrong finger drags a triangle across the whole hand.
        ///
        /// Uses the median rather than the mean: a handful of very large
        /// displacements is exactly the case being detected, and they would
        /// drag a mean up far enough to hide themselves.
        /// </summary>
        private static int RevertProjectionOutliers(ProxyHandCarrierPart[] parts)
        {
            var displacements = new List<float>();
            foreach (ProxyHandCarrierPart part in parts)
            {
                foreach (CarrierSourceVertex vertex in part.SourceVertices)
                {
                    displacements.Add(Vector3.Distance(
                        vertex.Position,
                        vertex.StockPosition));
                }
            }
            if (displacements.Count == 0)
            {
                return 0;
            }
            displacements.Sort();
            float median = displacements[displacements.Count / 2];

            // A vertex is an outlier when it moved several times as far as a
            // typical one. The floor keeps the rule quiet on a hand that
            // matches closely, where the median is near zero and any small
            // variation would otherwise read as a multiple of it.
            float limit = Math.Max(median * OutlierDisplacementFactor,
                MaximumSurfaceProjection * 0.25f);

            int reverted = 0;
            foreach (ProxyHandCarrierPart part in parts)
            {
                for (int index = 0; index < part.SourceVertices.Length; index++)
                {
                    CarrierSourceVertex vertex = part.SourceVertices[index];
                    if (Vector3.Distance(vertex.Position, vertex.StockPosition)
                        <= limit)
                    {
                        continue;
                    }
                    // Keep the texture coordinate: it came from the avatar and
                    // is what makes the hand look like this avatar's. Only the
                    // position was untrustworthy.
                    vertex.Position = vertex.StockPosition;
                    vertex.Normal = vertex.StockNormal;
                    part.SourceVertices[index] = vertex;
                    reverted++;
                }
            }
            return reverted;
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
                float largest = Math.Max(shape.X, Math.Max(shape.Y, shape.Z));
                if (largest > LargestShapeScale)
                {
                    LargestShapeScale = largest;
                }
                transforms[proxyBone] =
                    _inverseBindPose[proxyBone] *
                    Matrix.CreateScale(shape) *
                    worldBoneTransforms[proxyBone];
            }

            // Both of these skin the game's own untouched hand, so no
            // projection can tear it; they differ only in how much of the
            // avatar's material they then carry across.
            ItemTuning.HandBuild build = ItemTuning.Hands;
            bool stockShape =
                build == ItemTuning.HandBuild.Tinted ||
                build == ItemTuning.HandBuild.Hybrid;

            foreach (ProxyHandCarrierPart part in Parts)
            {
                for (int index = 0; index < part.SourceVertices.Length; index++)
                {
                    CarrierSourceVertex source = part.SourceVertices[index];
                    if (stockShape)
                    {
                        source.Position = source.StockPosition;
                        source.Normal = source.StockNormal;
                    }
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

            // No glove triangle fits, so this part of the hand is not gloved.
            //
            // Forcing it onto the garment anyway was a mistake: gloves are
            // frequently fingerless, and the triangles that fail to find glove
            // to sit on are exactly the bare fingers sticking out of one. On
            // such a hand 60 of 789 triangles belong to the body, and painting
            // them with the glove is what turns bare fingers into glove-
            // coloured ones. The split between the two materials is the
            // fingerless glove.
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
            int[] avatarBoneByProxy,
            ref int unmorphed,
            ref int total)
        {
            CarrierSourceVertex[] vertices =
                { vertex0, vertex1, vertex2 };
            var moved = new bool[vertices.Length];
            Vector3 shift = Vector3.Zero;
            int shifted = 0;
            for (int index = 0; index < vertices.Length; index++)
            {
                vertices[index].StockPosition = vertices[index].Position;
                vertices[index].StockNormal = vertices[index].Normal;
                moved[index] = MorphCarrierVertex(
                    ref vertices[index],
                    side,
                    surface,
                    avatarBoneByProxy);
                if (moved[index])
                {
                    shift += vertices[index].Position -
                        vertices[index].StockPosition;
                    shifted++;
                }
                else
                {
                    unmorphed++;
                }
                total++;
            }

            // Carry a corner that could not be projected along with the ones
            // that could.
            //
            // A vertex left exactly where the game's hand put it, between two
            // that moved onto the avatar's, is a crease - and with 108 of 2367
            // vertices in that state there are creases all over the hand. None
            // is far enough to look like a spike by itself, which is why no
            // measure of displacement finds them: the surface is simply being
            // pulled in two directions at once. Moving a stranded corner by as
            // much as its neighbours moved keeps the triangle flat. It is not
            // where the avatar's surface is, but it is continuous with the
            // vertices that are, and continuity is the thing that was lost.
            if (shifted > 0 && shifted < vertices.Length)
            {
                shift /= shifted;
                for (int index = 0; index < vertices.Length; index++)
                {
                    if (!moved[index])
                    {
                        vertices[index].Position += shift;
                    }
                }
            }

            UnwrapTriangleTextureCoordinates(vertices);

            for (int index = 0; index < vertices.Length; index++)
            {
                builder.Add(vertices[index]);
            }
        }

        /// <summary>
        /// Bring a triangle's three texture coordinates into the same tile.
        ///
        /// Xbox garment atlases address their islands with deliberately
        /// out-of-range coordinates - this glove spans u=[-3.3,0.96] - and rely
        /// on wrapping to reach them. The mesh's own triangles are authored so
        /// that no triangle straddles the jump. The carrier is not that mesh:
        /// it re-triangulates the surface with ProxyBoy's topology, so its
        /// triangles do straddle, and one corner at u=-3.3 beside another at
        /// u=0.9 sweeps four copies of the atlas across a single triangle.
        /// That is the smeared, torn glove, and it happens in first person only
        /// because third person draws the original triangles.
        ///
        /// Shifting a corner by a whole tile samples the identical texel under
        /// wrapping, so this changes nothing that was already consistent and
        /// removes the sweep from everything that was not.
        /// </summary>
        private static void UnwrapTriangleTextureCoordinates(
            CarrierSourceVertex[] vertices)
        {
            Vector2 reference = vertices[0].TextureCoordinate;
            for (int index = 1; index < vertices.Length; index++)
            {
                Vector2 coordinate = vertices[index].TextureCoordinate;
                coordinate.X -= (float)Math.Round(coordinate.X - reference.X);
                coordinate.Y -= (float)Math.Round(coordinate.Y - reference.Y);
                vertices[index].TextureCoordinate = coordinate;
            }
        }

        /// <summary>
        /// Move one carrier vertex onto the avatar's surface. Returns whether
        /// it landed; a vertex that did not is reported so a hand that is
        /// mostly unprojected can be recognised rather than guessed at.
        /// </summary>
        private static bool MorphCarrierVertex(
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
            if (!found)
            {
                return false;
            }

            // Too far to move the vertex onto, but its texture coordinate must
            // still come from the avatar.
            //
            // Leaving the vertex entirely alone kept ProxyBoy's own UV, which
            // then addressed the avatar's atlas - a coordinate meaning one
            // thing in the game's hand texture and something unrelated in a
            // glove's. Neighbouring vertices that did project sample correctly,
            // so a single surface ends up half right and half arbitrary, which
            // is the torn, patchy look a gloved hand has in first person and
            // never in third.
            if (point.Distance > MaximumSurfaceProjection)
            {
                vertex.TextureCoordinate = point.TextureCoordinate;
                vertex.Color = point.Color;
                return false;
            }

            vertex.Position = ToProxyPosition(point.Position);
            vertex.Normal = ToProxyDirection(point.Normal);
            vertex.TextureCoordinate = point.TextureCoordinate;
            vertex.Color = point.Color;
            return true;
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
            private readonly AvatarBatch[] _layers;
            private readonly bool _outer;

            internal CarrierPartBuilder(
                AvatarBatch material,
                AvatarBatch[] layers,
                bool outer)
            {
                _material = material;
                _layers = layers;
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
                // Draw the hand the way third person draws the same surface:
                // its texture and its vertex colours, not a flat diffuse.
                //
                // Every batch in an Xbox avatar has a white diffuse - the skin
                // tone lives in the texture and the vertex colours - so a bare
                // hand, which is not an outfit glove and so had both switched
                // off, could only ever come out white. An avatar whose outfit
                // includes gloves was unaffected, which is why this went
                // unnoticed until a bare-handed outfit turned up.
                //
                // A bare-hand shell is still the exception the third-person
                // path makes it: its vertex colours are greyscale shader masks
                // rather than skin, so they stay suppressed.
                bool maskColours = _material.IsBareHandShell;
                return new ProxyHandCarrierPart(
                    _sourceVertices.ToArray(),
                    _drawVertices.ToArray(),
                    _indices.ToArray(),
                    _material,
                    _layers,
                    _material.TexturePng != null &&
                        _material.TexturePng.Length > 0,
                    !maskColours);
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

            /// <summary>
            /// Where this vertex sat on the game's own hand before being
            /// projected onto the avatar, so a projection that proves to be an
            /// outlier can be undone and the untouched hand can be drawn.
            /// </summary>
            internal Vector3 StockPosition;
            internal Vector3 StockNormal;
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
        internal AvatarBatch[] Layers;
        internal bool UseTexture;
        internal bool UseVertexColor;

        internal ProxyHandCarrierPart(
            ProxyHandCarrier.CarrierSourceVertex[] sourceVertices,
            AvatarDrawVertex[] drawVertices,
            short[] indices,
            AvatarBatch material,
            AvatarBatch[] layers,
            bool useTexture,
            bool useVertexColor)
        {
            SourceVertices = sourceVertices;
            DrawVertices = drawVertices;
            Indices = indices;
            Material = material;
            Layers = layers != null && layers.Length > 0
                ? layers
                : new[] { material };
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

        /// <summary>Mirrors a batch's texture coordinates in V, in place.</summary>
        private static void FlipBatchV(AvatarBatch batch)
        {
            if (batch == null || batch.DrawVertices == null)
            {
                return;
            }
            for (int index = 0; index < batch.DrawVertices.Length; index++)
            {
                Vector2 uv = batch.DrawVertices[index].TextureCoordinate;
                batch.DrawVertices[index].TextureCoordinate =
                    new Vector2(uv.X, 1f - uv.Y);
            }
        }

        /// <summary>
        /// The body layer that carries the skin.
        ///
        /// A body can export several ":body:" layers - the solid skin plus
        /// overlays painted on top of it. Every one of them satisfies the
        /// base-body test, and taking whichever came last meant an outfit whose
        /// body ends in "…:body:0:material-overlay-decal" handed the first
        /// person its white decal material: the hands rendered white and the
        /// glove, which draws as part of the same carrier, disappeared with
        /// them. Third person was unaffected because it uses each batch's own
        /// material and only borrows this one for bare-hand shells.
        ///
        /// Prefer the first layer that is not an overlay, and fall back to the
        /// first of any kind so a body made only of overlays still resolves.
        /// </summary>
        private static AvatarBatch ChooseBaseBodyBatch(AvatarBatch[] batches)
        {
            AvatarBatch fallback = null;
            foreach (AvatarBatch batch in batches)
            {
                if (batch == null || !batch.IsBaseBody)
                {
                    continue;
                }
                if (fallback == null)
                {
                    fallback = batch;
                }
                if (!batch.IsOverlayLayer)
                {
                    return batch;
                }
            }
            return fallback;
        }

        /// <summary>
        /// What geometry each batch offers first person, written when the
        /// avatar loads.
        ///
        /// The draw-time report only appears if the draw reaches the end of
        /// its path, so it says nothing about a batch that never draws - which
        /// is precisely the case worth explaining. This one is written from
        /// the loader and cannot be silenced by whatever happens later.
        /// </summary>
        /// <summary>
        /// Drop from an arm selection every triangle the hand selection also
        /// draws, so the two partition the limb instead of overlapping.
        ///
        /// The arm set stops short of the carrier's reach, 11 cm from the
        /// wrist, while the hand volume extends to 22.5 cm. Everything in that
        /// ring was in both sets, and the two are posed differently - the arm
        /// by ordinary skinning, the hand by the first-person hand placement -
        /// so each of those triangles was drawn twice, in two places. Two
        /// copies of the same surface pulled apart is what put the flat wedges
        /// down either side of the hand.
        /// </summary>
        private static short[] WithoutHandTriangles(short[] arm, byte[] sides)
        {
            if (arm == null || sides == null)
            {
                return arm ?? new short[0];
            }
            var result = new List<short>(arm.Length);
            for (int triangle = 0; triangle + 2 < arm.Length; triangle += 3)
            {
                ushort index0 = (ushort)arm[triangle];
                ushort index1 = (ushort)arm[triangle + 1];
                ushort index2 = (ushort)arm[triangle + 2];
                if (index0 >= sides.Length ||
                    index1 >= sides.Length ||
                    index2 >= sides.Length)
                {
                    continue;
                }
                // All three corners, matching what the hand set takes.
                //
                // Dropping a triangle with only one corner in the hand volume
                // left it in neither set: the arm had discarded it and the
                // hand set requires all three. That is a ring of missing
                // triangles right where the hand meets the arm, and two arcs
                // of that ring face the camera - the cutouts in the side of
                // the glove. Only an exact duplicate is worth removing.
                if (sides[index0] != 0 &&
                    sides[index1] != 0 &&
                    sides[index2] != 0)
                {
                    continue;
                }
                result.Add(arm[triangle]);
                result.Add(arm[triangle + 1]);
                result.Add(arm[triangle + 2]);
            }
            return result.ToArray();
        }

        /// <summary>
        /// Drop the bare-skin triangles an equipped glove is sitting on.
        ///
        /// Third person already does this; first person drew its own selection
        /// and kept them, so the skin under a glove was rendered as well as
        /// the glove itself, in the same place. Which of the two won came down
        /// to the depth buffer, and where the skin won the glove appeared to
        /// have holes in it - around the thumb and below the little finger on
        /// the outfit this was found on.
        ///
        /// Only whole covered triangles go: one covered corner on a triangle
        /// that reaches out from under the glove is the seam where skin meets
        /// garment, and removing those would open a gap instead of closing
        /// one.
        /// </summary>
        private static short[] WithoutCoveredTriangles(
            short[] indices,
            bool[] covered)
        {
            if (indices == null || covered == null)
            {
                return indices ?? new short[0];
            }
            var result = new List<short>(indices.Length);
            for (int triangle = 0; triangle + 2 < indices.Length; triangle += 3)
            {
                ushort index0 = (ushort)indices[triangle];
                ushort index1 = (ushort)indices[triangle + 1];
                ushort index2 = (ushort)indices[triangle + 2];
                if (index0 < covered.Length &&
                    index1 < covered.Length &&
                    index2 < covered.Length &&
                    covered[index0] && covered[index1] && covered[index2])
                {
                    continue;
                }
                result.Add(indices[triangle]);
                result.Add(indices[triangle + 1]);
                result.Add(indices[triangle + 2]);
            }
            return result.ToArray();
        }

        private static short[] ConcatenateIndices(short[] first, short[] second)
        {
            int firstCount = first == null ? 0 : first.Length;
            int secondCount = second == null ? 0 : second.Length;
            var result = new short[firstCount + secondCount];
            if (firstCount > 0)
            {
                Array.Copy(first, 0, result, 0, firstCount);
            }
            if (secondCount > 0)
            {
                Array.Copy(second, 0, result, firstCount, secondCount);
            }
            return result;
        }

        private static void WriteHandGeometryReport(AvatarAsset asset)
        {
            try
            {
                var lines = new List<string>();
                lines.Add(
                    "name".PadRight(58) +
                    " third mapped handOnly  body hand shell fingers");
                foreach (AvatarBatch batch in asset.Batches)
                {
                    lines.Add(
                        (batch.Name ?? "?").PadRight(58) +
                        " " + (batch.ThirdPersonIndices == null
                            ? 0 : batch.ThirdPersonIndices.Length / 3).ToString().PadLeft(5) +
                        " " + (batch.MappedFirstPersonIndices == null
                            ? 0 : batch.MappedFirstPersonIndices.Length / 3).ToString().PadLeft(6) +
                        " " + (batch.MappedFirstPersonHandIndices == null
                            ? 0 : batch.MappedFirstPersonHandIndices.Length / 3).ToString().PadLeft(8) +
                        "  " + (batch.IsBaseBody ? "yes " : "no  ") +
                        " " + (batch.IsHandComponent ? "yes " : "no  ") +
                        " " + (batch.IsBareHandShell ? "yes  " : "no   ") +
                        " " + (batch.HasFingerGeometry ? "yes" : "no"));
                }
                lines.Add("");
                lines.Add("baseBody=" + (asset.BaseBodyBatch == null
                    ? "none" : asset.BaseBodyBatch.Name));
                lines.Add("bareHandShell=" + (asset.BareHandShell == null
                    ? "none" : asset.BareHandShell.Name));
                lines.Add("outerHandBatches=" + asset.OuterHandBatches.Count);

                // Every bone's first-person scale. This multiplies the bone's
                // transform when the hand is skinned, so a bone that has
                // collapsed to nearly nothing takes its vertices with it and
                // bites a piece out of the surface - a fault in the posing
                // that no amount of checking the selection would ever find.
                lines.Add("");
                lines.Add("first-person bone scales (suspicious ones marked):");
                if (asset.FirstPersonBoneScale != null)
                {
                    for (int bone = 0; bone < asset.FirstPersonBoneScale.Length; bone++)
                    {
                        Vector3 scale = asset.FirstPersonBoneScale[bone];
                        float smallest = Math.Min(scale.X, Math.Min(scale.Y, scale.Z));
                        float largest = Math.Max(scale.X, Math.Max(scale.Y, scale.Z));
                        bool suspicious = smallest < 0.25f || largest > 4f ||
                            float.IsNaN(smallest) || float.IsNaN(largest);
                        if (!suspicious)
                        {
                            continue;
                        }
                        lines.Add(
                            "  bone " + bone.ToString().PadLeft(2) +
                            " " + ((AvatarBone)bone).ToString().PadRight(22) +
                            " scale=" + scale +
                            "  <-- collapsed or exploded");
                    }
                }
                string folder = Branding.AvatarFolder(
                    AppDomain.CurrentDomain.BaseDirectory);
                Directory.CreateDirectory(folder);
                File.WriteAllLines(
                    Path.Combine(folder, "hand-geometry.log"),
                    lines.ToArray());
            }
            catch
            {
                // Diagnostics must never stop an avatar loading.
            }
        }

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
                if (version < 1 || version > 4)
                {
                    // Say what this actually means. An avatar arriving from a
                    // peer is that peer's file, so an unknown version means the
                    // two machines are on different builds of the add-on - which
                    // presents as a stock character and looks like broken
                    // syncing rather than a version mismatch.
                    throw new InvalidDataException(
                        "Unsupported Xbox Avatar asset version " + version +
                        "; this build reads up to 4. If the avatar came from" +
                        " another player, that player is running a different" +
                        " build of the add-on and both machines need the same one.");
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
                    // Assets before v4 stored mesh UVs V-flipped, which put
                    // garment atlases upside down: the jacket hem sampled the
                    // shirt strip, so red appeared on the abdomen instead of the
                    // collar and cuffs. Face layers were pre-inverted on import
                    // to cancel that flip, so they were already correct and must
                    // not be touched. Correct the rest here so an avatar
                    // imported before the fix still renders properly.
                    if (version < 4 && asset.Batches[index].FaceTextureUsage < 0)
                    {
                        FlipBatchV(asset.Batches[index]);
                    }
                    asset.Batches[index].BuildFirstPersonGeometry(
                        asset.BindPoseAbsolute[(int)AvatarBone.WristLeft].Translation,
                        asset.BindPoseAbsolute[(int)AvatarBone.WristRight].Translation);
                }
                asset.BaseBodyBatch = ChooseBaseBodyBatch(asset.Batches);
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

                    // The body is not one layer but three - the skin and the
                    // overlay passes that colour it - over identical geometry.
                    // Only the layer chosen as the skin was told which of its
                    // triangles a glove covers, so the other two carried on
                    // drawing the hand underneath the garment, and the skin's
                    // own colour passes were painted straight over the glove.
                    // That reads as holes in the glove rather than as skin on
                    // top of it, which is what it is.
                    foreach (AvatarBatch layer in asset.Batches)
                    {
                        if (layer != asset.BaseBodyBatch &&
                            layer.IsBaseBody &&
                            layer.SourceVertices != null &&
                            asset.BaseBodyBatch.SourceVertices != null &&
                            layer.SourceVertices.Length ==
                                asset.BaseBodyBatch.SourceVertices.Length)
                        {
                            layer.CoveredByOuterHand =
                                asset.BaseBodyBatch.CoveredByOuterHand;
                        }
                    }
                }
                // What "hands mesh" draws: the arm this batch already
                // contributes, plus the avatar's own hand.
                //
                // Not the whole arm-and-hand selection - that was the first
                // attempt and it is why mesh showed fingers and nothing else.
                // Those triangles are drawn through the first-person hand
                // placement, which is built to position a hand; give it a
                // whole arm and everything beyond the wrist lands somewhere
                // off camera, leaving only the few triangles near the fingers
                // on screen. FirstPersonIndices is the compact hand volume the
                // placement is designed for, and is exactly the piece the
                // carrier was standing in for.
                foreach (AvatarBatch batch in asset.Batches)
                {
                    // Keep the skin the glove's openings expose.
                    //
                    // A fingerless glove is mostly holes, and the skin behind
                    // one is the whole point of it. Removing every triangle a
                    // glove triangle passes within 12 mm of takes the skin
                    // around each opening with it, and the opening then shows
                    // the world through the hand. The overlap that removal
                    // exists to prevent is skin poking through the garment,
                    // which the depth buffer already settles when the two are
                    // this close.
                    batch.MappedFirstPersonHandIndices = ConcatenateIndices(
                        WithoutHandTriangles(
                            batch.MappedFirstPersonIndices,
                            batch.FirstPersonSides),
                        WithoutCoveredTriangles(
                            batch.FirstPersonIndices,
                            batch.CoveredByOuterHand));
                    batch.MappedFirstPersonSkinIndices = ConcatenateIndices(
                        WithoutHandTriangles(
                            batch.MappedFirstPersonIndices,
                            batch.FirstPersonSides),
                        batch.FirstPersonIndices);
                }

                WriteHandGeometryReport(asset);
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

        /// <summary>
        /// A surface and the overlay passes painted on top of it, in draw
        /// order.
        ///
        /// An Xbox surface is not one batch but a stack: "…:model:0" followed
        /// by "…:model:0:material-overlay-palette" and "…-decal", the same
        /// triangles with the same UVs drawn again through another texture.
        /// Third person draws the whole stack, which is where the colour comes
        /// from. First person drew only the first layer, so its hand was the
        /// untinted base.
        /// </summary>
        internal AvatarBatch[] MaterialLayersFor(AvatarBatch batch)
        {
            if (batch == null || Batches == null)
            {
                return new AvatarBatch[0];
            }
            string overlayPrefix = batch.Name + ":material-overlay";
            var layers = new List<AvatarBatch> { batch };
            foreach (AvatarBatch candidate in Batches)
            {
                if (candidate != null &&
                    candidate != batch &&
                    candidate.Name != null &&
                    candidate.Name.StartsWith(
                        overlayPrefix,
                        StringComparison.OrdinalIgnoreCase) &&
                    SharesTextureMapping(batch, candidate))
                {
                    layers.Add(candidate);
                }
            }
            return layers.ToArray();
        }

        /// <summary>
        /// Whether two layers address their textures identically.
        ///
        /// The carrier samples one set of UVs, taken from the layer it was
        /// morphed against, so an extra pass is only meaningful when it uses
        /// those same UVs. A body's overlays do; an outfit's do not - its base
        /// spans u=[-0.994,0.997] while its overlays span u=[0,1] - and
        /// pushing the base's UVs through an overlay's atlas samples whatever
        /// happens to be there. Check rather than assume.
        /// </summary>
        private static bool SharesTextureMapping(
            AvatarBatch first,
            AvatarBatch second)
        {
            if (first.DrawVertices == null || second.DrawVertices == null ||
                first.DrawVertices.Length != second.DrawVertices.Length)
            {
                return false;
            }
            for (int index = 0; index < first.DrawVertices.Length; index++)
            {
                Vector2 a = first.DrawVertices[index].TextureCoordinate;
                Vector2 b = second.DrawVertices[index].TextureCoordinate;
                if (Math.Abs(a.X - b.X) > 0.0001f ||
                    Math.Abs(a.Y - b.Y) > 0.0001f)
                {
                    return false;
                }
            }
            return true;
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

        /// <summary>
        /// The avatar's own arm geometry with nothing removed, hands included.
        ///
        /// First person normally throws the exported hand away and re-projects
        /// the stock ProxyBoy hand onto its surface instead. This is the same
        /// geometry third person draws, kept so that choice can be reversed at
        /// runtime and the two compared.
        /// </summary>
        internal short[] MappedFirstPersonHandIndices;

        /// <summary>
        /// The same selection with the skin a glove covers left in.
        ///
        /// A fingerless glove is mostly openings, and the skin behind one is
        /// the point of it. Whether removing that skin closes an overlap or
        /// opens a hole depends on the garment, so both answers are kept and
        /// the tuning file chooses.
        /// </summary>
        internal short[] MappedFirstPersonSkinIndices;
        internal byte[] FirstPersonSides;
        internal bool[] FirstPersonUsed;

        /// <summary>
        /// Each vertex's distance to the nearer wrist in bind space. Says where
        /// a vertex is rather than how it was rigged, which is the only way to
        /// find a combined outfit's glove: it is bound to arm bones, so no
        /// weight test can tell it apart from the sleeve it shares a model
        /// with.
        /// </summary>
        internal float[] WristDistance;

        /// <summary>
        /// Which of this batch's vertices an equipped glove sits on top of.
        /// Only meaningful on the base body, and only once the outer hands
        /// have been matched against it.
        /// </summary>
        internal bool[] CoveredByOuterHand;

        /// <summary>
        /// How far from the wrist the first-person carrier reaches. Anything
        /// inside this the carrier draws, so the mesh must not: the two have to
        /// partition the hand between them or they overlap, and the mesh's half
        /// is posed by bones first person does not use.
        ///
        /// Matches ProxyHandCarrier's own cuff radius deliberately.
        /// </summary>
        internal const float CarrierHandRadius = 0.11f;
        internal byte[][] MappedBindings;
        internal byte[][] MappedWeights;
        internal Vector3 DiffuseColor;
        internal byte[] TexturePng;
        internal Texture2D Texture;

        /// <summary>
        /// This batch's image would not decode, so stop trying. Drawn untextured
        /// rather than not at all.
        /// </summary>
        internal bool TextureUnavailable;
        internal uint CategoryMask;
        internal int ShaderId;
        internal byte PaletteMask;
        internal Vector4[] Palette;
        private Vector3 _averageColor;
        private bool _averageColorKnown;

        /// <summary>
        /// One colour standing in for this material's whole texture, for the
        /// hand build that draws the game's own shape rather than the avatar's:
        /// that shape carries the game's UVs, which address nothing meaningful
        /// in the avatar's atlas, so a colour is the only part of the material
        /// that can be carried across.
        ///
        /// Fully transparent texels are skipped - a garment atlas is mostly
        /// empty space, and averaging that in would wash every glove out
        /// towards the same pale grey.
        /// </summary>
        internal Vector3 AverageColor()
        {
            if (_averageColorKnown)
            {
                return _averageColor;
            }
            _averageColorKnown = true;
            _averageColor = DiffuseColor;
            try
            {
                if (Texture == null)
                {
                    return _averageColor;
                }
                var texels = new Color[Texture.Width * Texture.Height];
                Texture.GetData(texels);
                float red = 0f, green = 0f, blue = 0f;
                int counted = 0;
                foreach (Color texel in texels)
                {
                    if (texel.A < 8)
                    {
                        continue;
                    }
                    red += texel.R;
                    green += texel.G;
                    blue += texel.B;
                    counted++;
                }
                if (counted > 0)
                {
                    _averageColor = new Vector3(
                        red / counted / 255f,
                        green / counted / 255f,
                        blue / counted / 255f);
                }
            }
            catch
            {
                // Keep the diffuse colour if the texture cannot be read.
            }
            return _averageColor;
        }

        internal bool IsBaseBody;
        internal bool IsOverlayLayer;
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

            // A layer painted onto the body rather than the body itself, such
            // as "…:body:0:material-overlay-decal". It carries a white material
            // and addresses a thin decal strip of its atlas, so it is never the
            // skin and must not be mistaken for it.
            batch.IsOverlayLayer =
                batch.Name.IndexOf("overlay", StringComparison.OrdinalIgnoreCase) >= 0 ||
                batch.Name.IndexOf("decal", StringComparison.OrdinalIgnoreCase) >= 0;
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

            // First person needs the same answer. It draws its own selection,
            // which still contained every triangle removed here, so skin and
            // glove ended up occupying the same space and fighting over the
            // depth buffer - and where the skin won, the glove looked as
            // though pieces of it were missing.
            CoveredByOuterHand = covered;
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
                    // Any vertex in the hand, not all three.
                    //
                    // The carrier supplies the whole hand here, so this mesh's
                    // own hand triangles have to go. Requiring all three
                    // vertices kept every triangle that straddled the wrist -
                    // two corners in the hand, one in the forearm - and those
                    // are still posed by hand bones, which first person does
                    // not pose the way this mesh expects. They came out as
                    // long flat slabs reaching across the hand, and on a
                    // combined outfit, where sleeve and glove are one model,
                    // there are a lot of them. The carrier covers the wrist,
                    // so nothing is left uncovered by dropping them.
                    //
                    // Skin weights alone cannot find it. A combined outfit
                    // binds its glove to arm bones rather than to the wrist,
                    // so the whole glove scores no wrist weight and survived
                    // every weight-based test - the outfit kept all 306 of its
                    // first-person triangles while the base body dropped 32.
                    // BuildFirstPersonGeometry has already worked out which
                    // vertices sit inside the hand volume, by position rather
                    // than by binding, and that answer does not care how the
                    // garment was rigged.
                    bool duplicateHandTriangle =
                        HandWeight(SourceVertices[(ushort)index0], wristBone) >= completeHandWeight ||
                        HandWeight(SourceVertices[(ushort)index1], wristBone) >= completeHandWeight ||
                        HandWeight(SourceVertices[(ushort)index2], wristBone) >= completeHandWeight ||
                        WristDistance[(ushort)index0] <= CarrierHandRadius ||
                        WristDistance[(ushort)index1] <= CarrierHandRadius ||
                        WristDistance[(ushort)index2] <= CarrierHandRadius;
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
            WristDistance = new float[SourceVertices.Length];
            for (int index = 0; index < SourceVertices.Length; index++)
            {
                float leftDistance = Vector3.Distance(
                    SourceVertices[index].Position,
                    wristLeft);
                float rightDistance = Vector3.Distance(
                    SourceVertices[index].Position,
                    wristRight);
                float nearest = Math.Min(leftDistance, rightDistance);
                WristDistance[index] = nearest;
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

        // Hellos that arrived before their sender's capability marker did.
        // Capability adverts are broadcast and relayed by the host, while hellos
        // go directly to one peer, so the two travel on different sequence
        // channels and are not ordered against each other. Dropping an unproven
        // hello loses it for good, because a peer only ever says hello once per
        // session. Hold it until the marker proves the sender instead.
        private static readonly Dictionary<byte, NetworkGamer> DeferredHello =
            new Dictionary<byte, NetworkGamer>();
        private const int MaxDeferredHellos = 32;

        private static uint _nextTransferId = 1;
        private static DateTime _nextCleanupUtc = DateTime.MinValue;
        private static LocalSnapshot _localSnapshot;
        private static bool _capabilityAdvertisementPending = true;

        // Whether this session has actually put our marker on the wire. A hello
        // sent before that is unanswerable: the peer cannot yet tell we are a
        // capable peer, so it refuses to serve us and we never ask again.
        private static bool _capabilityAdvertised;

        // How long to hold hellos back waiting for our own marker before giving
        // up and asking anyway. Short enough not to be noticed, long enough to
        // cover a normal join.
        private const double HelloGateSeconds = 5.0;
        private static DateTime _helloGateDeadlineUtc = DateTime.MinValue;

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
            if (gamer.IsLocal)
            {
                // A fresh session for this process. Statics survive from the
                // previous one, so re-prove ourselves before asking anybody for
                // an avatar, or the second session repeats the first-join bug.
                _capabilityAdvertised = false;
                _helloGateDeadlineUtc = DateTime.UtcNow.AddSeconds(HelloGateSeconds);
                DeferredHello.Clear();
            }
            else
            {
                // Gamer IDs can be reused after a disconnect. Do not let a new
                // player inherit the previous occupant's cached model binding
                // or protocol capability.
                PlayerBinding binding;
                if (Players.TryGetValue(gamer.Id, out binding) &&
                    binding.Gamer != gamer)
                {
                    // The previous occupant's model goes with the binding, so
                    // hand its textures back rather than leaving them on the
                    // device for the rest of the session.
                    if (binding.Avatar != null)
                    {
                        var retired = binding.Avatar.ProxyModelEntity as
                            ImportedAvatarModelEntity;
                        if (retired != null)
                        {
                            binding.Avatar.ProxyModelEntity = null;
                            retired.ReleaseGraphicsResources();
                        }
                    }
                    Players.Remove(gamer.Id);
                    RemoteAssetPaths.Remove(gamer.Id);
                    AnchorReport.Remove(gamer.Id);
                }
                PendingHello.Remove(gamer.Id);
                HelloSent.Remove(gamer.Id);
                PeerReady.Remove(gamer.Id);
                DeferredHello.Remove(gamer.Id);
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

            ZZAvatarSyncMessage packet =
                message as ZZAvatarSyncMessage;
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
                // A hello is the exception worth holding: only an add-on peer
                // can have sent one, and its marker may simply still be in
                // flight on the other sequence channel. Dropping it strands the
                // sender with a stock model for the rest of the session, since
                // it will never ask twice. Park it for the marker to clear.
                if (packet.Kind == HelloPacket &&
                    DeferredHello.Count < MaxDeferredHellos)
                {
                    DeferredHello[packet.Sender.Id] = packet.Sender;
                }
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

            // Prefer to ask for avatars only once our own marker is on the wire,
            // because a peer cannot recognise us as capable before that and
            // would refuse. But never let that preference become a hard
            // requirement: the advertisement has several ways to keep returning
            // early - no local player yet, or no avatar description on it - and
            // making hellos wait on it means an avatar that never advertises
            // also never asks, so nobody's avatar ever appears. Fall back to
            // asking anyway, which is what this did before the ordering fix, and
            // is strictly better than silence.
            if (_helloGateDeadlineUtc == DateTime.MinValue)
            {
                _helloGateDeadlineUtc = DateTime.UtcNow.AddSeconds(HelloGateSeconds);
            }
            if (_capabilityAdvertised || DateTime.UtcNow >= _helloGateDeadlineUtc)
            {
                FlushPendingHello(local);
            }

            ServeDeferredHellos();

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
                ZZAvatarSyncMessage.SendPacket(
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
            _capabilityAdvertised = true;
        }

        /// <summary>
        /// Answers hellos that arrived before their sender was proven capable.
        ///
        /// Serviced here rather than the moment the marker lands, because
        /// answering reads and hashes the local avatar from disk and the marker
        /// arrives on the stock message path during a join.
        /// </summary>
        private static void ServeDeferredHellos()
        {
            if (DeferredHello.Count == 0)
            {
                return;
            }

            var ids = new List<byte>(DeferredHello.Keys);
            foreach (byte id in ids)
            {
                NetworkGamer peer = DeferredHello[id];
                if (peer == null || peer.HasLeftSession)
                {
                    DeferredHello.Remove(id);
                    continue;
                }

                // Identity, not id: a reused gamer id must not inherit the
                // previous occupant's pending hello.
                if (!IsPeerCapable(peer))
                {
                    continue;
                }

                DeferredHello.Remove(id);
                HandleHello(peer);
            }
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
                ZZAvatarSyncMessage.SendHello(local, peer);
                PendingHello.Remove(id);
                HelloSent[id] = peer;
            }
        }

        /// <summary>
        /// Report the players this cannot help. The correction itself runs in
        /// <see cref="ImportedAvatarModelEntity.OnUpdate"/>, which is the only
        /// point in the frame where writing the anchor has any effect.
        /// </summary>
        private static void ApplyThirdPersonItemAnchors()
        {
            foreach (PlayerBinding binding in Players.Values)
            {
                if (binding == null || binding.Avatar == null)
                {
                    continue;
                }
                if (!(binding.Avatar.ProxyModelEntity is ImportedAvatarModelEntity))
                {
                    // Not an error: this player has no imported avatar yet, so
                    // there is no custom grip to apply. Report it anyway, since
                    // third person is only ever seen on a remote player and a
                    // silent skip is indistinguishable from a broken hook.
                    Report(binding, "no imported avatar (stock model)");
                }
            }

            // Every avatar has updated by now, so this frame's collection is
            // complete and can be written.
            FlushAnchorReport();
        }

        private static PlayerBinding FindBinding(Avatar avatar)
        {
            foreach (PlayerBinding binding in Players.Values)
            {
                if (binding != null && binding.Avatar == avatar)
                {
                    return binding;
                }
            }
            return null;
        }

        /// <summary>
        /// Seat one avatar's held item. Called from the imported model's own
        /// update so the write lands after Avatar.UpdateParts has reset the
        /// anchor and before the item samples it.
        /// </summary>
        internal static void ApplyItemAnchor(
            Avatar avatar,
            ImportedAvatarModelEntity imported)
        {
            if (avatar == null || imported == null)
            {
                return;
            }
            PlayerBinding binding = FindBinding(avatar);
            {
                Entity itemAnchor = avatar.GetAvatarPart(
                    AvatarBone.PropRight);
                Matrix transform = avatar.GetBoneToAvatar(
                    AvatarBone.PropRight);
                float shape = imported.AvatarShapeScale;

                // Where the game itself would put the item, before this hook
                // touches anything. Unmodded that lands in the stock character's
                // hand, so it is the reference the corrected position has to be
                // judged against.
                Vector3 stockAnchor = transform.Translation;

                // The held item and how far it sits from the anchor. Each item
                // class picks its own offset, so this is what turns an error in
                // the anchor's rotation into a visible error in position, and
                // it is the key a per-item nudge is looked up by.
                bool haveChild =
                    itemAnchor.Children != null && itemAnchor.Children.Count > 0;
                Vector3 childOffset = haveChild
                    ? itemAnchor.Children[0].LocalToParent.Translation
                    : Vector3.Zero;
                string childTypeName = haveChild
                    ? itemAnchor.Children[0].GetType().Name
                    : "none";

                // Third person only. First person keeps the stock anchor: the
                // viewmodel hand is drawn by a different path with its own
                // scaling, so moving the anchor there lifted the held item off
                // the hand instead of onto it.
                Vector3 target;
                if (!imported.TryGetThirdPersonPropTranslation(out target))
                {
                    Report(binding, "no third-person grip" +
                        (avatar.HideHead
                            ? " (first person, expected)"
                            : " (UNEXPECTED: head is visible)"));
                    return;
                }

                // Why the stock anchor is wrong at all, and by how much:
                //
                // Avatar.UpdateParts rebuilds the item anchor every frame from
                // the animated skeleton but forces each bone's translation
                // back to Avatar.BindPose - the stock 1.6 m rig. The visible
                // arms come from the imported avatar's own bind pose instead,
                // via ProxyModelEntity. So the anchor tracks a body the player
                // is not wearing, and the taller the avatar the further the
                // hand has moved away from it. That is the floating weapon.
                //
                // Both live in the same avatar space - the imported mesh is
                // drawn through RenderWorld, which is that space with Z
                // negated, and TryGetAvatarSpaceBone undoes exactly that - so
                // the imported bones can be written straight into the anchor.
                Vector3 propToGrip = Vector3.Zero;
                Vector3 importedProp;
                bool haveImportedProp = imported.TryGetAvatarSpaceBone(
                    AvatarBone.PropRight, out importedProp);
                if (haveImportedProp)
                {
                    propToGrip = target - importedProp;
                }

                // Which of these closes the gap is a question about pixels, so
                // it is left switchable rather than settled by argument.
                //
                // Only "hand" replaces the anchor's rotation as well as its
                // position, and only that can seat every item at once: each
                // item is offset from the anchor by a different amount along
                // the anchor's own axes, so a stock rotation misplaces each of
                // them by a different distance and swings them all as the arm
                // pitches. The position-only modes can be tuned to suit one
                // item, never all of them.
                Vector3 placed;
                Matrix handToAvatar;
                bool haveHand = imported.TryGetAvatarSpaceBoneTransform(
                    AvatarBone.PropRight, out handToAvatar);
                switch (ItemTuning.Mode)
                {
                    case ItemTuning.Placement.Stock:
                        placed = stockAnchor;
                        break;
                    case ItemTuning.Placement.Shift:
                        placed = stockAnchor + propToGrip;
                        break;
                    case ItemTuning.Placement.Prop:
                        placed = haveImportedProp ? importedProp : target;
                        break;
                    case ItemTuning.Placement.Hand:
                        if (haveHand)
                        {
                            // Keep the imported rotation, and with it the
                            // scale-free axes every item offset is measured
                            // against.
                            transform = handToAvatar;
                        }
                        placed = haveHand ? handToAvatar.Translation : target;
                        break;
                    default:
                        placed = target;
                        break;
                }

                // Editable nudge, for trimming the fit without a rebuild. Zero
                // unless the tuning file says otherwise. Measured along the
                // hand's own axes by default so one value stays right at every
                // view pitch instead of drifting as the arm swings.
                Vector3 nudge =
                    ItemTuning.OffsetFor(shape) +
                    ItemTuning.OffsetForItem(childTypeName);
                if (ItemTuning.NudgeInHandSpace)
                {
                    nudge = Vector3.TransformNormal(nudge, transform);
                }

                transform.Translation = placed + nudge;

                itemAnchor.LocalToParent = transform;

                // Everything below is diagnostics only. It runs at the log's
                // own cadence rather than every frame: built per frame per
                // player it rebuilt the export pose twice more and produced a
                // long string sixty times a second, all of it to describe a
                // file written once every two seconds.
                if (!ReportDue)
                {
                    return;
                }

                // "item" is where the held object actually ends up once its own
                // offset is applied, and "grip" is where the fingers are. Those
                // two matching is the thing that matters; the anchor sitting
                // somewhere else is expected whenever the item is offset.
                Vector3 item =
                    Vector3.TransformNormal(childOffset, transform) +
                    transform.Translation;

                // The item lands where it is aimed, so when it still looks
                // wrong the aim point is the suspect. Print the candidate hand
                // bones alongside it, all in the anchor's own space, so the one
                // the visible hand actually follows can be identified by which
                // number matches where the hand is on screen.
                Vector3 wrist, prop;
                bool haveWrist = imported.TryGetAvatarSpaceBone(
                    AvatarBone.WristRight, out wrist);
                bool haveProp = imported.TryGetAvatarSpaceBone(
                    AvatarBone.PropRight, out prop);

                Report(binding,
                    "mode=" + ItemTuning.Mode.ToString().ToLowerInvariant() +
                    " build=" + shape.ToString("F3") +
                    " item=" + item +
                    " grip=" + target +
                    " stockAnchor=" + stockAnchor +
                    " shift=" + propToGrip +
                    " shiftLen=" + propToGrip.Length().ToString("F4") +
                    // Echo the tuning actually in force, so a saved edit can be
                    // confirmed as picked up without restarting anything.
                    " tuning=" + nudge +
                    (haveWrist
                        ? " gripToWrist=" + Vector3.Distance(target, wrist).ToString("F4")
                        : " wrist=?") +
                    (haveProp
                        ? " gripToProp=" + Vector3.Distance(target, prop).ToString("F4")
                        : " prop=?") +
                    " childOffset=" + childOffset +
                    " child0=" + childTypeName);
            }
        }

        private static readonly Dictionary<byte, string> AnchorReport =
            new Dictionary<byte, string>();
        private static DateTime _nextAnchorReportUtc = DateTime.MinValue;
        private static bool _reportArmed;

        /// <summary>
        /// Whether this frame is a reporting frame. Callers use it to skip
        /// building a diagnostic they would only throw away.
        ///
        /// Armed for a whole frame rather than until the first write, so every
        /// player still describes itself in the same pass. Gating on the write
        /// clock instead would let the first player consume the window and
        /// leave everyone else's line stale.
        /// </summary>
        private static bool ReportDue
        {
            get { return _reportArmed; }
        }

        /// <summary>
        /// One line per player, collected during a reporting frame and written
        /// once at the end of it. Third person is only ever visible on somebody
        /// else, so this has to describe remote players as well as the local
        /// one, including the ones that are deliberately skipped.
        /// </summary>
        private static void Report(PlayerBinding binding, string detail)
        {
            if (binding == null)
            {
                // An avatar with no binding yet: nothing to name the line
                // after, and it will report itself once the player is tracked.
                return;
            }
            try
            {
                NetworkGamer gamer = binding.Gamer;
                byte key = gamer == null ? (byte)255 : gamer.Id;
                AnchorReport[key] =
                    (gamer == null ? "?" : gamer.Gamertag) +
                    (gamer != null && gamer.IsLocal ? " [local]" : " [remote]") +
                    "  " + detail;
            }
            catch (Exception exception)
            {
                ImportedAvatarModelEntity.WriteFailure(exception);
            }
        }

        /// <summary>
        /// Write what this frame collected, then decide when to collect next.
        /// Runs once per frame from the game-update epilogue, after every
        /// avatar has had its turn.
        /// </summary>
        private static void FlushAnchorReport()
        {
            try
            {
                if (!_reportArmed)
                {
                    if (DateTime.UtcNow >= _nextAnchorReportUtc)
                    {
                        // Collect during the next frame and write at the end of
                        // it: the avatars update before this point, so arming
                        // now is the earliest a full set can be gathered.
                        _reportArmed = true;
                    }
                    return;
                }
                _reportArmed = false;
                _nextAnchorReportUtc = DateTime.UtcNow.AddSeconds(2);

                string folder = Branding.AvatarFolder(
                    AppDomain.CurrentDomain.BaseDirectory);
                Directory.CreateDirectory(folder);
                var lines = new List<string>();
                // Lead with the tuning state so a saved edit can be confirmed as
                // read even when no avatar is present to apply it to - otherwise
                // "nothing happens" is ambiguous between the file not loading and
                // there being nothing to tune.
                lines.Add("tuning file: " + ItemTuning.Describe());
                var players = new List<string>(AnchorReport.Values);
                players.Sort(StringComparer.OrdinalIgnoreCase);
                lines.AddRange(players);
                File.WriteAllLines(
                    Path.Combine(folder, "anchor-status.log"),
                    lines.ToArray());
            }
            catch
            {
                // Diagnostics must never break rendering.
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
            ZZAvatarSyncMessage.SendPacket(
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

        private static void HandleManifest(ZZAvatarSyncMessage packet)
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
            ZZAvatarSyncMessage.SendPacket(
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

        private static void HandleRequest(ZZAvatarSyncMessage packet)
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

        private static void HandleChunk(ZZAvatarSyncMessage packet)
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

        private static bool ValidManifest(ZZAvatarSyncMessage packet)
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

                // The setter has detached the old model, so nothing can draw it
                // any more and its textures can go back. Without this every
                // avatar change or rejoin leaked a texture set for the rest of
                // the session.
                var retired = previous as ImportedAvatarModelEntity;
                if (retired != null && retired != replacement)
                {
                    retired.ReleaseGraphicsResources();
                }

                // Entity.Update walks children in order, and a held item
                // records the world matrix it will draw with during that walk.
                // Installing the model appends it after the prop part, so move
                // the part to the end: the anchor correction runs in this
                // model's update and has to happen before the item reads it.
                Entity propPart = binding.Avatar.GetAvatarPart(
                    AvatarBone.PropRight);
                if (binding.Avatar.Children.Remove(propPart))
                {
                    binding.Avatar.Children.Add(propPart);
                }

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
                return Branding.AvatarFolder(AppDomain.CurrentDomain.BaseDirectory);
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

    public sealed class ZZAvatarSyncMessage : CastleMinerZMessage
    {
        internal byte Protocol;
        internal byte Kind;
        internal uint TransferId;
        internal int TotalLength;
        internal ushort ChunkIndex;
        internal ushort ChunkCount;
        internal byte[] Hash;
        internal byte[] Payload;

        private ZZAvatarSyncMessage()
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
                throw new InvalidDataException("Invalid avatar chunk length.");
            }
            Payload = reader.ReadBytes(payloadLength);
            if (Hash.Length != 32 || Payload.Length != payloadLength)
            {
                throw new EndOfStreamException("Truncated avatar packet.");
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
            ZZAvatarSyncMessage packet =
                GetSendInstance<ZZAvatarSyncMessage>();
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
