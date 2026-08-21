using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DNA.Avatars;
using DNA.Net.GamerServices;
using Microsoft.Xna.Framework;

// Guards the first-person hand pose.
//
// First person pins the arm to the game's ProxyBoy rig and chains the hand
// below each wrist from the avatar's own skeleton. The regression this exists
// to catch is the one that shipped for a while: finger bones pinned to
// ProxyBoy's first-person fist, which an Xbox glove is not rigged for, so the
// glove tore open at the knuckles. Three things are checked against a real
// avatar:
//
//   1. At grip 0 the hand is exactly its bind pose relative to the wrist, for
//      every vertex in the hand volume of every batch.
//   2. Whatever ProxyBoy does with its finger bones makes no difference.
//   3. At full grip no hand triangle tears: the worst edge stretch stays far
//      below the 19x the fist produced.
internal static class FirstPersonHandSmoke
{
    private const BindingFlags Hidden =
        BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;

    private static int Main(string[] args)
    {
        if (args.Length < 2 || args.Length > 3)
        {
            Console.Error.WriteLine(
                "Usage: FirstPersonHandSmoke <mod.dll> <avatar.ocavatar> [game folder]");
            return 2;
        }
        // Kept free of DNA types so the resolver below is attached before the
        // JIT needs DNA.Common; see AvatarAttachmentSmoke.
        string gameFolder = args.Length == 3
            ? Path.GetFullPath(args[2])
            : Path.GetDirectoryName(Path.GetFullPath(args[1]));
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs resolveArgs)
        {
            string wanted = new AssemblyName(resolveArgs.Name).Name;
            for (string folder = gameFolder;
                 !string.IsNullOrEmpty(folder);
                 folder = Path.GetDirectoryName(folder))
            {
                foreach (string extension in new[] { ".dll", ".exe" })
                {
                    string candidate = Path.Combine(folder, wanted + extension);
                    if (File.Exists(candidate))
                    {
                        return Assembly.LoadFrom(candidate);
                    }
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
            Console.Error.WriteLine("FAIL FirstPersonHandSmoke: " + error.Message);
            return 1;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        Assembly mod = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        Type assetType = ModType(mod, "AvatarAsset");
        object asset = assetType.GetMethod("Load", Hidden).Invoke(
            null, new object[] { Path.GetFullPath(args[1]) });
        Matrix[] inverseBind = (Matrix[])Field(assetType, asset, "InverseBindPose");
        Matrix[] bindAbsolute = (Matrix[])Field(assetType, asset, "BindPoseAbsolute");
        Vector3[] boneScale = (Vector3[])Field(assetType, asset, "FirstPersonBoneScale");
        int bones = inverseBind.Length;

        Type entityType = ModType(mod, "ImportedAvatarModelEntity");
        MethodInfo build = entityType.GetMethod("BuildFirstPersonSkinTransforms", Hidden);
        Require(build != null, "runtime has no BuildFirstPersonSkinTransforms");

        // A proxy rig standing exactly where the avatar's bind pose stands,
        // with the avatar's shape scale taken back out, since the real proxy
        // rig carries none and the runtime puts it back in.
        var proxy = new Matrix[bones];
        var identity = new int[bones];
        for (int bone = 0; bone < bones; bone++)
        {
            proxy[bone] = Matrix.Invert(Matrix.CreateScale(boneScale[bone])) * bindAbsolute[bone];
            identity[bone] = bone;
        }
        var result = new Matrix[bones];
        var scratch = new Matrix[bones];
        var hand = new List<int>();
        for (int bone = 0; bone < bones; bone++)
        {
            if (WristOf(bone) >= 0) { hand.Add(bone); }
        }
        Require(hand.Count >= 30, "expected the finger bones below both wrists, found " + hand.Count);

        // 1. Grip 0 is the bind pose.
        build.Invoke(null, new object[] { asset, proxy, identity, 0f, result, scratch });
        float worstRest = 0f;
        int restVertices = 0;
        foreach (object batch in (Array)Field(assetType, asset, "Batches"))
        {
            Type batchType = batch.GetType();
            short[] volume = (short[])Field(batchType, batch, "FirstPersonIndices");
            if (volume == null || volume.Length == 0) { continue; }
            Array vertices = (Array)Field(batchType, batch, "SourceVertices");
            var seen = new HashSet<int>();
            foreach (short raw in volume)
            {
                int index = (ushort)raw;
                if (!seen.Add(index)) { continue; }
                object vertex = vertices.GetValue(index);
                Vector3 bind = (Vector3)Field(vertex.GetType(), vertex, "Position");
                Vector3 posed = Skin(vertex, bind, result, bones);
                worstRest = Math.Max(worstRest, Vector3.Distance(bind, posed));
                restVertices++;
            }
        }
        Require(restVertices > 0, "no hand-volume vertices to check");
        Require(worstRest < 0.001f, string.Format(
            "at grip 0 the hand is {0:F4} m off its bind pose; it must be the bind pose", worstRest));

        // 2. ProxyBoy's finger bones are ignored.
        var twisted = (Matrix[])proxy.Clone();
        foreach (int bone in hand)
        {
            twisted[bone] = Matrix.CreateRotationX(1.1f) * Matrix.CreateTranslation(0.2f, -0.1f, 0.3f) * proxy[bone];
        }
        var resultTwisted = new Matrix[bones];
        build.Invoke(null, new object[] { asset, twisted, identity, 0f, resultTwisted, scratch });
        foreach (int bone in hand)
        {
            Require(Same(result[bone], resultTwisted[bone]), "bone " + bone + " followed the proxy rig's finger pose");
        }

        // 3. Full grip does not tear the hand.
        build.Invoke(null, new object[] { asset, proxy, identity, 1f, result, scratch });
        float worstStretch = 0f;
        int torn = 0, triangles = 0;
        foreach (object batch in (Array)Field(assetType, asset, "Batches"))
        {
            Type batchType = batch.GetType();
            short[] volume = (short[])Field(batchType, batch, "FirstPersonIndices");
            if (volume == null || volume.Length < 3) { continue; }
            Array vertices = (Array)Field(batchType, batch, "SourceVertices");
            for (int t = 0; t + 2 < volume.Length; t += 3)
            {
                var bind = new Vector3[3];
                var posed = new Vector3[3];
                for (int k = 0; k < 3; k++)
                {
                    object vertex = vertices.GetValue((ushort)volume[t + k]);
                    bind[k] = (Vector3)Field(vertex.GetType(), vertex, "Position");
                    posed[k] = Skin(vertex, bind[k], result, bones);
                }
                float stretch = 1f;
                for (int k = 0; k < 3; k++)
                {
                    float before = Vector3.Distance(bind[k], bind[(k + 1) % 3]);
                    float after = Vector3.Distance(posed[k], posed[(k + 1) % 3]);
                    if (before > 1e-5f) { stretch = Math.Max(stretch, after / before); }
                }
                worstStretch = Math.Max(worstStretch, stretch);
                if (stretch > 2.5f) { torn++; }
                triangles++;
            }
        }
        // A healthy full grip peaks around 3x at the thumb web; the fist that
        // tore the glove reached 19x.
        Require(worstStretch < 6f, string.Format(
            "full grip stretches a hand triangle {0:F1}x; the hand is tearing", worstStretch));
        Require(torn * 100 <= triangles, string.Format(
            "full grip stretches {0} of {1} hand triangles beyond 2.5x", torn, triangles));

        Console.WriteLine(string.Format(
            "PASS FirstPersonHandSmoke: {0} hand vertices at bind ({1:E1} m), {2} hand bones independent of the proxy rig, full grip worst stretch {3:F2}x over {4} triangles",
            restVertices, worstRest, hand.Count, worstStretch, triangles));
        return 0;
    }

    private static Vector3 Skin(object vertex, Vector3 bind, Matrix[] skin, int bones)
    {
        Type type = vertex.GetType();
        byte[] bindings = (byte[])Field(type, vertex, "Bindings");
        byte[] weights = (byte[])Field(type, vertex, "Weights");
        Vector3 position = Vector3.Zero;
        float total = 0f;
        for (int i = 0; i < 4; i++)
        {
            float weight = weights[i] / 255f;
            int bone = bindings[i];
            if (weight <= 0f || bone < 0 || bone >= bones) { continue; }
            position += Vector3.Transform(bind, skin[bone]) * weight;
            total += weight;
        }
        if (total <= 0.0001f) { return bind; }
        return Math.Abs(total - 1f) > 0.0001f ? position / total : position;
    }

    private static int WristOf(int bone)
    {
        int left = (int)AvatarBone.WristLeft, right = (int)AvatarBone.WristRight;
        if (bone == left || bone == right) { return -1; }
        int current = bone;
        for (int guard = 0; guard < 128 && current >= 0 && current < Avatar.DefaultParentBones.Count; guard++)
        {
            current = Avatar.DefaultParentBones[current];
            if (current == left || current == right) { return current; }
        }
        return -1;
    }

    private static bool Same(Matrix a, Matrix b)
    {
        float[] ea = Elements(a), eb = Elements(b);
        for (int i = 0; i < 16; i++)
        {
            if (Math.Abs(ea[i] - eb[i]) > 1e-5f) { return false; }
        }
        return true;
    }

    private static float[] Elements(Matrix m)
    {
        return new[]
        {
            m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44
        };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
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
