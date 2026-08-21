using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Xna.Framework;
using Color = System.Drawing.Color;

// Offline first-person hand renderer and analyser.
//
// The first-person hand can only be judged in the game, a hundred pixels
// across, in a pose the player cannot hold still. The runtime dumps what it
// drew (first-person-mesh.obj in world space, first-person-view.obj through
// the player's camera). Because first person skins every avatar vertex
// linearly against one matrix per bone, those two files are enough to recover
// the exact live bone matrices and the exact camera, after which the hand can
// be re-skinned, re-selected and re-rendered here at any size, with any
// colouring, without launching the game.
//
// Usage:
//   FirstPersonProbe <mod.dll> <avatar.ocavatar> <game folder> <dump folder> <out folder>
//       [--colour batch|bone|stretch|texture|depthfight]   (default texture)
//       [--source dump|reskin]                             (default reskin)
//       [--selection hand|skin|volume|all]                 (default hand)
//       [--side left|right|both]                           (default right)
//       [--size N] [--margin F] [--frame x0 y0 x1 y1|full]
//       [--pose <file>]   reuse a previously solved pose instead of the dump
internal static class FirstPersonProbe
{
    private const BindingFlags Hidden =
        BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private sealed class Batch
    {
        public string Name;
        public int Index;
        public Vector3[] Bind;
        public Vector3[] BindNormal;
        public byte[][] Bindings;
        public byte[][] Weights;
        public Vector2[] Uv;
        public Color[] VertexColor;
        public short[] Indices;
        public short[] FirstPersonIndices;
        public short[] MappedFirstPersonIndices;
        public short[] MappedFirstPersonHandIndices;
        public short[] MappedFirstPersonSkinIndices;
        public byte[] Sides;
        public bool[] Covered;
        public bool IsBaseBody;
        public bool IsHandComponent;
        public bool IsOverlayLayer;
        public bool HasFingerGeometry;
        public bool IsBareHandShell;
        public Bitmap Texture;
        public Vector3 Diffuse;
        public Vector3[] World;    // from the dump, null if the batch was not drawn
        public Vector3[] Screen;   // from the dump (x, y down, z)
        public Vector3[] Skinned;  // re-skinned with the solved matrices
    }

    private sealed class Asset
    {
        public object Raw;
        public Assembly Mod;
        public Matrix[] InverseBindPose;
        public Matrix[] BindPoseAbsolute;
        public Matrix[] SourcePoseLocal;
        public Vector3[] BoneScale;
        public int[] Parents;
        public List<Batch> Batches = new List<Batch>();
        public int BoneCount { get { return InverseBindPose.Length; } }
    }

    private static int Main(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine(
                "Usage: FirstPersonProbe <mod.dll> <avatar.ocavatar> <game folder> <dump folder> <out folder> [options]");
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
        try
        {
            return Run(args);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        string modPath = Path.GetFullPath(args[0]);
        string avatarPath = Path.GetFullPath(args[1]);
        string dumpFolder = Path.GetFullPath(args[3]);
        string outFolder = Path.GetFullPath(args[4]);
        Directory.CreateDirectory(outFolder);

        string colour = "texture";
        string source = "reskin";
        string selection = "hand";
        string side = "right";
        int size = 1024;
        float margin = 0.15f;
        float[] frame = null;
        bool fullFrame = false;
        string posePath = null;
        bool overlays = false;
        string hand = "live";
        float[] curl = { 42f, 55f, 35f, 18f, 28f, 16f };
        float curlSign = 1f;
        float leftSign = 1f;
        float grip = 0.5f;
        bool mirror = false;
        for (int i = 5; i + 1 < args.Length; i += 2)
        {
            string option = args[i].ToLowerInvariant();
            string value = args[i + 1];
            if (option == "--colour" || option == "--color") { colour = value.ToLowerInvariant(); }
            else if (option == "--source") { source = value.ToLowerInvariant(); }
            else if (option == "--selection") { selection = value.ToLowerInvariant(); }
            else if (option == "--side") { side = value.ToLowerInvariant(); }
            else if (option == "--size") { size = int.Parse(value, Invariant); }
            else if (option == "--margin") { margin = float.Parse(value, Invariant); }
            else if (option == "--pose") { posePath = value; }
            else if (option == "--overlays") { overlays = value == "1"; }
            else if (option == "--hand") { hand = value.ToLowerInvariant(); }
            else if (option == "--curlsign") { curlSign = float.Parse(value, Invariant); }
            else if (option == "--leftsign") { leftSign = float.Parse(value, Invariant); }
            else if (option == "--grip") { grip = float.Parse(value, Invariant); }
            else if (option == "--mirror") { mirror = value == "1"; }
            else if (option == "--curl")
            {
                string[] parts = value.Split(',');
                for (int k = 0; k < parts.Length && k < curl.Length; k++) { curl[k] = float.Parse(parts[k], Invariant); }
            }
            else if (option == "--frame")
            {
                if (value.ToLowerInvariant() == "full") { fullFrame = true; }
                else
                {
                    frame = new float[4];
                    frame[0] = float.Parse(value, Invariant);
                    frame[1] = float.Parse(args[i + 2], Invariant);
                    frame[2] = float.Parse(args[i + 3], Invariant);
                    frame[3] = float.Parse(args[i + 4], Invariant);
                    i += 3;
                }
            }
            else { throw new ArgumentException("Unknown option " + option); }
        }

        var report = new StringBuilder();
        Asset asset = LoadAsset(modPath, avatarPath);
        Log(report, "avatar " + avatarPath + " bones=" + asset.BoneCount + " batches=" + asset.Batches.Count);

        // ---- the dump ----------------------------------------------------
        ReadDump(asset, Path.Combine(dumpFolder, "first-person-mesh.obj"), false);
        ReadDump(asset, Path.Combine(dumpFolder, "first-person-view.obj"), true);
        int dumped = 0;
        foreach (Batch batch in asset.Batches) { if (batch.World != null) { dumped++; } }
        Log(report, "dumped batches=" + dumped);

        // ---- the camera ----------------------------------------------------
        Matrix camera = SolveCamera(asset, report);
        float aspect = CameraAspect(camera);
        Log(report, "camera aspect=" + aspect.ToString("F4", Invariant));

        // ---- the bones -----------------------------------------------------
        Matrix[] skin = SolveSkinMatrices(asset, report);
        Reskin(asset, skin);
        ValidateReskin(asset, report);
        ReportRig(asset, skin, report);
        WritePose(Path.Combine(outFolder, "first-person-pose.txt"), camera, skin);
        if (hand == "runtime")
        {
            // The runtime's own posing code, fed the live proxy bones the dump
            // gave us. This is the picture the new DLL will draw.
            Matrix[] solved = skin;
            skin = RuntimePose(asset, solved, grip);
            Reskin(asset, skin);
            Log(report, "hand re-posed by the runtime: grip=" + grip.ToString("F2", Invariant));
            // How far the runtime's answer is from this tool's own chaining at
            // the same curl, per bone, so a disagreement between the two
            // implementations cannot hide behind a picture that looks fine.
            var scaled = new float[curl.Length];
            for (int k = 0; k < curl.Length; k++) { scaled[k] = curl[k] * grip; }
            Matrix[] own = Repose(asset, solved, "curl", scaled, 1f, -1f, true, report);
            for (int bone = 0; bone < asset.BoneCount; bone++)
            {
                double angle = RotationBetween(skin[bone], own[bone]);
                float shift = Vector3.Distance(skin[bone].Translation, own[bone].Translation);
                if (angle > 0.5 || shift > 0.002f)
                {
                    Log(report, string.Format(Invariant, "  runtime vs tool bone {0,3}: rotation {1:F2} deg, translation {2:F4}", bone, angle, shift));
                }
            }
        }
        else if (hand != "live")
        {
            skin = Repose(asset, skin, hand, curl, curlSign, leftSign, mirror, report);
            Reskin(asset, skin);
            Log(report, "hand re-posed: " + hand + (mirror ? " (Z-reflected at the wrist)" : "") + (hand == "curl"
                ? string.Format(Invariant, " curl={0},{1},{2} thumb={3},{4},{5} sign={6} leftsign={7}",
                    curl[0], curl[1], curl[2], curl[3], curl[4], curl[5], curlSign, leftSign)
                : ""));
        }

        ReportFingertips(asset, report);

        // ---- the picture ---------------------------------------------------
        byte wantSide = side == "left" ? (byte)1 : side == "right" ? (byte)2 : (byte)0;
        // Texture mode needs the overlay passes, which carry the garment's
        // colour; the diagnostic colourings only want one copy of each surface.
        bool drawOverlays = overlays || colour == "texture";
        var triangles = CollectTriangles(asset, selection, wantSide, source == "dump", drawOverlays);
        Log(report, "triangles selected=" + triangles.Count + " selection=" + selection + " side=" + side + " source=" + source);
        AnalyseStretch(asset, triangles, report);
        AnalyseNearPlane(asset, triangles, camera, report);

        float[] window = fullFrame
            ? new[] { -1f, -1f, 1f, 1f }
            : frame ?? AutoFrame(triangles, camera, aspect, margin);
        Log(report, string.Format(Invariant,
            "frame x=[{0:F3},{1:F3}] y=[{2:F3},{3:F3}] (screen units, y down)",
            window[0], window[2], window[1], window[3]));

        string outPng = Path.Combine(outFolder, "first-person-" + colour + "-" + selection + "-" + side + "-" + hand + ".png");
        RenderResult result = Render(asset, triangles, camera, aspect, window, size, colour);
        result.Image.Save(outPng, ImageFormat.Png);
        result.Image.Dispose();
        Log(report, "wrote " + outPng);
        foreach (KeyValuePair<string, int> pixels in result.PixelsPerBatch)
        {
            Log(report, "  pixels " + pixels.Value.ToString().PadLeft(7) + " " + pixels.Key);
        }
        if (colour == "depthfight")
        {
            Log(report, "  glove pixels occluded by skin within 5mm: " + result.Occluded);
        }

        File.WriteAllText(Path.Combine(outFolder, "report.txt"), report.ToString());
        return 0;
    }

    private static void Log(StringBuilder report, string line)
    {
        Console.WriteLine(line);
        report.AppendLine(line);
    }

    // ------------------------------------------------------------------ asset

    private static Asset LoadAsset(string modPath, string avatarPath)
    {
        Assembly mod = Assembly.LoadFrom(modPath);
        Type assetType = ModType(mod, "AvatarAsset");
        object raw = assetType.GetMethod("Load", Hidden).Invoke(null, new object[] { avatarPath });
        var asset = new Asset();
        asset.Raw = raw;
        asset.Mod = mod;
        asset.InverseBindPose = (Matrix[])Field(assetType, raw, "InverseBindPose");
        asset.BindPoseAbsolute = (Matrix[])Field(assetType, raw, "BindPoseAbsolute");
        asset.SourcePoseLocal = (Matrix[])Field(assetType, raw, "SourcePoseLocal");
        asset.BoneScale = (Vector3[])Field(assetType, raw, "FirstPersonBoneScale");
        asset.Parents = DefaultParents();

        Array batches = (Array)Field(assetType, raw, "Batches");
        Type batchType = null;
        Type sourceType = null;
        Type drawType = null;
        int index = 0;
        foreach (object rawBatch in batches)
        {
            if (batchType == null) { batchType = rawBatch.GetType(); }
            var batch = new Batch();
            batch.Index = index++;
            batch.Name = (string)Field(batchType, rawBatch, "Name");
            batch.Indices = (short[])Field(batchType, rawBatch, "Indices");
            batch.FirstPersonIndices = (short[])Field(batchType, rawBatch, "FirstPersonIndices");
            batch.MappedFirstPersonIndices = (short[])Field(batchType, rawBatch, "MappedFirstPersonIndices");
            batch.MappedFirstPersonHandIndices = (short[])Field(batchType, rawBatch, "MappedFirstPersonHandIndices");
            batch.MappedFirstPersonSkinIndices = (short[])Field(batchType, rawBatch, "MappedFirstPersonSkinIndices");
            batch.Sides = (byte[])Field(batchType, rawBatch, "FirstPersonSides");
            batch.Covered = (bool[])Field(batchType, rawBatch, "CoveredByOuterHand");
            batch.IsBaseBody = (bool)Field(batchType, rawBatch, "IsBaseBody");
            batch.IsHandComponent = (bool)Field(batchType, rawBatch, "IsHandComponent");
            batch.IsOverlayLayer = (bool)Field(batchType, rawBatch, "IsOverlayLayer");
            batch.HasFingerGeometry = (bool)Field(batchType, rawBatch, "HasFingerGeometry");
            batch.IsBareHandShell = (bool)Field(batchType, rawBatch, "IsBareHandShell");
            batch.Diffuse = (Vector3)Field(batchType, rawBatch, "DiffuseColor");
            byte[] png = (byte[])Field(batchType, rawBatch, "TexturePng");
            if (png != null && png.Length > 0)
            {
                using (var stream = new MemoryStream(png))
                {
                    batch.Texture = new Bitmap(Image.FromStream(stream));
                }
            }

            Array sourceVertices = (Array)Field(batchType, rawBatch, "SourceVertices");
            Array drawVertices = (Array)Field(batchType, rawBatch, "DrawVertices");
            int count = sourceVertices.Length;
            batch.Bind = new Vector3[count];
            batch.BindNormal = new Vector3[count];
            batch.Bindings = new byte[count][];
            batch.Weights = new byte[count][];
            batch.Uv = new Vector2[count];
            batch.VertexColor = new Color[count];
            for (int v = 0; v < count; v++)
            {
                object sv = sourceVertices.GetValue(v);
                if (sourceType == null) { sourceType = sv.GetType(); }
                batch.Bind[v] = (Vector3)Field(sourceType, sv, "Position");
                batch.BindNormal[v] = (Vector3)Field(sourceType, sv, "Normal");
                batch.Bindings[v] = (byte[])Field(sourceType, sv, "Bindings");
                batch.Weights[v] = (byte[])Field(sourceType, sv, "Weights");
                object dv = drawVertices.GetValue(v);
                if (drawType == null) { drawType = dv.GetType(); }
                batch.Uv[v] = (Vector2)Field(drawType, dv, "TextureCoordinate");
                var xnaColour = (Microsoft.Xna.Framework.Color)Field(drawType, dv, "Color");
                batch.VertexColor[v] = Color.FromArgb(xnaColour.A, xnaColour.R, xnaColour.G, xnaColour.B);
            }
            asset.Batches.Add(batch);
        }
        return asset;
    }

    private static int[] DefaultParents()
    {
        Type avatar = Type.GetType("DNA.Avatars.Avatar, DNA.Common");
        object parents = avatar.GetProperty("DefaultParentBones", Hidden) != null
            ? avatar.GetProperty("DefaultParentBones", Hidden).GetValue(null, null)
            : avatar.GetField("DefaultParentBones", Hidden).GetValue(null);
        var result = new List<int>();
        foreach (object value in (System.Collections.IEnumerable)parents)
        {
            result.Add(Convert.ToInt32(value));
        }
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

    // ------------------------------------------------------------------- dump

    private static void ReadDump(Asset asset, string path, bool screen)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Missing dump", path);
        }
        Batch current = null;
        var positions = new List<Vector3>();
        Action flush = delegate
        {
            if (current == null) { return; }
            if (positions.Count != current.Bind.Length)
            {
                throw new InvalidDataException(
                    "Dump group " + current.Name + " has " + positions.Count +
                    " vertices, the asset has " + current.Bind.Length);
            }
            if (screen) { current.Screen = positions.ToArray(); }
            else { current.World = positions.ToArray(); }
            positions.Clear();
        };
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') { continue; }
            string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts[0] == "g")
            {
                flush();
                current = null;
                foreach (Batch batch in asset.Batches)
                {
                    if (batch.Name == parts[1]) { current = batch; break; }
                }
                if (current == null)
                {
                    throw new InvalidDataException("Dump group " + parts[1] + " is not a batch of this avatar.");
                }
            }
            else if (parts[0] == "v")
            {
                positions.Add(new Vector3(
                    float.Parse(parts[1], Invariant),
                    float.Parse(parts[2], Invariant),
                    float.Parse(parts[3], Invariant)));
            }
        }
        flush();
    }

    // ----------------------------------------------------------------- camera

    /// <summary>
    /// The view*projection matrix, from world/screen pairs. Screen y in the
    /// dump is -clip.Y/w; the matrix solved here reproduces exactly the dump's
    /// convention, so clip = [world 1] * camera and screen = (x/w, y/w, z/w)
    /// with y already pointing down.
    /// </summary>
    private static Matrix SolveCamera(Asset asset, StringBuilder report)
    {
        var rows = new List<double[]>();
        foreach (Batch batch in asset.Batches)
        {
            if (batch.World == null || batch.Screen == null) { continue; }
            for (int v = 0; v < batch.World.Length; v++)
            {
                Vector3 s = batch.Screen[v];
                if (float.IsNaN(s.X) || float.IsInfinity(s.X) ||
                    Math.Abs(s.X) > 8f || Math.Abs(s.Y) > 8f || Math.Abs(s.Z) > 8f)
                {
                    continue;
                }
                Vector3 w = batch.World[v];
                double[] X = { w.X, w.Y, w.Z, 1.0 };
                // columns 0..3 of the matrix are unknown groups c0,c1,c2,c3 (4 each)
                rows.Add(Homogeneous(X, 0, s.X));
                rows.Add(Homogeneous(X, 1, s.Y));
                rows.Add(Homogeneous(X, 2, s.Z));
            }
        }
        if (rows.Count < 32)
        {
            throw new InvalidDataException("Not enough on-screen vertices to solve the camera.");
        }
        var ata = new double[16, 16];
        foreach (double[] row in rows)
        {
            for (int a = 0; a < 16; a++)
            {
                if (row[a] == 0) { continue; }
                for (int b = 0; b < 16; b++) { ata[a, b] += row[a] * row[b]; }
            }
        }
        double[] p = SmallestEigenvector(ata);
        var camera = new Matrix(
            (float)p[0], (float)p[1], (float)p[2], (float)p[3],
            (float)p[4], (float)p[5], (float)p[6], (float)p[7],
            (float)p[8], (float)p[9], (float)p[10], (float)p[11],
            (float)p[12], (float)p[13], (float)p[14], (float)p[15]);
        // Sign: a point in front of the camera must have w > 0.
        int positive = 0, negative = 0;
        foreach (Batch batch in asset.Batches)
        {
            if (batch.World == null) { continue; }
            for (int v = 0; v < batch.World.Length; v++)
            {
                if (batch.Screen[v].Z < 0f || batch.Screen[v].Z > 1f) { continue; }
                Vector4 clip = Vector4.Transform(new Vector4(batch.World[v], 1f), camera);
                if (clip.W > 0) { positive++; } else { negative++; }
            }
        }
        if (negative > positive) { camera = camera * -1f; }

        // Residual against the dump.
        double worst = 0, sum = 0; int counted = 0;
        foreach (Batch batch in asset.Batches)
        {
            if (batch.World == null) { continue; }
            for (int v = 0; v < batch.World.Length; v++)
            {
                Vector3 s = batch.Screen[v];
                if (Math.Abs(s.X) > 2f || Math.Abs(s.Y) > 2f || s.Z < 0f || s.Z > 1f) { continue; }
                Vector3 back = ToScreen(batch.World[v], camera);
                double err = Math.Sqrt(
                    (back.X - s.X) * (back.X - s.X) +
                    (back.Y - s.Y) * (back.Y - s.Y) +
                    (back.Z - s.Z) * (back.Z - s.Z));
                if (err > worst) { worst = err; }
                sum += err; counted++;
            }
        }
        Log(report, string.Format(Invariant,
            "camera solved from {0} rows: on-screen reprojection mean={1:E3} worst={2:E3} over {3} vertices",
            rows.Count, counted > 0 ? sum / counted : 0, worst, counted));
        return camera;
    }

    private static double[] Homogeneous(double[] X, int axis, double s)
    {
        // [X]·col(axis) - s * [X]·col(3) = 0 ; columns are interleaved in a
        // row-major 4x4, so entry (r, c) is index r*4 + c.
        var row = new double[16];
        for (int r = 0; r < 4; r++)
        {
            row[r * 4 + axis] += X[r];
            row[r * 4 + 3] -= s * X[r];
        }
        return row;
    }

    private static float CameraAspect(Matrix camera)
    {
        // camera = view * projection; the view is rigid, so the lengths of the
        // first two columns' linear parts are the projection's M11 and M22.
        double c0 = Math.Sqrt(camera.M11 * camera.M11 + camera.M21 * camera.M21 + camera.M31 * camera.M31);
        double c1 = Math.Sqrt(camera.M12 * camera.M12 + camera.M22 * camera.M22 + camera.M32 * camera.M32);
        return c0 > 1e-9 ? (float)(c1 / c0) : 1f;
    }

    private static Vector3 ToScreen(Vector3 world, Matrix camera)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), camera);
        float w = Math.Abs(clip.W) < 1e-6f ? 1e-6f : clip.W;
        return new Vector3(clip.X / w, clip.Y / w, clip.Z / w);
    }

    private static double[] SmallestEigenvector(double[,] a)
    {
        int n = a.GetLength(0);
        var m = (double[,])a.Clone();
        var v = new double[n, n];
        for (int i = 0; i < n; i++) { v[i, i] = 1; }
        for (int sweep = 0; sweep < 100; sweep++)
        {
            double off = 0;
            for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) { off += m[i, j] * m[i, j]; }
            if (off < 1e-30) { break; }
            for (int p = 0; p < n; p++)
            {
                for (int q = p + 1; q < n; q++)
                {
                    if (Math.Abs(m[p, q]) < 1e-300) { continue; }
                    double theta = (m[q, q] - m[p, p]) / (2 * m[p, q]);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) { t = 1; }
                    double c = 1 / Math.Sqrt(t * t + 1);
                    double s = t * c;
                    for (int k = 0; k < n; k++)
                    {
                        double mkp = m[k, p], mkq = m[k, q];
                        m[k, p] = c * mkp - s * mkq;
                        m[k, q] = s * mkp + c * mkq;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double mpk = m[p, k], mqk = m[q, k];
                        m[p, k] = c * mpk - s * mqk;
                        m[q, k] = s * mpk + c * mqk;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq;
                        v[k, q] = s * vkp + c * vkq;
                    }
                }
            }
        }
        int best = 0;
        for (int i = 1; i < n; i++) { if (m[i, i] < m[best, best]) { best = i; } }
        var result = new double[n];
        double norm = 0;
        for (int i = 0; i < n; i++) { result[i] = v[i, best]; norm += result[i] * result[i]; }
        norm = Math.Sqrt(norm);
        for (int i = 0; i < n; i++) { result[i] /= norm; }
        return result;
    }

    // ------------------------------------------------------------------ bones

    private static float[] NormalisedWeights(Asset asset, Batch batch, int v)
    {
        var result = new float[4];
        float total = 0f;
        for (int i = 0; i < 4; i++)
        {
            float w = batch.Weights[v][i] / 255f;
            int bone = batch.Bindings[v][i];
            if (w <= 0f || bone < 0 || bone >= asset.BoneCount) { continue; }
            result[i] = w;
            total += w;
        }
        if (total <= 0.0001f) { return null; }
        if (Math.Abs(total - 1f) > 0.0001f)
        {
            for (int i = 0; i < 4; i++) { result[i] /= total; }
        }
        return result;
    }

    /// <summary>
    /// One 4x4 skin matrix per bone (world = [bind 1] * M[bone]) recovered
    /// by least squares from every dumped vertex. The three axes share the
    /// system matrix and differ only in the right-hand side.
    /// </summary>
    private static Matrix[] SolveSkinMatrices(Asset asset, StringBuilder report)
    {
        int bones = asset.BoneCount;
        int n = bones * 4;
        var ata = new double[n, n];
        var atb = new double[n, 3];
        var weightSum = new double[bones];
        var vertexCount = new int[bones];
        var row = new double[n];
        var touched = new List<int>();
        foreach (Batch batch in asset.Batches)
        {
            if (batch.World == null) { continue; }
            for (int v = 0; v < batch.World.Length; v++)
            {
                float[] weights = NormalisedWeights(asset, batch, v);
                if (weights == null) { continue; }
                Array.Clear(row, 0, n);
                touched.Clear();
                Vector3 p = batch.Bind[v];
                for (int i = 0; i < 4; i++)
                {
                    if (weights[i] <= 0f) { continue; }
                    int bone = batch.Bindings[v][i];
                    int b = bone * 4;
                    row[b] += weights[i] * p.X;
                    row[b + 1] += weights[i] * p.Y;
                    row[b + 2] += weights[i] * p.Z;
                    row[b + 3] += weights[i];
                    weightSum[bone] += weights[i];
                    vertexCount[bone]++;
                    for (int k = 0; k < 4; k++) { touched.Add(b + k); }
                }
                Vector3 w = batch.World[v];
                foreach (int a in touched)
                {
                    foreach (int b in touched) { ata[a, b] += row[a] * row[b]; }
                    atb[a, 0] += row[a] * w.X;
                    atb[a, 1] += row[a] * w.Y;
                    atb[a, 2] += row[a] * w.Z;
                }
            }
        }
        // Ridge for bones no vertex constrains, so the system stays solvable.
        double ridge = 1e-9;
        for (int i = 0; i < n; i++) { ata[i, i] += ridge; }
        double[][] solution = SolveMultiple(ata, atb, n, 3);

        var result = new Matrix[bones];
        for (int bone = 0; bone < bones; bone++)
        {
            int b = bone * 4;
            result[bone] = new Matrix(
                (float)solution[0][b], (float)solution[1][b], (float)solution[2][b], 0f,
                (float)solution[0][b + 1], (float)solution[1][b + 1], (float)solution[2][b + 1], 0f,
                (float)solution[0][b + 2], (float)solution[1][b + 2], (float)solution[2][b + 2], 0f,
                (float)solution[0][b + 3], (float)solution[1][b + 3], (float)solution[2][b + 3], 1f);
        }
        int constrained = 0;
        for (int bone = 0; bone < bones; bone++) { if (weightSum[bone] >= 2.0) { constrained++; } }
        Log(report, "bones solved: " + constrained + "/" + bones + " have at least 2.0 total weight in the dump");
        var line = new StringBuilder("  bone weight sums:");
        for (int bone = 0; bone < bones; bone++)
        {
            if (weightSum[bone] > 0) { line.Append(' ').Append(bone).Append('=').Append(weightSum[bone].ToString("F1", Invariant)); }
        }
        Log(report, line.ToString());
        return result;
    }

    private static double[][] SolveMultiple(double[,] a, double[,] b, int n, int rhs)
    {
        var m = (double[,])a.Clone();
        var r = (double[,])b.Clone();
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int i = col + 1; i < n; i++) { if (Math.Abs(m[i, col]) > Math.Abs(m[pivot, col])) { pivot = i; } }
            if (pivot != col)
            {
                for (int k = 0; k < n; k++) { double t = m[col, k]; m[col, k] = m[pivot, k]; m[pivot, k] = t; }
                for (int k = 0; k < rhs; k++) { double t = r[col, k]; r[col, k] = r[pivot, k]; r[pivot, k] = t; }
            }
            double d = m[col, col];
            if (Math.Abs(d) < 1e-300) { continue; }
            for (int i = col + 1; i < n; i++)
            {
                double f = m[i, col] / d;
                if (f == 0) { continue; }
                for (int k = col; k < n; k++) { m[i, k] -= f * m[col, k]; }
                for (int k = 0; k < rhs; k++) { r[i, k] -= f * r[col, k]; }
            }
        }
        var x = new double[rhs][];
        for (int k = 0; k < rhs; k++)
        {
            x[k] = new double[n];
            for (int i = n - 1; i >= 0; i--)
            {
                double s = r[i, k];
                for (int j = i + 1; j < n; j++) { s -= m[i, j] * x[k][j]; }
                x[k][i] = Math.Abs(m[i, i]) < 1e-300 ? 0 : s / m[i, i];
            }
        }
        return x;
    }

    private static void Reskin(Asset asset, Matrix[] skin)
    {
        foreach (Batch batch in asset.Batches)
        {
            batch.Skinned = new Vector3[batch.Bind.Length];
            for (int v = 0; v < batch.Bind.Length; v++)
            {
                float[] weights = NormalisedWeights(asset, batch, v);
                if (weights == null) { batch.Skinned[v] = batch.Bind[v]; continue; }
                Vector3 p = Vector3.Zero;
                for (int i = 0; i < 4; i++)
                {
                    if (weights[i] <= 0f) { continue; }
                    p += Vector3.Transform(batch.Bind[v], skin[batch.Bindings[v][i]]) * weights[i];
                }
                batch.Skinned[v] = p;
            }
        }
    }

    private static void ValidateReskin(Asset asset, StringBuilder report)
    {
        double worst = 0, sum = 0; int counted = 0; string worstWhere = "";
        foreach (Batch batch in asset.Batches)
        {
            if (batch.World == null) { continue; }
            for (int v = 0; v < batch.World.Length; v++)
            {
                double err = Vector3.Distance(batch.World[v], batch.Skinned[v]);
                if (err > worst) { worst = err; worstWhere = batch.Name + "[" + v + "]"; }
                sum += err; counted++;
            }
        }
        Log(report, string.Format(Invariant,
            "reskin against dump: mean={0:E3} worst={1:E3} at {2} over {3} vertices",
            counted > 0 ? sum / counted : 0, worst, worstWhere, counted));
    }

    private static void ReportRig(Asset asset, Matrix[] skin, StringBuilder report)
    {
        // skin = inverseBind * scale * target  =>  target = (inverseBind*scale)^-1 * skin
        Log(report, "rig (bones the dump constrains): bone parent | bind->parent length | live->parent length | ratio");
        var live = new Matrix[asset.BoneCount];
        var known = new bool[asset.BoneCount];
        var weightSum = new double[asset.BoneCount];
        foreach (Batch batch in asset.Batches)
        {
            if (batch.World == null) { continue; }
            for (int v = 0; v < batch.World.Length; v++)
            {
                float[] w = NormalisedWeights(asset, batch, v);
                if (w == null) { continue; }
                for (int i = 0; i < 4; i++) { if (w[i] > 0) { weightSum[batch.Bindings[v][i]] += w[i]; } }
            }
        }
        for (int bone = 0; bone < asset.BoneCount; bone++)
        {
            if (weightSum[bone] < 2.0) { continue; }
            Matrix pre = asset.InverseBindPose[bone] * Matrix.CreateScale(asset.BoneScale[bone]);
            live[bone] = Matrix.Invert(pre) * skin[bone];
            known[bone] = true;
        }
        Log(report, "motion (skin matrix = bind-to-live motion of each bone): bone | angle to parent's motion | angle to wrist's motion");
        for (int bone = 0; bone < asset.BoneCount; bone++)
        {
            if (!known[bone]) { continue; }
            int parent = asset.Parents[bone];
            int wrist = IsUnder(asset, bone, 33) ? 33 : IsUnder(asset, bone, 36) ? 36 : -1;
            if (wrist < 0 || bone == wrist) { continue; }
            string toParent = parent >= 0 && known[parent]
                ? RotationBetween(skin[bone], skin[parent]).ToString("F1", Invariant) : "?";
            string toWrist = known[wrist]
                ? RotationBetween(skin[bone], skin[wrist]).ToString("F1", Invariant) : "?";
            // The hinge: the parent-relative rotation's axis, in the bone's own
            // bind-local frame. A finger curl is a rotation about local Z.
            string axisText = "";
            if (parent >= 0 && known[parent])
            {
                Matrix relative = live[bone] * Matrix.Invert(live[parent]);
                Matrix bindRelative = asset.BindPoseAbsolute[bone] * Matrix.Invert(asset.BindPoseAbsolute[parent]);
                // motion of the bone relative to the parent, compared with bind
                Matrix motion = Matrix.Invert(bindRelative) * relative;
                Vector3 s; Quaternion q; Vector3 t;
                if (motion.Decompose(out s, out q, out t))
                {
                    q.Normalize();
                    Vector3 axis = new Vector3(q.X, q.Y, q.Z);
                    if (axis.Length() > 1e-6f)
                    {
                        axis.Normalize();
                        // into the bone's bind-local frame
                        Matrix bindRotation = asset.BindPoseAbsolute[bone];
                        bindRotation.Translation = Vector3.Zero;
                        Vector3 localAxis = Vector3.TransformNormal(axis, Matrix.Invert(bindRotation));
                        axisText = string.Format(Invariant, " axis=({0:F2},{1:F2},{2:F2}) offset={3:F4}",
                            localAxis.X, localAxis.Y, localAxis.Z, t.Length());
                    }
                }
            }
            Log(report, string.Format(Invariant, "  bone {0,3} parent {1,3} | {2,6} deg | {3,6} deg{4}", bone, parent, toParent, toWrist, axisText));
        }
        for (int bone = 0; bone < asset.BoneCount; bone++)
        {
            if (!known[bone]) { continue; }
            int parent = asset.Parents[bone];
            string parentText = "-";
            if (parent >= 0 && known[parent])
            {
                float bindLength = Vector3.Distance(
                    asset.BindPoseAbsolute[bone].Translation, asset.BindPoseAbsolute[parent].Translation);
                float liveLength = Vector3.Distance(live[bone].Translation, live[parent].Translation);
                parentText = string.Format(Invariant, "{0,3} | {1:F4} | {2:F4} | {3:F3}",
                    parent, bindLength, liveLength, bindLength > 1e-6f ? liveLength / bindLength : 0f);
            }
            Vector3 scale; Quaternion rotation; Vector3 translation;
            string det = live[bone].Decompose(out scale, out rotation, out translation)
                ? string.Format(Invariant, "scale=({0:F3},{1:F3},{2:F3})", scale.X, scale.Y, scale.Z)
                : "scale=?";
            Log(report, string.Format(Invariant, "  bone {0,3} w={1,7:F1} {2} live=({3:F3},{4:F3},{5:F3}) {6}",
                bone, weightSum[bone], parentText,
                live[bone].Translation.X, live[bone].Translation.Y, live[bone].Translation.Z, det));
        }
    }

    /// <summary>
    /// Live world matrix of a bone, undone from its skin matrix:
    /// skin = inverseBind * shape * live.
    /// </summary>
    private static Matrix LiveBone(Asset asset, Matrix[] skin, int bone)
    {
        Matrix pre = asset.InverseBindPose[bone] * Matrix.CreateScale(asset.BoneScale[bone]);
        return Matrix.Invert(pre) * skin[bone];
    }

    private static float CurlFor(int bone, float[] curl)
    {
        if ((bone >= 37 && bone <= 40) || (bone >= 44 && bone <= 47)) { return curl[0]; }
        if ((bone >= 51 && bone <= 54) || (bone >= 56 && bone <= 59)) { return curl[1]; }
        if ((bone >= 61 && bone <= 64) || (bone >= 66 && bone <= 69)) { return curl[2]; }
        if (bone == 43 || bone == 50) { return curl[3]; }
        if (bone == 55 || bone == 60) { return curl[4]; }
        if (bone == 65 || bone == 70) { return curl[5]; }
        return 0f;
    }

    /// <summary>
    /// Re-pose everything below each wrist from the avatar's own local bind
    /// transforms, chained from the live wrist, the way third person poses a
    /// hand - optionally curling each finger joint about its local Z hinge.
    /// The wrist itself, and everything above it, keeps its live matrix.
    /// </summary>
    private static Matrix[] RuntimePose(Asset asset, Matrix[] skin, float grip)
    {
        var proxyWorld = new Matrix[asset.BoneCount];
        var identity = new int[asset.BoneCount];
        for (int bone = 0; bone < asset.BoneCount; bone++)
        {
            proxyWorld[bone] = LiveBone(asset, skin, bone);
            identity[bone] = bone;
        }
        Type entity = ModType(asset.Mod, "ImportedAvatarModelEntity");
        MethodInfo build = entity.GetMethod("BuildFirstPersonSkinTransforms", Hidden);
        if (build == null)
        {
            throw new MissingMethodException("This runtime has no BuildFirstPersonSkinTransforms; build the fixed runtime first.");
        }
        var result = new Matrix[asset.BoneCount];
        var scratch = new Matrix[asset.BoneCount];
        build.Invoke(null, new object[] { asset.Raw, proxyWorld, identity, grip, result, scratch });
        return result;
    }

    private static Matrix[] Repose(Asset asset, Matrix[] skin, string mode, float[] curl, float sign, float leftSign, bool mirror, StringBuilder report)
    {
        var result = (Matrix[])skin.Clone();
        var live = new Matrix[asset.BoneCount];
        var proxy = new Matrix[asset.BoneCount];
        Matrix flip = Matrix.CreateScale(1f, 1f, -1f);
        for (int bone = 0; bone < asset.BoneCount; bone++)
        {
            proxy[bone] = LiveBone(asset, skin, bone);
            // Xbox-to-XNA handedness, applied in every pinned bone's frame, as
            // the runtime does; the hand chains from the mirrored wrist.
            live[bone] = mirror ? flip * proxy[bone] : proxy[bone];
            if (mirror)
            {
                result[bone] = asset.InverseBindPose[bone] * Matrix.CreateScale(asset.BoneScale[bone]) * live[bone];
            }
        }
        foreach (int wrist in new[] { 33, 36 })
        {
            for (int bone = 0; bone < asset.BoneCount; bone++)
            {
                if (bone == wrist || !IsUnder(asset, bone, wrist)) { continue; }
                int parent = asset.Parents[bone];
                Matrix local = asset.SourcePoseLocal[bone];
                if (mode == "proxylocal")
                {
                    // The proxy's own joint rotation, relative to its parent,
                    // on the avatar's own bone offsets - what third person does
                    // with every animated bone. Conjugated into the avatar's
                    // handedness when the chain is mirrored.
                    Matrix proxyLocal = proxy[bone] * Matrix.Invert(proxy[parent]);
                    if (mirror) { proxyLocal = flip * proxyLocal * flip; }
                    Vector3 ps; Quaternion pr; Vector3 pt;
                    if (proxyLocal.Decompose(out ps, out pr, out pt))
                    {
                        Vector3 ss; Quaternion sr; Vector3 st;
                        if (!local.Decompose(out ss, out sr, out st)) { ss = Vector3.One; sr = Quaternion.Identity; }
                        Quaternion blended = Quaternion.Slerp(sr, pr, curl[0] / 42f);
                        Matrix retargeted = Matrix.CreateScale(ss) * Matrix.CreateFromQuaternion(blended);
                        retargeted.Translation = local.Translation;
                        local = retargeted;
                    }
                }
                else if (mode == "curl")
                {
                    float degrees = CurlFor(bone, curl) * sign * (wrist == 33 ? leftSign : 1f);
                    if (degrees != 0f)
                    {
                        local = Matrix.CreateRotationZ(MathHelper.ToRadians(degrees)) * local;
                    }
                }
                live[bone] = local * live[parent];
                result[bone] = asset.InverseBindPose[bone] * Matrix.CreateScale(asset.BoneScale[bone]) * live[bone];
            }
        }
        return result;
    }

    private static bool IsUnder(Asset asset, int bone, int ancestor)
    {
        while (bone >= 0 && bone < asset.Parents.Length)
        {
            if (bone == ancestor) { return true; }
            bone = asset.Parents[bone];
        }
        return false;
    }

    /// <summary>Angle in degrees between the rotations of two rigid motions.</summary>
    private static double RotationBetween(Matrix a, Matrix b)
    {
        Vector3 sa, sb, ta, tb; Quaternion qa, qb;
        if (!a.Decompose(out sa, out qa, out ta) || !b.Decompose(out sb, out qb, out tb)) { return double.NaN; }
        Quaternion d = Quaternion.Concatenate(qa, Quaternion.Inverse(qb));
        d.Normalize();
        double w = Math.Min(1.0, Math.Abs(d.W));
        return 2.0 * Math.Acos(w) * 180.0 / Math.PI;
    }

    private static void WritePose(string path, Matrix camera, Matrix[] skin)
    {
        var text = new StringBuilder();
        text.AppendLine("# solved first-person pose: camera (view*projection, screen y down) then one skin matrix per bone");
        text.AppendLine("camera " + MatrixText(camera));
        for (int bone = 0; bone < skin.Length; bone++)
        {
            text.AppendLine("bone " + bone + " " + MatrixText(skin[bone]));
        }
        File.WriteAllText(path, text.ToString());
    }

    private static string MatrixText(Matrix m)
    {
        float[] e =
        {
            m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44
        };
        var parts = new string[16];
        for (int i = 0; i < 16; i++) { parts[i] = e[i].ToString("R", Invariant); }
        return string.Join(" ", parts);
    }

    // -------------------------------------------------------------- triangles

    private sealed class Tri
    {
        public Batch Batch;
        public int A, B, C;
        public Vector3 Wa, Wb, Wc;
        public float Stretch;
    }

    private static List<Tri> CollectTriangles(Asset asset, string selection, byte side, bool fromDump, bool overlays)
    {
        var result = new List<Tri>();
        foreach (Batch batch in asset.Batches)
        {
            if (!overlays && batch.IsOverlayLayer) { continue; }
            short[] indices =
                selection == "hand" ? batch.MappedFirstPersonHandIndices :
                selection == "skin" ? batch.MappedFirstPersonSkinIndices :
                selection == "volume" ? batch.FirstPersonIndices :
                selection == "arm" ? batch.MappedFirstPersonIndices :
                batch.Indices;
            if (indices == null || indices.Length < 3) { continue; }
            Vector3[] positions = fromDump ? batch.World : batch.Skinned;
            if (positions == null) { continue; }
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                int a = (ushort)indices[t], b = (ushort)indices[t + 1], c = (ushort)indices[t + 2];
                if (side != 0 &&
                    batch.Sides[a] != side && batch.Sides[b] != side && batch.Sides[c] != side)
                {
                    continue;
                }
                var tri = new Tri { Batch = batch, A = a, B = b, C = c };
                tri.Wa = positions[a]; tri.Wb = positions[b]; tri.Wc = positions[c];
                float bindAb = Vector3.Distance(batch.Bind[a], batch.Bind[b]);
                float bindBc = Vector3.Distance(batch.Bind[b], batch.Bind[c]);
                float bindCa = Vector3.Distance(batch.Bind[c], batch.Bind[a]);
                float liveAb = Vector3.Distance(tri.Wa, tri.Wb);
                float liveBc = Vector3.Distance(tri.Wb, tri.Wc);
                float liveCa = Vector3.Distance(tri.Wc, tri.Wa);
                // Relative to the avatar's own build scale, which every edge
                // carries, so 1.0 means "as authored" for a tall avatar too.
                float build = asset.BoneScale[36].Y > 0.01f ? asset.BoneScale[36].Y : 1f;
                tri.Stretch = Math.Max(Ratio(liveAb, bindAb), Math.Max(Ratio(liveBc, bindBc), Ratio(liveCa, bindCa))) / build;
                result.Add(tri);
            }
        }
        return result;
    }

    private static float Ratio(float live, float bind)
    {
        return bind > 1e-5f ? live / bind : 1f;
    }

    private static void AnalyseStretch(Asset asset, List<Tri> triangles, StringBuilder report)
    {
        var perBatch = new Dictionary<string, int[]>();
        var worst = new List<Tri>();
        foreach (Tri tri in triangles)
        {
            int[] counts;
            if (!perBatch.TryGetValue(tri.Batch.Name, out counts)) { counts = new int[4]; perBatch[tri.Batch.Name] = counts; }
            counts[0]++;
            if (tri.Stretch > 1.3f) { counts[1]++; }
            if (tri.Stretch > 1.8f) { counts[2]++; }
            if (tri.Stretch > 2.5f) { counts[3]++; }
            if (tri.Stretch > 1.3f) { worst.Add(tri); }
        }
        Log(report, "stretch (live edge / bind edge): batch | triangles | >1.3 | >1.8 | >2.5");
        foreach (KeyValuePair<string, int[]> entry in perBatch)
        {
            Log(report, string.Format("  {0,-70} {1,6} {2,6} {3,6} {4,6}", entry.Key, entry.Value[0], entry.Value[1], entry.Value[2], entry.Value[3]));
        }
        // Which bone pairs the stretched triangles straddle: the tear is
        // always between two bones that moved differently.
        var pairs = new Dictionary<string, int>();
        foreach (Tri tri in worst)
        {
            if (tri.Batch.IsOverlayLayer) { continue; }
            int a = DominantBone(tri.Batch, tri.A), b = DominantBone(tri.Batch, tri.B), c = DominantBone(tri.Batch, tri.C);
            int lo = Math.Min(a, Math.Min(b, c)), hi = Math.Max(a, Math.Max(b, c));
            string key = lo + "-" + hi;
            int count;
            pairs.TryGetValue(key, out count);
            pairs[key] = count + 1;
        }
        var pairText = new StringBuilder("  stretched triangles by dominant-bone span:");
        foreach (KeyValuePair<string, int> pair in pairs) { pairText.Append(' ').Append(pair.Key).Append('=').Append(pair.Value); }
        Log(report, pairText.ToString());
        worst.Sort(delegate(Tri x, Tri y) { return y.Stretch.CompareTo(x.Stretch); });
        for (int i = 0; i < Math.Min(12, worst.Count); i++)
        {
            Tri tri = worst[i];
            Log(report, string.Format(Invariant, "  stretched x{0:F2} {1} tri({2},{3},{4}) bones {5} | {6} | {7}",
                tri.Stretch, Short(tri.Batch.Name), tri.A, tri.B, tri.C,
                BonesText(tri.Batch, tri.A), BonesText(tri.Batch, tri.B), BonesText(tri.Batch, tri.C)));
        }
    }

    /// <summary>
    /// Where each fingertip ends up in the current pose against where the game's
    /// own pose put it. The proxy fist is the authored grip, so whichever curl
    /// direction lands the tips nearer to it is the direction that closes the
    /// hand around the item; a picture cannot tell the palm from the back of a
    /// hand reliably, this can.
    /// </summary>
    private static void ReportFingertips(Asset asset, StringBuilder report)
    {
        int[] tips = { 61, 62, 63, 64, 65, 66, 67, 68, 69, 70 };
        Log(report, "fingertips: bone | vertices | distance from the game's own pose | distance from bind-relative-to-wrist");
        foreach (int tip in tips)
        {
            Vector3 live = Vector3.Zero, posed = Vector3.Zero;
            int count = 0;
            foreach (Batch batch in asset.Batches)
            {
                if (batch.World == null || batch.IsOverlayLayer) { continue; }
                for (int v = 0; v < batch.Bind.Length; v++)
                {
                    if (DominantBone(batch, v) != tip) { continue; }
                    live += batch.World[v];
                    posed += batch.Skinned[v];
                    count++;
                }
            }
            if (count == 0) { continue; }
            live /= count; posed /= count;
            Log(report, string.Format(Invariant, "  tip {0,2} | {1,4} | {2:F4} m", tip, count, Vector3.Distance(live, posed)));
        }
    }

    private static int DominantBone(Batch batch, int v)
    {
        int dominant = 0;
        for (int i = 1; i < 4; i++) { if (batch.Weights[v][i] > batch.Weights[v][dominant]) { dominant = i; } }
        return batch.Bindings[v][dominant];
    }

    private static string Short(string name)
    {
        int colon = name.IndexOf(':');
        return colon > 8 ? name.Substring(0, 8) + name.Substring(colon) : name;
    }

    private static string BonesText(Batch batch, int v)
    {
        var text = new StringBuilder();
        for (int i = 0; i < 4; i++)
        {
            if (batch.Weights[v][i] == 0) { continue; }
            if (text.Length > 0) { text.Append(','); }
            text.Append(batch.Bindings[v][i]).Append(':').Append(batch.Weights[v][i]);
        }
        return text.ToString();
    }

    private static void AnalyseNearPlane(Asset asset, List<Tri> triangles, Matrix camera, StringBuilder report)
    {
        int behind = 0, nearClipped = 0, total = 0;
        foreach (Tri tri in triangles)
        {
            foreach (Vector3 p in new[] { tri.Wa, tri.Wb, tri.Wc })
            {
                total++;
                Vector4 clip = Vector4.Transform(new Vector4(p, 1f), camera);
                if (clip.W <= 0f) { behind++; }
                else if (clip.Z < 0f) { nearClipped++; }
            }
        }
        Log(report, "near plane: corners=" + total + " behindCamera=" + behind + " inFrontOfNearPlane=" + nearClipped);
    }

    private static float[] AutoFrame(List<Tri> triangles, Matrix camera, float aspect, float margin)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (Tri tri in triangles)
        {
            foreach (Vector3 p in new[] { tri.Wa, tri.Wb, tri.Wc })
            {
                Vector4 clip = Vector4.Transform(new Vector4(p, 1f), camera);
                if (clip.W <= 1e-4f || clip.Z < 0f || clip.Z > clip.W) { continue; }
                float x = clip.X / clip.W, y = clip.Y / clip.W;
                if (Math.Abs(x) > 1.5f || Math.Abs(y) > 1.5f) { continue; }
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
        }
        if (minX > maxX) { return new[] { -1f, -1f, 1f, 1f }; }
        // Square in screen-metric units (x scaled by the aspect ratio).
        float cx = (minX + maxX) / 2f, cy = (minY + maxY) / 2f;
        float half = Math.Max((maxX - minX) * aspect, maxY - minY) / 2f * (1f + margin);
        return new[] { cx - half / aspect, cy - half, cx + half / aspect, cy + half };
    }

    // ----------------------------------------------------------------- render

    private sealed class RenderResult
    {
        public Bitmap Image;
        public Dictionary<string, int> PixelsPerBatch = new Dictionary<string, int>();
        public int Occluded;
    }

    private struct ClipVertex
    {
        public Vector4 Clip;
        public Vector2 Uv;
        public Vector3 Colour;
        public ClipVertex(Vector4 clip, Vector2 uv, Vector3 colour) { Clip = clip; Uv = uv; Colour = colour; }
    }

    private static readonly Color[] Palette =
    {
        Color.FromArgb(220, 90, 90), Color.FromArgb(90, 170, 220),
        Color.FromArgb(120, 200, 120), Color.FromArgb(230, 190, 90),
        Color.FromArgb(190, 120, 210), Color.FromArgb(120, 210, 200),
        Color.FromArgb(230, 140, 190), Color.FromArgb(170, 170, 170),
        Color.FromArgb(255, 128, 0), Color.FromArgb(0, 128, 255),
        Color.FromArgb(128, 255, 0), Color.FromArgb(255, 0, 128),
    };

    private static Vector3 ColourOf(Color c)
    {
        return new Vector3(c.R / 255f, c.G / 255f, c.B / 255f);
    }

    private static Vector3 BoneColour(Batch batch, int v)
    {
        int dominant = 0;
        for (int i = 1; i < 4; i++) { if (batch.Weights[v][i] > batch.Weights[v][dominant]) { dominant = i; } }
        int bone = batch.Bindings[v][dominant];
        // Golden-angle hue so neighbouring bone numbers differ clearly.
        double hue = (bone * 137.508) % 360.0;
        return Hsv(hue, 0.75, 0.95);
    }

    private static Vector3 Hsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return new Vector3((float)(r + m), (float)(g + m), (float)(b + m));
    }

    private static Vector3 StretchColour(float stretch)
    {
        float t = MathHelper.Clamp((stretch - 1f) / 1.5f, 0f, 1f);
        return t < 0.5f
            ? Vector3.Lerp(new Vector3(0.2f, 0.8f, 0.2f), new Vector3(0.95f, 0.9f, 0.1f), t * 2f)
            : Vector3.Lerp(new Vector3(0.95f, 0.9f, 0.1f), new Vector3(0.95f, 0.1f, 0.1f), (t - 0.5f) * 2f);
    }

    private static RenderResult Render(
        Asset asset, List<Tri> triangles, Matrix camera, float aspect,
        float[] window, int size, string colour)
    {
        var result = new RenderResult();
        float x0 = window[0], y0 = window[1], x1 = window[2], y1 = window[3];
        float scaleX = size / (x1 - x0), scaleY = size / (y1 - y0);
        var rgb = new float[size * size * 3];
        var depth = new float[size * size];
        var owner = new int[size * size];
        for (int i = 0; i < depth.Length; i++) { depth[i] = float.MaxValue; owner[i] = -1; }
        for (int i = 0; i < rgb.Length; i++) { rgb[i] = 0.35f; }

        // Light: towards the camera, slightly above. Derived from the camera's
        // view axis (third column of the linear part points along depth).
        Vector3 viewAxis = Vector3.Normalize(new Vector3(camera.M13, camera.M23, camera.M33));
        Vector3 light = Vector3.Normalize(-viewAxis + new Vector3(0f, 0.4f, 0f));

        // A glove-only depth buffer, for the depth-fight analysis.
        float[] gloveDepth = null;
        if (colour == "depthfight")
        {
            gloveDepth = new float[size * size];
            for (int i = 0; i < gloveDepth.Length; i++) { gloveDepth[i] = float.MaxValue; }
            foreach (Tri tri in triangles)
            {
                if (!tri.Batch.IsHandComponent) { continue; }
                Rasterise(tri, camera, x0, y0, scaleX, scaleY, size, null, gloveDepth, null, null, null, null, null, light, "batch", aspect);
            }
        }

        foreach (Tri tri in triangles)
        {
            Rasterise(tri, camera, x0, y0, scaleX, scaleY, size, rgb, depth, owner, result, gloveDepth, null, null, light, colour, aspect);
        }

        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 3;
                bitmap.SetPixel(x, y, Color.FromArgb(255,
                    Clamp8((int)(rgb[i] * 255)), Clamp8((int)(rgb[i + 1] * 255)), Clamp8((int)(rgb[i + 2] * 255))));
            }
        }
        foreach (Tri tri in triangles)
        {
            string name = tri.Batch.Name;
            if (!result.PixelsPerBatch.ContainsKey(name)) { result.PixelsPerBatch[name] = 0; }
        }
        for (int i = 0; i < owner.Length; i++)
        {
            if (owner[i] < 0) { continue; }
            string name = asset.Batches[owner[i]].Name;
            result.PixelsPerBatch[name] = result.PixelsPerBatch[name] + 1;
        }
        result.Image = bitmap;
        return result;
    }

    private static void Rasterise(
        Tri tri, Matrix camera, float x0, float y0, float scaleX, float scaleY, int size,
        float[] rgb, float[] depth, int[] owner, RenderResult result, float[] gloveDepth,
        object unused1, object unused2, Vector3 light, string colour, float aspect)
    {
        Batch batch = tri.Batch;
        Vector3 normal = Vector3.Cross(tri.Wb - tri.Wa, tri.Wc - tri.Wa);
        float shade = 1f;
        if (normal.LengthSquared() > 1e-12f)
        {
            normal.Normalize();
            shade = 0.55f + 0.45f * Math.Abs(Vector3.Dot(normal, light));
        }

        Vector3 flat = Vector3.One;
        bool perVertex = false;
        if (colour == "batch" || colour == "depthfight") { flat = ColourOf(Palette[batch.Index % Palette.Length]); }
        else if (colour == "stretch") { flat = StretchColour(tri.Stretch); shade = 1f; }
        else if (colour == "bone") { perVertex = true; }

        var poly = new List<ClipVertex>
        {
            new ClipVertex(Vector4.Transform(new Vector4(tri.Wa, 1f), camera), batch.Uv[tri.A], perVertex ? BoneColour(batch, tri.A) : flat),
            new ClipVertex(Vector4.Transform(new Vector4(tri.Wb, 1f), camera), batch.Uv[tri.B], perVertex ? BoneColour(batch, tri.B) : flat),
            new ClipVertex(Vector4.Transform(new Vector4(tri.Wc, 1f), camera), batch.Uv[tri.C], perVertex ? BoneColour(batch, tri.C) : flat),
        };
        // Clip against w > epsilon, z >= 0 and z <= w, as the GPU does.
        poly = ClipAgainst(poly, delegate(Vector4 c) { return c.W - 1e-5f; });
        poly = ClipAgainst(poly, delegate(Vector4 c) { return c.Z; });
        poly = ClipAgainst(poly, delegate(Vector4 c) { return c.W - c.Z; });
        if (poly.Count < 3) { return; }

        bool vertexColourEnabled = !batch.IsBareHandShell && !batch.IsBaseBody;
        for (int k = 1; k + 1 < poly.Count; k++)
        {
            ClipVertex va = poly[0], vb = poly[k], vc = poly[k + 1];
            Vector3 sa = Divide(va.Clip), sb = Divide(vb.Clip), sc = Divide(vc.Clip);
            var pa = new Vector2((sa.X - x0) * scaleX, (sa.Y - y0) * scaleY);
            var pb = new Vector2((sb.X - x0) * scaleX, (sb.Y - y0) * scaleY);
            var pc = new Vector2((sc.X - x0) * scaleX, (sc.Y - y0) * scaleY);
            float area = Edge(pa, pb, pc);
            if (Math.Abs(area) < 1e-9f) { continue; }
            int loX = (int)Math.Max(0, Math.Floor(Math.Min(pa.X, Math.Min(pb.X, pc.X))));
            int hiX = (int)Math.Min(size - 1, Math.Ceiling(Math.Max(pa.X, Math.Max(pb.X, pc.X))));
            int loY = (int)Math.Max(0, Math.Floor(Math.Min(pa.Y, Math.Min(pb.Y, pc.Y))));
            int hiY = (int)Math.Min(size - 1, Math.Ceiling(Math.Max(pa.Y, Math.Max(pb.Y, pc.Y))));
            // Perspective-correct interpolation weights use 1/w.
            float ia = 1f / va.Clip.W, ib = 1f / vb.Clip.W, ic = 1f / vc.Clip.W;
            for (int y = loY; y <= hiY; y++)
            {
                for (int x = loX; x <= hiX; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float w0 = Edge(pb, pc, p) / area;
                    float w1 = Edge(pc, pa, p) / area;
                    float w2 = Edge(pa, pb, p) / area;
                    if (w0 < 0 || w1 < 0 || w2 < 0) { continue; }
                    float z = w0 * sa.Z + w1 * sb.Z + w2 * sc.Z;
                    int pixel = y * size + x;
                    if (z > depth[pixel]) { continue; }
                    if (rgb == null) { depth[pixel] = z; continue; }

                    float denominator = w0 * ia + w1 * ib + w2 * ic;
                    float q0 = w0 * ia / denominator, q1 = w1 * ib / denominator, q2 = w2 * ic / denominator;
                    Vector3 tint = va.Colour * q0 + vb.Colour * q1 + vc.Colour * q2;
                    float alpha = 1f;
                    Vector3 texel = tint;
                    if (colour == "texture")
                    {
                        Vector2 uv = va.Uv * q0 + vb.Uv * q1 + vc.Uv * q2;
                        texel = batch.Diffuse;
                        if (batch.Texture != null)
                        {
                            Color sample = Sample(batch.Texture, uv);
                            texel = new Vector3(texel.X * sample.R / 255f, texel.Y * sample.G / 255f, texel.Z * sample.B / 255f);
                            alpha = sample.A / 255f;
                        }
                        if (vertexColourEnabled)
                        {
                            Vector3 vertexColour =
                                ColourOf(batch.VertexColor[tri.A]) * q0 +
                                ColourOf(batch.VertexColor[tri.B]) * q1 +
                                ColourOf(batch.VertexColor[tri.C]) * q2;
                            texel *= vertexColour;
                        }
                    }
                    if (colour == "depthfight" && gloveDepth != null && !batch.IsHandComponent &&
                        gloveDepth[pixel] != float.MaxValue && gloveDepth[pixel] >= z &&
                        gloveDepth[pixel] - z < 0.0005f)
                    {
                        texel = new Vector3(1f, 0f, 1f);
                        if (result != null && depth[pixel] == float.MaxValue) { result.Occluded++; }
                    }
                    texel *= shade;
                    int i = pixel * 3;
                    // NonPremultiplied blending with depth written regardless,
                    // which is what BasicEffect does without an alpha test.
                    rgb[i] = rgb[i] * (1 - alpha) + texel.X * alpha;
                    rgb[i + 1] = rgb[i + 1] * (1 - alpha) + texel.Y * alpha;
                    rgb[i + 2] = rgb[i + 2] * (1 - alpha) + texel.Z * alpha;
                    depth[pixel] = z;
                    if (owner != null && alpha > 0.03f) { owner[pixel] = batch.Index; }
                }
            }
        }
    }

    private static Vector3 Divide(Vector4 clip)
    {
        return new Vector3(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
    }

    private static List<ClipVertex> ClipAgainst(List<ClipVertex> poly, Func<Vector4, float> inside)
    {
        var result = new List<ClipVertex>();
        for (int i = 0; i < poly.Count; i++)
        {
            ClipVertex current = poly[i];
            ClipVertex next = poly[(i + 1) % poly.Count];
            float dc = inside(current.Clip), dn = inside(next.Clip);
            if (dc >= 0) { result.Add(current); }
            if ((dc >= 0) != (dn >= 0))
            {
                float t = dc / (dc - dn);
                result.Add(new ClipVertex(
                    Vector4.Lerp(current.Clip, next.Clip, t),
                    Vector2.Lerp(current.Uv, next.Uv, t),
                    Vector3.Lerp(current.Colour, next.Colour, t)));
            }
        }
        return result;
    }

    private static float Edge(Vector2 a, Vector2 b, Vector2 c)
    {
        return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    }

    private static Color Sample(Bitmap texture, Vector2 uv)
    {
        float u = uv.X - (float)Math.Floor(uv.X);
        float v = uv.Y - (float)Math.Floor(uv.Y);
        int x = (int)(u * texture.Width);
        int y = (int)(v * texture.Height);
        if (x < 0) x = 0; if (x >= texture.Width) x = texture.Width - 1;
        if (y < 0) y = 0; if (y >= texture.Height) y = texture.Height - 1;
        return texture.GetPixel(x, y);
    }

    private static int Clamp8(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }
}
