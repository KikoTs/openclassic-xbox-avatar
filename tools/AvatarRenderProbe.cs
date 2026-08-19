using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using Microsoft.Xna.Framework;
using Color = System.Drawing.Color;

// Offline software renderer for an .ocavatar.
//
// The avatar can otherwise only be inspected by launching the game and looking
// at it, which makes texture and attachment bugs slow and subjective to chase.
// This loads the asset through the mod's own AvatarAsset.Load, skins it exactly
// as ImportedAvatarModelEntity does, and rasterises it to a PNG, so a change can
// be seen and diffed without the game.
//
// Usage:
//   AvatarRenderProbe <mod.dll> <avatar.ocavatar> <game folder> <out.png>
//                     [--address wrap|clamp] [--alphacut N] [--view front|side|back]
//                     [--zoom full|head] [--size N] [--batch NAME]
internal static class AvatarRenderProbe
{
    private const BindingFlags Hidden =
        BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;

    private static int Main(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine(
                "Usage: AvatarRenderProbe <mod.dll> <avatar.ocavatar> <game folder> <out.png> [options]");
            return 2;
        }

        string gameFolder = Path.GetFullPath(args[2]);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
        {
            string wanted = new AssemblyName(e.Name).Name;
            foreach (string extension in new[] { ".dll", ".exe" })
            {
                string candidate = Path.Combine(gameFolder, wanted + extension);
                if (File.Exists(candidate))
                {
                    return Assembly.LoadFrom(candidate);
                }
            }
            return null;
        };

        return Run(args);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        string address = "perbatch";
        int alphaCut = 0;
        string view = "front";
        string zoom = "full";
        int size = 768;
        string onlyBatch = null;
        string dumpTextures = null;

        for (int i = 4; i < args.Length - 1; i++)
        {
            string option = args[i].ToLowerInvariant();
            if (option == "--address") { address = args[i + 1].ToLowerInvariant(); }
            else if (option == "--alphacut") { alphaCut = int.Parse(args[i + 1]); }
            else if (option == "--view") { view = args[i + 1].ToLowerInvariant(); }
            else if (option == "--zoom") { zoom = args[i + 1].ToLowerInvariant(); }
            else if (option == "--size") { size = int.Parse(args[i + 1]); }
            else if (option == "--batch") { onlyBatch = args[i + 1]; }
            else if (option == "--dumptex") { dumpTextures = args[i + 1]; }
        }

        Assembly mod = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        Type assetType = ModType(mod, "AvatarAsset");
        object asset = assetType.GetMethod("Load", Hidden)
            .Invoke(null, new object[] { Path.GetFullPath(args[1]) });

        Matrix[] sourceLocal = (Matrix[])Field(assetType, asset, "SourcePoseLocal");
        Matrix[] inverseBind = (Matrix[])Field(assetType, asset, "InverseBindPose");
        Array batches = (Array)Field(assetType, asset, "Batches");

        // Static preview pose: forward kinematics over the imported local pose,
        // which is what the runtime's export-space pose reduces to when no
        // animation is playing. Build scale rides in the root, as in the game.
        int[] parents = DefaultParents(mod);
        Matrix[] world = new Matrix[sourceLocal.Length];
        world[0] = sourceLocal[0];
        for (int bone = 1; bone < sourceLocal.Length; bone++)
        {
            world[bone] = sourceLocal[bone] * world[parents[bone]];
        }

        Matrix[] skin = new Matrix[world.Length];
        for (int bone = 0; bone < world.Length; bone++)
        {
            skin[bone] = inverseBind[bone] * world[bone];
        }

        var triangles = new List<Triangle>();
        Type batchType = null;
        foreach (object batch in batches)
        {
            if (batchType == null) { batchType = batch.GetType(); }
            string name = (string)Field(batchType, batch, "Name");
            if (onlyBatch != null && name.IndexOf(onlyBatch, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            short[] indices = (short[])Field(batchType, batch, "ThirdPersonIndices");
            if (indices == null || indices.Length < 3)
            {
                indices = (short[])Field(batchType, batch, "Indices");
            }
            if (indices == null || indices.Length < 3) { continue; }

            byte[] png = (byte[])Field(batchType, batch, "TexturePng");
            if (dumpTextures != null && png != null && png.Length > 0)
            {
                Directory.CreateDirectory(dumpTextures);
                string safe = name.Replace(":", "_").Replace("/", "_");
                File.WriteAllBytes(Path.Combine(dumpTextures, safe + ".png"), png);
            }
            Bitmap texture = null;
            if (png != null && png.Length > 0)
            {
                using (var stream = new MemoryStream(png))
                {
                    texture = new Bitmap(Image.FromStream(stream));
                }
            }

            Array sourceVertices = (Array)Field(batchType, batch, "SourceVertices");
            Array drawVertices = (Array)Field(batchType, batch, "DrawVertices");
            int count = sourceVertices.Length;
            var positions = new Vector3[count];
            var uvs = new Vector2[count];

            Type sourceType = null;
            Type drawType = null;
            for (int index = 0; index < count; index++)
            {
                object sv = sourceVertices.GetValue(index);
                if (sourceType == null) { sourceType = sv.GetType(); }
                Vector3 position = (Vector3)Field(sourceType, sv, "Position");
                byte[] bindings = (byte[])Field(sourceType, sv, "Bindings");
                byte[] weights = (byte[])Field(sourceType, sv, "Weights");

                Vector3 skinned = Vector3.Zero;
                float total = 0f;
                for (int influence = 0; influence < 4; influence++)
                {
                    float weight = weights[influence] / 255f;
                    int bone = bindings[influence];
                    if (weight <= 0f || bone < 0 || bone >= skin.Length) { continue; }
                    skinned += Vector3.Transform(position, skin[bone]) * weight;
                    total += weight;
                }
                if (total <= 0.0001f) { skinned = position; }
                else if (Math.Abs(total - 1f) > 0.0001f) { skinned /= total; }
                positions[index] = skinned;

                object dv = drawVertices.GetValue(index);
                if (drawType == null) { drawType = dv.GetType(); }
                uvs[index] = (Vector2)Field(drawType, dv, "TextureCoordinate");
            }

            float uMin = float.MaxValue, uMax = float.MinValue;
            float vMin = float.MaxValue, vMax = float.MinValue;
            for (int index = 0; index < count; index++)
            {
                if (uvs[index].X < uMin) uMin = uvs[index].X;
                if (uvs[index].X > uMax) uMax = uvs[index].X;
                if (uvs[index].Y < vMin) vMin = uvs[index].Y;
                if (uvs[index].Y > vMax) vMax = uvs[index].Y;
            }
            object faceUsage = Field(batchType, batch, "FaceTextureUsage");
            object faceFrame = Field(batchType, batch, "FaceFrame");
            int usage = Convert.ToInt32(faceUsage);
            int frame = Convert.ToInt32(faceFrame);
            bool isFace = usage >= 0;
            // The game shows one expression frame at a time; stacking all of
            // them is a harness artefact, not what the player sees.
            if (isFace && frame > 0) { continue; }
            Vector3 diffuseColour = (Vector3)Field(batchType, batch, "DiffuseColor");
            Color diffuse = Color.FromArgb(255,
                Clamp8((int)(diffuseColour.X * 255)),
                Clamp8((int)(diffuseColour.Y * 255)),
                Clamp8((int)(diffuseColour.Z * 255)));
            Console.WriteLine(string.Format(
                "batch {0,-34} tris={1,5} tex={2,-9} u=[{3,7:F3},{4,7:F3}] v=[{5,7:F3},{6,7:F3}] faceUsage={7} frame={8}",
                name,
                indices.Length / 3,
                texture == null ? "NONE" : texture.Width + "x" + texture.Height,
                uMin, uMax, vMin, vMax,
                faceUsage, faceFrame));

            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                triangles.Add(new Triangle
                {
                    A = positions[indices[i]],
                    B = positions[indices[i + 1]],
                    C = positions[indices[i + 2]],
                    Ua = uvs[indices[i]],
                    Ub = uvs[indices[i + 1]],
                    Uc = uvs[indices[i + 2]],
                    Texture = texture,
                    Batch = name
                    ,Diffuse = diffuse
                    ,IsFaceLayer = isFace
                });
            }
        }

        if (triangles.Count == 0)
        {
            Console.Error.WriteLine("No triangles to draw.");
            return 1;
        }

        Render(triangles, args[3], address, alphaCut, view, zoom, size);
        Console.WriteLine(
            "wrote " + args[3] +
            " triangles=" + triangles.Count +
            " address=" + address +
            " alphacut=" + alphaCut +
            " view=" + view + " zoom=" + zoom);
        return 0;
    }

    private struct Triangle
    {
        public Vector3 A, B, C;
        public Vector2 Ua, Ub, Uc;
        public Bitmap Texture;
        public string Batch;
        public Color Diffuse;
        public bool IsFaceLayer;
    }

    private static void Render(
        List<Triangle> triangles, string outPath, string address,
        int alphaCut, string view, string zoom, int size)
    {
        // Project orthographically: the goal is a readable, repeatable picture,
        // not a match for the game's camera.
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (Triangle t in triangles)
        {
            foreach (Vector3 p in new[] { t.A, t.B, t.C })
            {
                Vector2 s = Project(p, view);
                if (s.X < minX) minX = s.X;
                if (s.X > maxX) maxX = s.X;
                if (s.Y < minY) minY = s.Y;
                if (s.Y > maxY) maxY = s.Y;
            }
        }

        if (zoom == "head")
        {
            // Top fifth of the body, which is where the face layers live.
            float span = maxY - minY;
            minY = maxY - span * 0.22f;
            float midX = (minX + maxX) / 2f;
            float half = span * 0.13f;
            minX = midX - half;
            maxX = midX + half;
        }

        float width = Math.Max(maxX - minX, 1e-4f);
        float height = Math.Max(maxY - minY, 1e-4f);
        float scale = Math.Min(size / width, size / height) * 0.92f;
        float offsetX = size / 2f - (minX + maxX) / 2f * scale;
        float offsetY = size / 2f + (minY + maxY) / 2f * scale;

        var colour = new float[size, size, 4];
        var depth = new float[size, size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++) { depth[x, y] = float.MaxValue; }
        }

        foreach (Triangle t in triangles)
        {
            Vector2 pa = ToScreen(t.A, view, scale, offsetX, offsetY);
            Vector2 pb = ToScreen(t.B, view, scale, offsetX, offsetY);
            Vector2 pc = ToScreen(t.C, view, scale, offsetX, offsetY);
            float za = Depth(t.A, view), zb = Depth(t.B, view), zc = Depth(t.C, view);

            int loX = (int)Math.Max(0, Math.Floor(Math.Min(pa.X, Math.Min(pb.X, pc.X))));
            int hiX = (int)Math.Min(size - 1, Math.Ceiling(Math.Max(pa.X, Math.Max(pb.X, pc.X))));
            int loY = (int)Math.Max(0, Math.Floor(Math.Min(pa.Y, Math.Min(pb.Y, pc.Y))));
            int hiY = (int)Math.Min(size - 1, Math.Ceiling(Math.Max(pa.Y, Math.Max(pb.Y, pc.Y))));

            float area = Edge(pa, pb, pc);
            if (Math.Abs(area) < 1e-9f) { continue; }

            for (int y = loY; y <= hiY; y++)
            {
                for (int x = loX; x <= hiX; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float w0 = Edge(pb, pc, p) / area;
                    float w1 = Edge(pc, pa, p) / area;
                    float w2 = Edge(pa, pb, p) / area;
                    if (w0 < 0 || w1 < 0 || w2 < 0) { continue; }

                    float z = w0 * za + w1 * zb + w2 * zc;
                    // LessEqual, matching DepthStencilState.Default. Face layers are
                    // the same head geometry as the base head and so are exactly
                    // coplanar with it; a strict test would reject every one.
                    if (z > depth[x, y]) { continue; }

                    Vector2 uv = t.Ua * w0 + t.Ub * w1 + t.Uc * w2;
                    bool useClamp = address == "clamp" ||
                        (address == "perbatch" && t.IsFaceLayer);
                    Color texel = t.Texture == null
                        ? t.Diffuse
                        : Sample(t.Texture, uv, useClamp);
                    if (texel.A <= alphaCut) { continue; }

                    float alpha = texel.A / 255f;
                    colour[x, y, 0] = colour[x, y, 0] * (1 - alpha) + texel.R / 255f * alpha;
                    colour[x, y, 1] = colour[x, y, 1] * (1 - alpha) + texel.G / 255f * alpha;
                    colour[x, y, 2] = colour[x, y, 2] * (1 - alpha) + texel.B / 255f * alpha;
                    colour[x, y, 3] = Math.Max(colour[x, y, 3], alpha);

                    // Depth is written for any texel that passed the cut, which
                    // mirrors the game's DepthStencilState.Default.
                    depth[x, y] = z;
                }
            }
        }

        using (var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Mid grey behind, so both dark art and transparency read.
                    float a = colour[x, y, 3];
                    int r = (int)((colour[x, y, 0] * a + 0.35f * (1 - a)) * 255);
                    int g = (int)((colour[x, y, 1] * a + 0.35f * (1 - a)) * 255);
                    int b = (int)((colour[x, y, 2] * a + 0.35f * (1 - a)) * 255);
                    bitmap.SetPixel(x, y, Color.FromArgb(255, Clamp8(r), Clamp8(g), Clamp8(b)));
                }
            }
            bitmap.Save(outPath, ImageFormat.Png);
        }
    }

    private static int Clamp8(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }

    private static Vector2 Project(Vector3 p, string view)
    {
        if (view == "side") { return new Vector2(p.Z, p.Y); }
        if (view == "back") { return new Vector2(-p.X, p.Y); }
        return new Vector2(p.X, p.Y);
    }

    private static float Depth(Vector3 p, string view)
    {
        if (view == "side") { return -p.X; }
        if (view == "back") { return -p.Z; }
        return p.Z;
    }

    private static Vector2 ToScreen(Vector3 p, string view, float scale, float ox, float oy)
    {
        Vector2 s = Project(p, view);
        return new Vector2(s.X * scale + ox, oy - s.Y * scale);
    }

    private static float Edge(Vector2 a, Vector2 b, Vector2 c)
    {
        return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    }

    private static Color Sample(Bitmap texture, Vector2 uv, bool clamp)
    {
        float u = uv.X;
        float v = uv.Y;
        if (clamp)
        {
            u = u < 0f ? 0f : (u > 1f ? 1f : u);
            v = v < 0f ? 0f : (v > 1f ? 1f : v);
        }
        else
        {
            u = u - (float)Math.Floor(u);
            v = v - (float)Math.Floor(v);
        }

        int x = (int)(u * (texture.Width - 1));
        int y = (int)(v * (texture.Height - 1));
        if (x < 0) x = 0; if (x >= texture.Width) x = texture.Width - 1;
        if (y < 0) y = 0; if (y >= texture.Height) y = texture.Height - 1;
        return texture.GetPixel(x, y);
    }

    private static int[] DefaultParents(Assembly mod)
    {
        Type avatar = Type.GetType("DNA.Avatars.Avatar, DNA.Common");
        object parents = avatar.GetProperty("DefaultParentBones", Hidden) != null
            ? avatar.GetProperty("DefaultParentBones", Hidden).GetValue(null, null)
            : avatar.GetField("DefaultParentBones", Hidden).GetValue(null);
        var list = (System.Collections.IEnumerable)parents;
        var result = new List<int>();
        foreach (object value in list) { result.Add(Convert.ToInt32(value)); }
        return result.ToArray();
    }

    private static object Field(Type type, object instance, string name)
    {
        FieldInfo field = type.GetField(name, Hidden);
        if (field != null) { return field.GetValue(instance); }
        PropertyInfo property = type.GetProperty(name, Hidden);
        return property == null ? null : property.GetValue(instance, null);
    }

    private static Type ModType(Assembly mod, string simpleName)
    {
        string[] namespaces = { "XboxAvatar", "OpenClassic.XboxAvatar" };
        foreach (string space in namespaces)
        {
            Type candidate = mod.GetType(space + "." + simpleName, false);
            if (candidate != null) { return candidate; }
        }
        return mod.GetType(namespaces[0] + "." + simpleName, true);
    }
}
