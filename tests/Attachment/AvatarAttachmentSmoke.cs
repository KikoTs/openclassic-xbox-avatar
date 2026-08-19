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
        if (args.Length < 2 || args.Length > 3)
        {
            Console.Error.WriteLine(
                "Usage: AvatarAttachmentSmoke <mod.dll> <avatar.ocavatar> [game folder]");
            return 2;
        }

        // This method must not touch a DNA type. Referencing one here would make
        // the JIT resolve DNA.Common while compiling Main, before the handler
        // below is attached, and the load would fail before it could help. The
        // real work therefore lives in Run, which is kept out of line.
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
                // .exe matters: the game assembly is CastleMinerZ.exe.
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

        return Run(args);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
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

        // Runtime export space reflects Z into Castle Miner Z space before the
        // attachment correction is evaluated.
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

        // Take the scale-aware overload explicitly: the correction bound is
        // proportional to the avatar's build, so the harness has to say which
        // build it is checking.
        float shapeScale = RootScale(sourceLocal);
        MethodInfo compute = entityType.GetMethod(
            "ComputeThirdPersonGripTranslation",
            Hidden,
            null,
            new[]
            {
                typeof(Vector3), typeof(Vector3), typeof(Vector3),
                typeof(Vector3), typeof(Vector3), typeof(Vector3),
                typeof(float)
            },
            null);
        Require(compute != null, "scale-aware grip overload is missing");
        Vector3 corrected = (Vector3)compute.Invoke(
            null,
            new object[]
            {
                importedProp,
                importedIndex,
                importedMiddle,
                importedRing,
                importedSmall,
                importedThumb,
                shapeScale
            });
        Require(IsFinite(corrected), "corrected PropRight is not finite");
        Vector3 expectedGrip =
            (importedIndex + importedMiddle + importedRing +
             importedSmall + importedThumb) / 5f;
        Require(Vector3.Distance(corrected, expectedGrip) < 0.0001f,
            "item anchor did not move to the visible digit grip center");
        Require(corrected.Y - importedProp.Y > 0.08f,
            "item anchor remains below the visible hand");

        // A tall avatar is the case that regressed: the prop-to-grip correction
        // grows with the body, so a correction bound expressed in absolute
        // metres starts truncating it around a build of 1.61 and drags the item
        // back down towards the invisible prop bone. Re-run the same geometry
        // scaled up and require the anchor still reaches the grip exactly.
        const float tallBuild = 2.0f;
        Vector3 tallProp = importedProp * tallBuild;
        Vector3 tallIndex = importedIndex * tallBuild;
        Vector3 tallMiddle = importedMiddle * tallBuild;
        Vector3 tallRing = importedRing * tallBuild;
        Vector3 tallSmall = importedSmall * tallBuild;
        Vector3 tallThumb = importedThumb * tallBuild;
        Vector3 tallCorrected = (Vector3)compute.Invoke(
            null,
            new object[]
            {
                tallProp, tallIndex, tallMiddle,
                tallRing, tallSmall, tallThumb,
                shapeScale * tallBuild
            });
        Vector3 tallGrip =
            (tallIndex + tallMiddle + tallRing + tallSmall + tallThumb) / 5f;
        Require(Vector3.Distance(tallCorrected, tallGrip) < 0.0001f,
            "tall avatar item anchor was clamped short of the visible grip");

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

    /// <summary>The editor build scale, which lives in the root of the source pose.</summary>
    private static float RootScale(Matrix[] sourceLocal)
    {
        Vector3 scale;
        Quaternion rotation;
        Vector3 translation;
        if (!sourceLocal[0].Decompose(out scale, out rotation, out translation))
        {
            return 1f;
        }
        return (scale.X + scale.Y + scale.Z) / 3f;
    }

    private static Vector3 ConvertExport(Vector3 source)
    {
        // Z only, matching the runtime's ExportBoneTranslation. The runtime
        // dropped the X flip when its render transform became a pure Z
        // reflection; this harness kept flipping X, so it was not exercising the
        // space the runtime actually uses and could not catch sign or space
        // regressions.
        return new Vector3(source.X, source.Y, -source.Z);
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
