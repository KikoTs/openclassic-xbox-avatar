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
        Type assetType = mod.GetType(
            "OpenClassic.XboxAvatar.AvatarAsset",
            true);
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
        Vector3 importedProp = new Vector3(
            -sourceProp.X,
            sourceProp.Y,
            -sourceProp.Z);
        Vector3 importedWrist = new Vector3(
            -sourceWrist.X,
            sourceWrist.Y,
            -sourceWrist.Z);
        Type entityType = mod.GetType(
            "OpenClassic.XboxAvatar.ImportedAvatarModelEntity",
            true);
        MethodInfo compute = entityType.GetMethod(
            "ComputeThirdPersonPropTranslation",
            Hidden);
        Vector3 corrected = (Vector3)compute.Invoke(
            null,
            new object[]
            {
                importedWrist,
                importedProp,
                stockWrist,
                stockProp
            });
        Require(IsFinite(corrected), "corrected PropRight is not finite");
        Require(Vector3.Distance(
            corrected - importedWrist,
            stockProp - stockWrist) < 0.0001f,
            "corrected item anchor did not preserve the stock grip offset");
        Require(Math.Abs(
            (corrected.Y - stockProp.Y) -
            (importedWrist.Y - stockWrist.Y)) < 0.0001f,
            "corrected item anchor did not follow avatar wrist height");

        Console.WriteLine(
            "PASS: proportion-aware third-person grip source=" + sourceProp +
            " stock=" + stockProp +
            " corrected=" + corrected +
            " delta=" + (sourceProp - stockProp) +
            " wristOffset=" + (sourceProp - sourceWrist) +
            " stockWristOffset=" + (stockProp - stockWrist) + ".");
        return 0;
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
}
