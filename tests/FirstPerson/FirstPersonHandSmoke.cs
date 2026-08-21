using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DNA.Avatars;
using DNA.Net.GamerServices;
using Microsoft.Xna.Framework;

// Guards the first-person hand pose.
//
// First person pins the arm to the game's ProxyBoy rig, converting each bone
// frame from the Xbox rig's handedness to XNA's, and below each wrist applies
// ProxyBoy's joint rotations to the avatar's own bone offsets. The regression
// this exists to catch is the one that shipped for a while: avatar bone frames
// multiplied straight onto the XNA proxy bones, which mirrored every bone's
// geometry in its own frame - the hand came out thumb-for-pinky and the glove
// tore open at the knuckles. Against a real avatar:
//
//   1. With the proxy rig standing in the avatar's bind pose, the hand is the
//      bind pose at every grip, for every vertex in every hand volume.
//   2. The hand takes its joint rotations from the proxy rig and its bone
//      positions from the avatar: moving the proxy's hand bones changes
//      nothing, rotating them moves the fingers.
//   3. The game's fist - finger bases 63 degrees, middle joints 90, tips 40 -
//      does not tear the hand either way round: the worst edge stretch stays
//      far below the 19x the mirrored hand produced.
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
        // in XNA handedness, with the avatar's shape scale taken back out -
        // the real proxy rig carries neither and the runtime puts both in.
        var proxy = new Matrix[bones];
        var identity = new int[bones];
        for (int bone = 0; bone < bones; bone++)
        {
            Vector3 s = boneScale[bone];
            proxy[bone] = Matrix.Invert(Matrix.CreateScale(s.X, s.Y, -s.Z)) * bindAbsolute[bone];
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
        Array batches = (Array)Field(assetType, asset, "Batches");

        // 1. A proxy rig in the bind pose gives the bind pose, at every grip.
        float worstRest = 0f;
        int restVertices = 0;
        foreach (float grip in new[] { 0f, 0.5f, 1f })
        {
            build.Invoke(null, new object[] { asset, proxy, identity, grip, result, scratch });
            restVertices = 0;
            foreach (object batch in batches)
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
        }
        Require(restVertices > 0, "no hand-volume vertices to check");
        Require(worstRest < 0.001f, string.Format(
            "with the proxy rig at bind the hand is {0:F4} m off its bind pose; it must be the bind pose", worstRest));

        // 2. Joint rotations come from the proxy rig, bone positions from the
        //    avatar. Shifting every hand bone of the proxy changes nothing;
        //    rotating a finger base moves that finger.
        var shifted = (Matrix[])proxy.Clone();
        foreach (int bone in hand)
        {
            shifted[bone] = proxy[bone] * Matrix.CreateTranslation(0.2f, -0.1f, 0.3f);
        }
        var resultShifted = new Matrix[bones];
        build.Invoke(null, new object[] { asset, shifted, identity, 1f, resultShifted, scratch });
        build.Invoke(null, new object[] { asset, proxy, identity, 1f, result, scratch });
        foreach (int bone in hand)
        {
            Require(Same(result[bone], resultShifted[bone]), "bone " + bone + " took its position from the proxy rig");
        }
        int indexBase = (int)AvatarBone.FingerIndexRight;
        int indexTip = (int)AvatarBone.FingerIndex3Right;
        Matrix[] bent = Fist(proxy, bones, new[] { indexBase }, 0.6f);
        build.Invoke(null, new object[] { asset, bent, identity, 1f, resultShifted, scratch });
        Vector3 tipBefore = Vector3.Transform(bindAbsolute[indexTip].Translation, result[indexTip]);
        Vector3 tipAfter = Vector3.Transform(bindAbsolute[indexTip].Translation, resultShifted[indexTip]);
        float moved = Vector3.Distance(tipBefore, tipAfter);
        Require(moved > 0.01f, string.Format(
            "rotating the proxy's index finger 0.6 rad moved the fingertip only {0:F4} m; the hand ignores the proxy's joints", moved));

        // 3. The game's fist does not tear the hand, whichever way its hinges
        //    turn out to run on this rig.
        float worstStretch = 0f;
        int torn = 0, triangles = 0;
        int[] bases = { 44, 45, 46, 47 };
        int[] middles = { 56, 57, 58, 59 };
        int[] tips = { 66, 67, 68, 69 };
        foreach (float direction in new[] { 1f, -1f })
        {
            Matrix[] fist = Fist(proxy, bones, bases, 1.1f * direction);
            fist = Fist(fist, bones, middles, 1.57f * direction);
            fist = Fist(fist, bones, tips, 0.7f * direction);
            build.Invoke(null, new object[] { asset, fist, identity, 1f, result, scratch });
            torn = 0; triangles = 0;
            foreach (object batch in batches)
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
            // The game's own fist peaks around 4x at one pinky-knuckle
            // triangle; the mirrored hand reached 19x.
            Require(worstStretch < 7f, string.Format(
                "the fist stretches a hand triangle {0:F1}x; the hand is tearing", worstStretch));
            Require(torn * 50 <= triangles, string.Format(
                "the fist stretches {0} of {1} hand triangles beyond 2.5x", torn, triangles));
        }

        Console.WriteLine(string.Format(
            "PASS FirstPersonHandSmoke: {0} hand vertices at bind ({1:E1} m), {2} hand bones positioned by the avatar and rotated by the proxy rig, fist worst stretch {3:F2}x over {4} triangles",
            restVertices, worstRest, hand.Count, worstStretch, triangles));
        return 0;
    }

    /// <summary>
    /// The proxy rig with each given bone, and everything below it, turned by
    /// <paramref name="radians"/> about that bone's own Z hinge.
    /// </summary>
    private static Matrix[] Fist(Matrix[] proxy, int bones, int[] joints, float radians)
    {
        var result = (Matrix[])proxy.Clone();
        foreach (int joint in joints)
        {
            Matrix turn = Matrix.Invert(proxy[joint]) * Matrix.CreateRotationZ(radians) * proxy[joint];
            for (int bone = 0; bone < bones; bone++)
            {
                if (bone == joint || IsBelow(bone, joint))
                {
                    result[bone] = result[bone] * turn;
                }
            }
        }
        return result;
    }

    private static bool IsBelow(int bone, int ancestor)
    {
        int current = bone;
        for (int guard = 0; guard < 128 && current >= 0 && current < Avatar.DefaultParentBones.Count; guard++)
        {
            current = Avatar.DefaultParentBones[current];
            if (current == ancestor) { return true; }
        }
        return false;
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
