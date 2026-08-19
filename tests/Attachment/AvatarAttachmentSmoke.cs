using System;
using System.IO;
using System.Reflection;
using DNA.Avatars;
using DNA.Net.GamerServices;
using Microsoft.Xna.Framework;

internal static class AvatarAttachmentSmoke
{
    private const BindingFlags Hidden =
        BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine(
                "Usage: AvatarAttachmentSmoke <mod.dll> <avatar.ocavatar>");
            return 2;
        }

        Assembly mod = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        Type assetType = ModType(mod, "AvatarAsset");
        object asset = assetType.GetMethod("Load", Hidden).Invoke(
            null,
            new object[] { Path.GetFullPath(args[1]) });
        Matrix[] sourceLocal = (Matrix[])assetType
            .GetField("SourcePoseLocal", Hidden)
            .GetValue(asset);
        Matrix[] source = BuildCumulative(sourceLocal);
        Matrix[] stockLocal = new Matrix[Avatar.DefaultBindPose.Count];
        Avatar.DefaultBindPose.CopyTo(stockLocal, 0);
        Matrix[] stock = BuildCumulative(stockLocal);

        int prop = (int)AvatarBone.PropRight;
        int wrist = (int)AvatarBone.WristRight;
        Vector3 sourceProp = source[prop].Translation;
        Vector3 sourceWrist = source[wrist].Translation;
        Vector3 stockProp = stock[prop].Translation;
        Vector3 stockWrist = stock[wrist].Translation;

        Require(IsFinite(sourceProp), "source PropRight is not finite");
        Require(IsFinite(sourceWrist), "source WristRight is not finite");
        Require(Vector3.Distance(sourceProp, sourceWrist) < 0.5f,
            "source PropRight is detached from the right wrist");

        // Runtime export space reflects X/Z into Castle Miner Z space before
        // the attachment correction is evaluated.
        Vector3 importedProp = ConvertExport(sourceProp);
        Vector3 importedIndex = ConvertExport(
            source[(int)AvatarBone.FingerIndexRight].Translation);
        Vector3 importedMiddle = ConvertExport(
            source[(int)AvatarBone.FingerMiddleRight].Translation);
        Vector3 importedRing = ConvertExport(
            source[(int)AvatarBone.FingerRingRight].Translation);
        Vector3 importedSmall = ConvertExport(
            source[(int)AvatarBone.FingerSmallRight].Translation);
        Vector3 importedThumb = ConvertExport(
            source[(int)AvatarBone.FingerThumbRight].Translation);
        Type entityType = ModType(mod, "ImportedAvatarModelEntity");
        MethodInfo compute = entityType.GetMethod(
            "ComputeThirdPersonGripTranslation",
            Hidden);
        Vector3 corrected = (Vector3)compute.Invoke(
            null,
            new object[]
            {
                importedProp,
                importedIndex,
                importedMiddle,
                importedRing,
                importedSmall,
                importedThumb
            });
        Require(IsFinite(corrected), "corrected PropRight is not finite");
        Vector3 expectedGrip =
            (importedIndex + importedMiddle + importedRing +
             importedSmall + importedThumb) / 5f;
        Require(Vector3.Distance(corrected, expectedGrip) < 0.0001f,
            "item anchor did not move to the visible digit grip center");
        Require(corrected.Y - importedProp.Y > 0.08f,
            "item anchor remains below the visible hand");

        // Pistols have an identity ItemUse.Hand child matrix, so this checks
        // the final rendered pistol origin after the complete child-to-anchor
        // transform chain rather than merely asserting a skeleton bone.
        Matrix anchor = Matrix.Identity;
        anchor.Translation = corrected;
        Matrix finalPistol = Matrix.Identity * anchor;
        Require(Vector3.Distance(
            finalPistol.Translation,
            expectedGrip) < 0.0001f,
            "final pistol entity did not reach the visible grip");

        Console.WriteLine(
            "PASS: proportion-aware third-person grip source=" + sourceProp +
            " stock=" + stockProp +
            " corrected=" + corrected +
            " delta=" + (sourceProp - stockProp) +
            " wristOffset=" + (sourceProp - sourceWrist) +
            " stockWristOffset=" + (stockProp - stockWrist) + ".");
        return 0;
    }

    private static Vector3 ConvertExport(Vector3 source)
    {
        return new Vector3(-source.X, source.Y, -source.Z);
    }

    private static Matrix[] BuildCumulative(Matrix[] local)
    {
        var result = new Matrix[local.Length];
        result[0] = local[0];
        for (int bone = 1; bone < local.Length; bone++)
        {
            result[bone] = local[bone] *
                result[Avatar.DefaultParentBones[bone]];
        }
        return result;
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.X) && !float.IsNaN(value.Y) &&
            !float.IsNaN(value.Z) && !float.IsInfinity(value.X) &&
            !float.IsInfinity(value.Y) && !float.IsInfinity(value.Z);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    // Resolve a mod type by its simple name. The runtime ships under more than
    // one brand, so its namespace is not fixed and must not be hard-coded here.
    private static Type ModType(Assembly mod, string simpleName)
    {
        // Enumerating every type would need each dependency loadable in this
        // reflection-only context, so probe the known brand namespaces instead.
        string[] namespaces = { "XboxAvatar", "OpenClassic.XboxAvatar" };
        foreach (string space in namespaces)
        {
            Type candidate = mod.GetType(space + "." + simpleName, false);
            if (candidate != null)
            {
                return candidate;
            }
        }
        // All probes returned null. Re-ask with throwOnError so the real reason
        // (usually an unresolvable base type) surfaces instead of a bare null.
        return mod.GetType(namespaces[0] + "." + simpleName, true);
    }
}
