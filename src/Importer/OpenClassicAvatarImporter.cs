using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class AvatarImporter
{
    private const string PackageName = "Microsoft.Avatars_8wekyb3d8bbwe";

    // Matches the runtime's Branding block. build.ps1 defines XBOX_AVATAR_BRAND
    // for the standalone build, which ships under its own name.
#if XBOX_AVATAR_BRAND
    private const string ProductName = "Xbox Avatar";
    private static readonly string[] AvatarSegments = { "Xbox Avatar" };
    private static readonly string[] BridgeSegments = { "Xbox Avatar", "Bridge" };
#else
    private const string ProductName = "OpenClassic Xbox Avatar";
    private static readonly string[] AvatarSegments = { "OpenClassic Addons", "Xbox Avatar" };
    private static readonly string[] BridgeSegments = { "OpenClassic Addons", "Xbox Avatar Bridge" };
#endif

    private static string AvatarFolder(string gameFolder)
    {
        return CombineSegments(gameFolder, AvatarSegments);
    }

    private static string BridgeFolder(string gameFolder)
    {
        return CombineSegments(gameFolder, BridgeSegments);
    }

    private static string CombineSegments(string root, string[] segments)
    {
        string path = root;
        for (int index = 0; index < segments.Length; index++)
        {
            path = Path.Combine(path, segments[index]);
        }
        return path;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "/convert")
        {
            try
            {
                ConvertExport(args[1], args[2]);
                return 0;
            }
            catch (Exception exception)
            {
                File.WriteAllText(args[2] + ".error.txt", exception.ToString());
                return 3;
            }
        }
        Application.EnableVisualStyles();
        try
        {
            string gameFolder = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string bridgeFolder = BridgeFolder(gameFolder);
            string bridgeDll = Path.Combine(bridgeFolder, "AvatarBridge.dll");
            string injector = Path.Combine(bridgeFolder, "AvatarBridgeInjector.exe");
            if (!File.Exists(bridgeDll) || !File.Exists(injector))
            {
                throw new FileNotFoundException("The Xbox Avatar Bridge files are missing. Reinstall the " + ProductName + " add-on.");
            }

            Process editor = FindOrLaunchEditor();
            DialogResult answer = MessageBox.Show(
                "Choose the avatar you want in Xbox Original Avatars.\n\n" +
                "When that avatar is visible in the editor, click OK. The importer will do the rest automatically.\n\n" +
                "No Intel GPA, Blender, or manual model conversion is needed.",
                "Import Xbox Avatar into " + ProductName,
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information);
            if (answer != DialogResult.OK)
            {
                return 1;
            }

            editor.Refresh();
            if (editor.HasExited)
            {
                editor = FindOrLaunchEditor();
            }

            GrantAppContainerReadAccess(bridgeDll);
            string packageFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages",
                PackageName);
            string logPath = Path.Combine(packageFolder, "AC", "AvatarBridge.log");
            DateTime startedUtc = DateTime.UtcNow;
            RunInjector(injector, editor.Id, bridgeDll);
            WaitForExport(logPath, startedUtc);

            string tempState = Path.Combine(packageFolder, "TempState");
            string targetFolder = AvatarFolder(gameFolder);
            Directory.CreateDirectory(targetFolder);
            string temporaryAsset = Path.Combine(targetFolder, "avatar.ocavatar.new");
            string finalAsset = Path.Combine(targetFolder, "avatar.ocavatar");
            ConvertExport(tempState, temporaryAsset);
            if (File.Exists(finalAsset))
            {
                File.Replace(temporaryAsset, finalAsset, Path.Combine(targetFolder, "avatar.previous.ocavatar"), true);
            }
            else
            {
                File.Move(temporaryAsset, finalAsset);
            }

            MessageBox.Show(
                "Your current Xbox avatar is installed for " + ProductName + ".\n\n" +
                "Start CastleMiner Z and your character will use it. Other players running the same add-on will receive it automatically. Run this importer again whenever you change avatars.",
                "Xbox Avatar imported",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message + "\n\nTechnical details:\n" + exception,
                "Xbox Avatar import failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 2;
        }
    }

    private static Process FindOrLaunchEditor()
    {
        Process editor = Process.GetProcessesByName("AvatarEditor").FirstOrDefault();
        if (editor != null)
        {
            return editor;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = "shell:AppsFolder\\Microsoft.Avatars_8wekyb3d8bbwe!Microsoft.Avatars",
            UseShellExecute = true
        });
        for (int attempt = 0; attempt < 100; attempt++)
        {
            Thread.Sleep(200);
            editor = Process.GetProcessesByName("AvatarEditor").FirstOrDefault();
            if (editor != null)
            {
                Thread.Sleep(1500);
                return editor;
            }
        }
        throw new InvalidOperationException(
            "Xbox Original Avatars did not start. Install/open it from Microsoft Store, then run the importer again.");
    }

    private static void GrantAppContainerReadAccess(string bridgeDll)
    {
        using (Process process = Process.Start(new ProcessStartInfo
        {
            FileName = "icacls.exe",
            Arguments = Quote(bridgeDll) + " /grant *S-1-15-2-1:(RX)",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }))
        {
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Windows would not allow Xbox Original Avatars to read the bridge DLL.");
            }
        }
    }

    private static void RunInjector(string injector, int processId, string bridgeDll)
    {
        using (Process process = Process.Start(new ProcessStartInfo
        {
            FileName = injector,
            Arguments = processId.ToString(CultureInfo.InvariantCulture) + " " + Quote(bridgeDll),
            WorkingDirectory = Path.GetDirectoryName(injector),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }))
        {
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Could not connect the exporter to Xbox Original Avatars.\n" + output + error);
            }
        }
    }

    private static void WaitForExport(string logPath, DateTime startedUtc)
    {
        DateTime deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);
            if (!File.Exists(logPath) || File.GetLastWriteTimeUtc(logPath) < startedUtc.AddSeconds(-1))
            {
                continue;
            }
            string log;
            try
            {
                log = File.ReadAllText(logPath);
            }
            catch (IOException)
            {
                continue;
            }
            if (log.Contains("Bridge activation probe completed."))
            {
                return;
            }
            if (log.Contains("failure:") || log.Contains("failed:") || log.Contains("Timed out"))
            {
                throw new InvalidOperationException("Xbox Avatar export failed.\n\n" + LastLines(log, 12));
            }
        }
        throw new TimeoutException("Xbox Original Avatars did not finish exporting within three minutes.");
    }

    private static void ConvertExport(string tempState, string outputPath)
    {
        string posesPath = Path.Combine(tempState, "avatar-poses.txt");
        string textureFolder = Path.Combine(tempState, "AvatarExport", "textures");
        string[] jsonFiles = Directory.GetFiles(tempState, "avatar-selected-*.json")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!File.Exists(posesPath) || jsonFiles.Length == 0)
        {
            throw new InvalidDataException("The exporter completed but did not produce avatar geometry.");
        }

        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 128 };
        string poseText = File.ReadAllText(posesPath);
        int jsonStart = poseText.IndexOf('{');
        if (jsonStart < 0)
        {
            throw new InvalidDataException("The exported avatar pose file is invalid.");
        }
        PoseRoot poses = serializer.Deserialize<PoseRoot>(ExtractJsonObject(poseText, jsonStart));
        if (poses == null || poses.joints == null || poses.joints.Length != 71)
        {
            throw new InvalidDataException("The exported avatar does not contain the expected 71-bone skeleton.");
        }

        var selectedModels = new Dictionary<string, SelectedModel>(StringComparer.OrdinalIgnoreCase);
        foreach (string jsonFile in jsonFiles)
        {
            ExportRoot root = serializer.Deserialize<ExportRoot>(File.ReadAllText(jsonFile));
            if (root == null || root.avatarInfo == null || root.models == null)
            {
                continue;
            }

            // Each category probe contains the complete assembled renderer.
            // The visible model ID itself begins with its component bitmask.
            // This is essential for Xbox style/costume meshes: a racing suit,
            // for example, is returned as one 00000ab8 model even though the
            // per-category asset IDs still name its hidden shirt/trouser/glove
            // constituents. Selecting only avatarInfo.assetId consequently
            // dropped the entire visible costume and its sleeves/gloves.
            foreach (ExportModel model in root.models)
            {
                if (model == null ||
                    model.avatarModel == null ||
                    string.IsNullOrEmpty(model.avatarModel.modelId) ||
                    model.batches == null ||
                    model.batches.Length == 0)
                {
                    continue;
                }
                string category = CategoryFromModelId(model.avatarModel.modelId);
                if (category == null)
                {
                    continue;
                }
                selectedModels[model.avatarModel.modelId] =
                    new SelectedModel(model, category);
            }
        }

        var batches = new List<ConvertedBatch>();
        foreach (SelectedModel selection in selectedModels.Values.OrderBy(value => value.Model.avatarModel.modelId, StringComparer.OrdinalIgnoreCase))
        {
            ExportBatch[] modelBatches = selection.Model.batches;
            bool baseHead = selection.Model.avatarModel.modelId.StartsWith("00000001-", StringComparison.OrdinalIgnoreCase);
            for (int index = 0; index < modelBatches.Length; index++)
            {
                ConvertedBatch batch = ConvertBatch(
                    selection.Model,
                    modelBatches[index],
                    textureFolder,
                    index,
                    selection.Category);
                if (batch != null)
                {
                    batches.Add(batch);
                    if (!baseHead)
                    {
                        batches.AddRange(ConvertMaterialOverlays(
                            selection.Model,
                            modelBatches[index],
                            textureFolder,
                            index,
                            selection.Category));
                    }
                }
                if (baseHead)
                {
                    AddFaceLayers(
                        batches,
                        selection.Model,
                        modelBatches[index],
                        textureFolder,
                        index,
                        selection.Category);
                }
            }
        }
        if (batches.Count == 0)
        {
            throw new InvalidDataException("No drawable avatar mesh batches were exported.");
        }

        using (var stream = File.Create(outputPath))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0x5641434fu);
            // Version 3 preserves the editor's per-bone local pose, including
            // the body-shape scales that distinguish short/tall and slim/heavy
            // avatars, plus component and shader metadata needed to distinguish
            // naked hands from outfit gloves and lower-arm sleeve replacements.
            writer.Write(3);
            writer.Write(poses.joints.Length);
            foreach (PoseJoint joint in poses.joints)
            {
                float[] inverse = InvertRigidTransform(joint.bindPosition, joint.bindRotation);
                foreach (float value in inverse)
                {
                    writer.Write(value);
                }
            }
            foreach (PoseJoint joint in poses.joints)
            {
                float[] local = CreateTransform(joint.local);
                foreach (float value in local)
                {
                    writer.Write(value);
                }
            }
            writer.Write(batches.Count);
            foreach (ConvertedBatch batch in batches)
            {
                WriteBatch(writer, batch);
            }
        }
    }

    private static string CategoryFromModelId(string modelId)
    {
        if (string.IsNullOrEmpty(modelId) || modelId.Length < 8)
        {
            return null;
        }
        string category = modelId.Substring(0, 8);
        uint ignored;
        return uint.TryParse(
            category,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out ignored)
                ? category
                : null;
    }

    private static ConvertedBatch ConvertBatch(
        ExportModel model,
        ExportBatch source,
        string textureFolder,
        int batchIndex,
        string category)
    {
        int vertexCount = source.positions == null ? 0 : source.positions.Length / 3;
        if (vertexCount == 0 || source.indices == null || source.indices.Length < 3 ||
            source.normals == null || source.normals.Length < vertexCount * 3 ||
            source.bindings == null || source.bindings.Length < vertexCount * 4 ||
            source.weights == null || source.weights.Length < vertexCount * 4)
        {
            return null;
        }

        float[] uv = source.uvs != null && source.uvs.Length >= vertexCount * 2
            ? source.uvs
            : new float[vertexCount * 2];
        bool baseHead = model.avatarModel.modelId.StartsWith(
            "00000001-",
            StringComparison.OrdinalIgnoreCase);
        float[] color = !baseHead &&
            source.colors != null && source.colors.Length >= vertexCount * 4
            ? source.colors
            : Enumerable.Repeat(1f, vertexCount * 4).ToArray();

        ShaderParam colorTexture = FindTextureParameter(source, 1);
        ShaderParam intensityTexture = FindTextureParameter(source, 2);
        ShaderParam decalTexture = FindTextureParameter(source, 3);
        byte[] texture = new byte[0];
        if (colorTexture != null)
        {
            uv = UvLayer(source, colorTexture.uvLayer, vertexCount) ?? uv;
        }

        float[] diffuse = { 1f, 1f, 1f };
        ShaderParam colorParameter = null;
        if (source.shaderParams != null)
        {
            // Usage 13 is the face skin tint. Usage 22 is the primary tint for
            // the body, hair and other multi-material meshes. Empty secondary
            // layers export usage 22 as transparent black, so only accept a
            // non-zero value; otherwise textured clothes must stay white and
            // must not be multiplied to black.
            colorParameter = source.shaderParams.FirstOrDefault(
                parameter => parameter.type == 3 && parameter.usage == 13);
            if (colorParameter == null)
            {
                colorParameter = source.shaderParams.FirstOrDefault(
                    parameter =>
                        parameter.type == 3 &&
                        parameter.usage == 22 &&
                        HasNonZeroRgb(parameter.constant));
            }
        }
        if (colorParameter != null && !string.IsNullOrEmpty(colorParameter.constant))
        {
            MatchCollection numbers = Regex.Matches(colorParameter.constant, @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[Ee][-+]?\d+)?");
            if (numbers.Count >= 3)
            {
                for (int index = 0; index < 3; index++)
                {
                    diffuse[index] = float.Parse(numbers[index].Value, CultureInfo.InvariantCulture);
                }
            }
        }

        byte paletteMask = 0;
        float[][] palette =
        {
            new float[4],
            new float[4],
            new float[4]
        };
        for (int paletteIndex = 0; paletteIndex < 3; paletteIndex++)
        {
            int usage = 22 + paletteIndex;
            ShaderParam parameter = source.shaderParams == null
                ? null
                : source.shaderParams.FirstOrDefault(value =>
                    value.type == 3 && value.usage == usage);
            if (parameter != null && TryParseColor(parameter.constant, palette[paletteIndex]))
            {
                paletteMask |= (byte)(1 << paletteIndex);
            }
        }

        if (!baseHead && colorTexture != null)
        {
            string colorPath = TexturePath(
                textureFolder,
                model.avatarModel.modelId,
                colorTexture.textureIndex,
                0);
            if (File.Exists(colorPath))
            {
                texture = File.ReadAllBytes(colorPath);
            }
            if (intensityTexture != null || decalTexture != null)
            {
                // Xbox samples the tint/decal layers with independent UVs.
                // They are emitted as transparent material passes below, so
                // the base atlas itself must remain neutral here.
                diffuse[0] = diffuse[1] = diffuse[2] = 1f;
            }
        }

        uint categoryMask;
        if (!uint.TryParse(category, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out categoryMask))
        {
            throw new InvalidDataException("Avatar component has an invalid category mask: " + category);
        }

        return new ConvertedBatch
        {
            Name = model.avatarModel.modelId + ":" + (source.name ?? "mesh") + ":" + batchIndex,
            CategoryMask = categoryMask,
            ShaderId = source.batchInfo == null ? -1 : source.batchInfo.shaderId,
            PaletteMask = paletteMask,
            Palette = palette,
            VertexCount = vertexCount,
            Positions = source.positions,
            Normals = source.normals,
            Uv = uv,
            Bindings = source.bindings,
            Weights = source.weights,
            Colors = color,
            Indices = source.indices,
            Diffuse = diffuse,
            Texture = texture
        };
    }

    private static ShaderParam FindTextureParameter(ExportBatch source, int usage)
    {
        return source.shaderParams == null
            ? null
            : source.shaderParams.FirstOrDefault(parameter =>
                parameter.type == 1 && parameter.usage == usage);
    }

    private static IEnumerable<ConvertedBatch> ConvertMaterialOverlays(
        ExportModel model,
        ExportBatch source,
        string textureFolder,
        int batchIndex,
        string category)
    {
        int vertexCount = source.positions == null
            ? 0
            : source.positions.Length / 3;
        if (vertexCount == 0)
        {
            yield break;
        }

        uint categoryMask;
        if (!uint.TryParse(
            category,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out categoryMask))
        {
            yield break;
        }

        float[][] palette =
        {
            new float[4],
            new float[4],
            new float[4]
        };
        for (int paletteIndex = 0; paletteIndex < 3; paletteIndex++)
        {
            ShaderParam parameter = source.shaderParams == null
                ? null
                : source.shaderParams.FirstOrDefault(value =>
                    value.type == 3 &&
                    value.usage == 22 + paletteIndex);
            if (parameter != null)
            {
                TryParseColor(parameter.constant, palette[paletteIndex]);
            }
        }

        ShaderParam intensity = FindTextureParameter(source, 2);
        if (intensity != null)
        {
            string path = TexturePath(
                textureFolder,
                model.avatarModel.modelId,
                intensity.textureIndex,
                0);
            float[] uv = UvLayer(source, intensity.uvLayer, vertexCount);
            if (uv != null && File.Exists(path))
            {
                yield return CreateMaterialOverlayBatch(
                    model,
                    source,
                    batchIndex,
                    categoryMask,
                    "palette",
                    uv,
                    BuildPaletteOverlayTexture(path, palette));
            }
        }

        ShaderParam decal = FindTextureParameter(source, 3);
        if (decal != null)
        {
            string path = TexturePath(
                textureFolder,
                model.avatarModel.modelId,
                decal.textureIndex,
                0);
            float[] uv = UvLayer(source, decal.uvLayer, vertexCount);
            if (uv != null && File.Exists(path))
            {
                yield return CreateMaterialOverlayBatch(
                    model,
                    source,
                    batchIndex,
                    categoryMask,
                    "decal",
                    uv,
                    File.ReadAllBytes(path));
            }
        }
    }

    private static ConvertedBatch CreateMaterialOverlayBatch(
        ExportModel model,
        ExportBatch source,
        int batchIndex,
        uint categoryMask,
        string layer,
        float[] uv,
        byte[] texture)
    {
        int vertexCount = source.positions.Length / 3;
        return new ConvertedBatch
        {
            Name = model.avatarModel.modelId + ":" +
                (source.name ?? "mesh") + ":" + batchIndex +
                ":material-overlay-" + layer,
            CategoryMask = categoryMask,
            ShaderId = source.batchInfo == null
                ? -1
                : source.batchInfo.shaderId,
            PaletteMask = 0,
            Palette = new[]
            {
                new float[4],
                new float[4],
                new float[4]
            },
            VertexCount = vertexCount,
            Positions = source.positions,
            Normals = source.normals,
            Uv = uv,
            Bindings = source.bindings,
            Weights = source.weights,
            Colors = Enumerable.Repeat(1f, vertexCount * 4).ToArray(),
            Indices = source.indices,
            Diffuse = new[] { 1f, 1f, 1f },
            Texture = texture
        };
    }

    private static byte[] BuildPaletteOverlayTexture(
        string texturePath,
        float[][] palette)
    {
        using (var bitmap = new Bitmap(texturePath))
        {
            PixelImage source = PixelImage.FromBitmap(bitmap);
            PixelImage output = source.Clone();
            for (int y = 0; y < source.Height; y++)
            {
                for (int x = 0; x < source.Width; x++)
                {
                    float red = source.Component(x, y, 0);
                    float green = source.Component(x, y, 1);
                    float blue = source.Component(x, y, 2);
                    float alpha = source.Component(x, y, 3);
                    output.SetPixel(x, y, new[]
                    {
                        red * palette[0][0] +
                            green * palette[1][0] +
                            blue * palette[2][0],
                        red * palette[0][1] +
                            green * palette[1][1] +
                            blue * palette[2][1],
                        red * palette[0][2] +
                            green * palette[1][2] +
                            blue * palette[2][2],
                        alpha
                    });
                }
            }
            return output.ToPng();
        }
    }

    private static byte[] BakeMaterialTexture(
        string modelId,
        ExportBatch source,
        string textureFolder,
        ShaderParam colorParameter,
        ShaderParam intensityParameter,
        ShaderParam decalParameter,
        float[][] palette)
    {
        string colorPath = TexturePath(
            textureFolder,
            modelId,
            colorParameter.textureIndex,
            0);
        if (!File.Exists(colorPath))
        {
            return new byte[0];
        }

        string intensityPath = intensityParameter == null
            ? null
            : TexturePath(
                textureFolder,
                modelId,
                intensityParameter.textureIndex,
                0);
        string decalPath = decalParameter == null
            ? null
            : TexturePath(
                textureFolder,
                modelId,
                decalParameter.textureIndex,
                0);
        bool hasIntensity = !string.IsNullOrEmpty(intensityPath) &&
            File.Exists(intensityPath);
        bool hasDecal = !string.IsNullOrEmpty(decalPath) &&
            File.Exists(decalPath);
        if (!hasIntensity && !hasDecal)
        {
            return File.ReadAllBytes(colorPath);
        }

        int vertexCount = source.positions == null
            ? 0
            : source.positions.Length / 3;
        float[] colorUv = UvLayer(
            source,
            colorParameter.uvLayer,
            vertexCount);
        float[] intensityUv = hasIntensity
            ? UvLayer(source, intensityParameter.uvLayer, vertexCount)
            : null;
        float[] decalUv = hasDecal
            ? UvLayer(source, decalParameter.uvLayer, vertexCount)
            : null;
        if (colorUv == null)
        {
            return File.ReadAllBytes(colorPath);
        }

        using (var colorBitmap = new Bitmap(colorPath))
        using (var intensityBitmap = hasIntensity ? new Bitmap(intensityPath) : null)
        using (var decalBitmap = hasDecal ? new Bitmap(decalPath) : null)
        {
            PixelImage color = PixelImage.FromBitmap(colorBitmap);
            PixelImage intensity = intensityBitmap == null
                ? null
                : PixelImage.FromBitmap(intensityBitmap);
            PixelImage decal = decalBitmap == null
                ? null
                : PixelImage.FromBitmap(decalBitmap);
            // The Xbox shader samples its decal atlas independently from the
            // base clothing atlas. Flattening both into the base resolution
            // discarded fine lettering and glove/outfit details. A 2x bake
            // retains the effective decal resolution while keeping the game
            // runtime on its simple, single-texture BasicEffect path.
            PixelImage result = hasIntensity || hasDecal
                ? color.CloneScaled(2)
                : color.Clone();
            int indexCount = source.indices == null
                ? 0
                : source.indices.Length - source.indices.Length % 3;
            for (int triangle = 0; triangle < indexCount; triangle += 3)
            {
                int index0 = source.indices[triangle];
                int index1 = source.indices[triangle + 1];
                int index2 = source.indices[triangle + 2];
                if (index0 < 0 || index0 >= vertexCount ||
                    index1 < 0 || index1 >= vertexCount ||
                    index2 < 0 || index2 >= vertexCount)
                {
                    continue;
                }
                BakeMaterialTriangle(
                    result,
                    color,
                    intensity,
                    decal,
                    palette,
                    colorUv,
                    intensityUv,
                    decalUv,
                    index0,
                    index1,
                    index2);
            }
            return result.ToPng();
        }
    }

    private static void BakeMaterialTriangle(
        PixelImage result,
        PixelImage color,
        PixelImage intensity,
        PixelImage decal,
        float[][] palette,
        float[] colorUv,
        float[] intensityUv,
        float[] decalUv,
        int index0,
        int index1,
        int index2)
    {
        float u0 = colorUv[index0 * 2];
        float v0 = colorUv[index0 * 2 + 1];
        float u1 = UnwrapNear(colorUv[index1 * 2], u0);
        float v1 = UnwrapNear(colorUv[index1 * 2 + 1], v0);
        float u2 = UnwrapNear(colorUv[index2 * 2], u0);
        float v2 = UnwrapNear(colorUv[index2 * 2 + 1], v0);
        float uShift = -(float)Math.Floor((u0 + u1 + u2) / 3f);
        float vShift = -(float)Math.Floor((v0 + v1 + v2) / 3f);
        u0 += uShift;
        u1 += uShift;
        u2 += uShift;
        v0 += vShift;
        v1 += vShift;
        v2 += vShift;
        float x0 = u0 * (result.Width - 1);
        float y0 = (1f - v0) * (result.Height - 1);
        float x1 = u1 * (result.Width - 1);
        float y1 = (1f - v1) * (result.Height - 1);
        float x2 = u2 * (result.Width - 1);
        float y2 = (1f - v2) * (result.Height - 1);
        float denominator =
            (y1 - y2) * (x0 - x2) +
            (x2 - x1) * (y0 - y2);
        if (Math.Abs(denominator) <= 0.00001f)
        {
            return;
        }

        // Xbox clothing UV islands are allowed to cross a texture boundary.
        // Clamping the raster bounds discarded the wrapped half of decals
        // (for example the "ORI" in DMG MORI). Rasterize in unwrapped space,
        // then address the destination atlas with repeat semantics just like
        // the native avatar sampler.
        int minX = (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2)));
        int maxX = (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2)));
        int minY = (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2)));
        int maxY = (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2)));
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float sampleX = x + 0.5f;
                float sampleY = y + 0.5f;
                float weight0 =
                    ((y1 - y2) * (sampleX - x2) +
                     (x2 - x1) * (sampleY - y2)) / denominator;
                float weight1 =
                    ((y2 - y0) * (sampleX - x2) +
                     (x0 - x2) * (sampleY - y2)) / denominator;
                float weight2 = 1f - weight0 - weight1;
                const float edgeTolerance = -0.002f;
                if (weight0 < edgeTolerance ||
                    weight1 < edgeTolerance ||
                    weight2 < edgeTolerance)
                {
                    continue;
                }

                float colorU = InterpolateUv(
                    colorUv,
                    index0,
                    index1,
                    index2,
                    weight0,
                    weight1,
                    weight2,
                    0);
                float colorV = InterpolateUv(
                    colorUv,
                    index0,
                    index1,
                    index2,
                    weight0,
                    weight1,
                    weight2,
                    1);
                float[] output = SampleBitmap(color, colorU, colorV);
                if (intensity != null && intensityUv != null)
                {
                    float intensityU = InterpolateUv(
                        intensityUv,
                        index0,
                        index1,
                        index2,
                        weight0,
                        weight1,
                        weight2,
                        0);
                    float intensityV = InterpolateUv(
                        intensityUv,
                        index0,
                        index1,
                        index2,
                        weight0,
                        weight1,
                        weight2,
                        1);
                    float[] mask = SampleBitmap(
                        intensity,
                        intensityU,
                        intensityV);
                    for (int component = 0; component < 3; component++)
                    {
                        float custom =
                            mask[0] * palette[0][component] +
                            mask[1] * palette[1][component] +
                            mask[2] * palette[2][component];
                        output[component] +=
                            (custom - output[component]) * mask[3];
                    }
                }
                if (decal != null && decalUv != null)
                {
                    float decalU = InterpolateUv(
                        decalUv,
                        index0,
                        index1,
                        index2,
                        weight0,
                        weight1,
                        weight2,
                        0);
                    float decalV = InterpolateUv(
                        decalUv,
                        index0,
                        index1,
                        index2,
                        weight0,
                        weight1,
                        weight2,
                        1);
                    float[] overlay = SampleBitmap(decal, decalU, decalV);
                    for (int component = 0; component < 3; component++)
                    {
                        output[component] +=
                            (overlay[component] - output[component]) *
                            overlay[3];
                    }
                }
                int destinationX = x % result.Width;
                int destinationY = y % result.Height;
                if (destinationX < 0)
                {
                    destinationX += result.Width;
                }
                if (destinationY < 0)
                {
                    destinationY += result.Height;
                }
                result.SetPixel(destinationX, destinationY, output);
            }
        }
    }

    private static float UnwrapNear(float value, float reference)
    {
        while (value - reference > 0.5f)
        {
            value -= 1f;
        }
        while (value - reference < -0.5f)
        {
            value += 1f;
        }
        return value;
    }

    private static float InterpolateUv(
        float[] uv,
        int index0,
        int index1,
        int index2,
        float weight0,
        float weight1,
        float weight2,
        int component)
    {
        float value0 = uv[index0 * 2 + component];
        float value1 = uv[index1 * 2 + component];
        float value2 = uv[index2 * 2 + component];
        // The GPU interpolates the authored coordinates as-is and applies
        // wrap addressing only when sampling. Choosing the numerically
        // shortest wrapped edge folds large decal projections and crops logos.
        return
            value0 * weight0 +
            value1 * weight1 +
            value2 * weight2;
    }

    private static float[] SampleBitmap(PixelImage bitmap, float u, float v)
    {
        u -= (float)Math.Floor(u);
        v -= (float)Math.Floor(v);
        float x = u * (bitmap.Width - 1);
        float y = (1f - v) * (bitmap.Height - 1);
        int x0 = Math.Max(0, Math.Min(bitmap.Width - 1, (int)Math.Floor(x)));
        int y0 = Math.Max(0, Math.Min(bitmap.Height - 1, (int)Math.Floor(y)));
        int x1 = Math.Min(bitmap.Width - 1, x0 + 1);
        int y1 = Math.Min(bitmap.Height - 1, y0 + 1);
        float tx = x - x0;
        float ty = y - y0;
        var result = new float[4];
        for (int component = 0; component < 4; component++)
        {
            float value00 = bitmap.Component(x0, y0, component);
            float value10 = bitmap.Component(x1, y0, component);
            float value01 = bitmap.Component(x0, y1, component);
            float value11 = bitmap.Component(x1, y1, component);
            float top = value00 + (value10 - value00) * tx;
            float bottom = value01 + (value11 - value01) * tx;
            result[component] = top + (bottom - top) * ty;
        }
        return result;
    }

    private static string TexturePath(
        string textureFolder,
        string modelId,
        int textureIndex,
        int frame)
    {
        return Path.Combine(
            textureFolder,
            modelId + "_" + textureIndex + "_" + frame + ".png");
    }

    private static void AddFaceLayers(
        List<ConvertedBatch> output,
        ExportModel model,
        ExportBatch source,
        string textureFolder,
        int batchIndex,
        string category)
    {
        if (source == null || source.shaderParams == null)
        {
            return;
        }

        // This is the exact layer order used by Microsoft's embedded head
        // pixel shader: skin features, eye shadow, mouth, eyes, facial hair,
        // then eyebrows. The Original Avatar head is split into left/right
        // geometry and each half supplies its own UVs for the asymmetric
        // layers.
        int[] usages = { 11, 6, 12, 9, 10, 5, 7, 8 };
        foreach (int usage in usages)
        {
            ShaderParam textureParameter = source.shaderParams.FirstOrDefault(
                parameter => parameter.type == 1 && parameter.usage == usage);
            if (textureParameter == null)
            {
                continue;
            }

            int vertexCount = source.positions == null
                ? 0
                : source.positions.Length / 3;
            float[] uv = UvLayer(
                source,
                textureParameter.uvLayer,
                vertexCount);
            if (vertexCount == 0 || uv == null)
            {
                continue;
            }

            float[] skin = FaceTint(source, 13);
            float[] primaryTint = FaceTint(
                source,
                FaceColorUsage(usage));
            float[] secondaryTint = usage == 11
                ? FaceTint(source, 21)
                : skin;
            uint categoryMask;
            if (!uint.TryParse(
                category,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out categoryMask))
            {
                throw new InvalidDataException(
                    "Avatar component has an invalid category mask: " + category);
            }

            int frameCount = usage == 11 || usage == 5 || usage == 6
                ? 1
                : usage == 7 || usage == 8
                    ? 5
                    : 14;
            for (int frame = 0; frame < frameCount; frame++)
            {
                string texturePath = Path.Combine(
                    textureFolder,
                    model.avatarModel.modelId + "_" +
                        textureParameter.textureIndex + "_" + frame + ".png");
                if (!File.Exists(texturePath))
                {
                    continue;
                }
                byte[] texture = BakeFaceLayer(
                    texturePath,
                    usage,
                    skin,
                    primaryTint,
                    secondaryTint);
                if (texture.Length == 0)
                {
                    continue;
                }

                output.Add(new ConvertedBatch
                {
                    Name = model.avatarModel.modelId + ":face-layer-" +
                        usage + "-frame-" + frame + ":" + batchIndex,
                    CategoryMask = categoryMask,
                    ShaderId = source.batchInfo == null ? -1 : source.batchInfo.shaderId,
                    PaletteMask = 0,
                    Palette = new[] { new float[4], new float[4], new float[4] },
                    VertexCount = vertexCount,
                    Positions = source.positions,
                    Normals = source.normals,
                    // Face-mask UVs are exported in the PNG's top-left image
                    // convention already. Pre-invert here because WriteBatch
                    // performs the general mesh-texture V conversion below.
                    Uv = InvertV(uv),
                    Bindings = source.bindings,
                    Weights = source.weights,
                    Colors = Enumerable.Repeat(1f, vertexCount * 4).ToArray(),
                    Indices = source.indices,
                    Diffuse = new[] { 1f, 1f, 1f },
                    Texture = texture
                });
            }
        }
    }

    private static float[] InvertV(float[] source)
    {
        float[] result = (float[])source.Clone();
        for (int index = 1; index < result.Length; index += 2)
        {
            result[index] = 1f - result[index];
        }
        return result;
    }

    private static int FaceColorUsage(int textureUsage)
    {
        switch (textureUsage)
        {
            case 5:
                return 19; // FacialHair
            case 6:
                return 18; // EyeShadow
            case 7:
            case 8:
                return 17; // Eyebrow
            case 9:
            case 10:
                return 16; // Iris
            case 12:
                return 15; // Mouth
            default:
                return 20; // SkinFeatures1
        }
    }

    private static float[] FaceTint(ExportBatch source, int usage)
    {
        float[] result = { 0.12f, 0.08f, 0.05f, 1f };
        ShaderParam parameter = source.shaderParams.FirstOrDefault(
            value => value.type == 3 && value.usage == usage);
        if (parameter != null)
        {
            TryParseColor(parameter.constant, result);
        }
        return result;
    }

    private static byte[] BakeFaceLayer(
        string texturePath,
        int usage,
        float[] skin,
        float[] primaryTint,
        float[] secondaryTint)
    {
        using (var source = new Bitmap(texturePath))
        using (var result = new Bitmap(
            source.Width,
            source.Height,
            PixelFormat.Format32bppArgb))
        {
            bool anyVisible = false;
            for (int y = 0; y < source.Height; y++)
            {
                for (int x = 0; x < source.Width; x++)
                {
                    System.Drawing.Color input = source.GetPixel(x, y);
                    if (input.A == 0)
                    {
                        result.SetPixel(x, y, System.Drawing.Color.Transparent);
                        continue;
                    }

                    anyVisible = true;
                    float sourceRed = input.R / 255f;
                    float sourceGreen = input.G / 255f;
                    float sourceBlue = input.B / 255f;
                    var output = new float[3];
                    for (int component = 0; component < 3; component++)
                    {
                        if (usage == 11)
                        {
                            // SkinFeatures: R=feature color 1,
                            // G=feature color 2, B=base skin.
                            output[component] =
                                sourceRed * primaryTint[component] +
                                sourceGreen * secondaryTint[component] +
                                sourceBlue * skin[component];
                        }
                        else if (usage == 6)
                        {
                            // Eye shadow uses only the red mask channel.
                            output[component] =
                                sourceRed * primaryTint[component];
                        }
                        else
                        {
                            // Mouth, eye, facial-hair and eyebrow masks use
                            // R=selected tint, G=neutral white and B=skin.
                            // This is the instruction sequence in the original
                            // Xbox head shader, including sclera and lip detail.
                            output[component] =
                                sourceRed * primaryTint[component] +
                                sourceGreen +
                                sourceBlue * skin[component];
                        }
                    }
                    result.SetPixel(
                        x,
                        y,
                        System.Drawing.Color.FromArgb(
                            input.A,
                            ClampToByte(output[0] * 255f),
                            ClampToByte(output[1] * 255f),
                            ClampToByte(output[2] * 255f)));
                }
            }

            if (!anyVisible)
            {
                return new byte[0];
            }
            using (var stream = new MemoryStream())
            {
                result.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }
    }

    private static bool TryParseColor(string constant, float[] result)
    {
        if (string.IsNullOrEmpty(constant) || result == null || result.Length < 4)
        {
            return false;
        }
        MatchCollection numbers = Regex.Matches(
            constant,
            @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[Ee][-+]?\d+)?");
        if (numbers.Count < 3)
        {
            return false;
        }
        for (int index = 0; index < 4; index++)
        {
            result[index] = index < numbers.Count
                ? float.Parse(numbers[index].Value, CultureInfo.InvariantCulture)
                : 1f;
        }
        return true;
    }

    private static bool HasNonZeroRgb(string constant)
    {
        if (string.IsNullOrEmpty(constant))
        {
            return false;
        }
        MatchCollection numbers = Regex.Matches(
            constant,
            @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[Ee][-+]?\d+)?");
        if (numbers.Count < 3)
        {
            return false;
        }
        for (int index = 0; index < 3; index++)
        {
            if (Math.Abs(float.Parse(
                numbers[index].Value,
                CultureInfo.InvariantCulture)) > 0.0001f)
            {
                return true;
            }
        }
        return false;
    }

    private static float[] UvLayer(ExportBatch batch, int layer, int vertexCount)
    {
        float[] result = null;
        switch (layer)
        {
            case 0: result = batch.uvs; break;
            case 1: result = batch.uvs2; break;
            case 2: result = batch.uvs3; break;
            case 3: result = batch.uvs4; break;
            case 4: result = batch.uvs5; break;
            case 5: result = batch.uvs6; break;
        }
        return result != null && result.Length >= vertexCount * 2 ? result : null;
    }

    private static void WriteBatch(BinaryWriter writer, ConvertedBatch batch)
    {
        writer.Write(batch.Name);
        writer.Write(batch.CategoryMask);
        writer.Write(batch.ShaderId);
        writer.Write(batch.PaletteMask);
        for (int paletteIndex = 0; paletteIndex < 3; paletteIndex++)
        {
            for (int component = 0; component < 4; component++)
            {
                writer.Write(batch.Palette[paletteIndex][component]);
            }
        }
        writer.Write(batch.VertexCount);
        for (int vertex = 0; vertex < batch.VertexCount; vertex++)
        {
            for (int component = 0; component < 3; component++) writer.Write(batch.Positions[vertex * 3 + component]);
            for (int component = 0; component < 3; component++) writer.Write(batch.Normals[vertex * 3 + component]);
            writer.Write(batch.Uv[vertex * 2]);
            writer.Write(1f - batch.Uv[vertex * 2 + 1]);
            for (int component = 0; component < 4; component++) writer.Write((byte)ClampToByte(batch.Bindings[vertex * 4 + component]));
            for (int component = 0; component < 4; component++) writer.Write((byte)ClampToByte(batch.Weights[vertex * 4 + component]));
            for (int component = 0; component < 4; component++) writer.Write((byte)ClampToByte(batch.Colors[vertex * 4 + component] * 255f));
        }
        int indexCount = batch.Indices.Length - batch.Indices.Length % 3;
        writer.Write(indexCount);
        for (int index = 0; index < indexCount; index++)
        {
            int value = batch.Indices[index];
            if (value < 0 || value >= batch.VertexCount || value > ushort.MaxValue)
            {
                throw new InvalidDataException("Avatar mesh contains an invalid vertex index.");
            }
            writer.Write((ushort)value);
        }
        writer.Write(batch.Diffuse[0]);
        writer.Write(batch.Diffuse[1]);
        writer.Write(batch.Diffuse[2]);
        writer.Write(batch.Texture.Length);
        writer.Write(batch.Texture);
    }

    private static float[] InvertRigidTransform(float[] position, float[] rotation)
    {
        if (position == null || position.Length < 3 || rotation == null || rotation.Length < 4)
        {
            throw new InvalidDataException("Avatar pose contains an invalid joint transform.");
        }
        float x = rotation[0], y = rotation[1], z = rotation[2], w = rotation[3];
        float xx = x * x, yy = y * y, zz = z * z;
        float xy = x * y, xz = x * z, yz = y * z;
        float wx = w * x, wy = w * y, wz = w * z;
        float[] rotationMatrix =
        {
            1f - 2f * (yy + zz), 2f * (xy + wz), 2f * (xz - wy), 0f,
            2f * (xy - wz), 1f - 2f * (xx + zz), 2f * (yz + wx), 0f,
            2f * (xz + wy), 2f * (yz - wx), 1f - 2f * (xx + yy), 0f,
            position[0], position[1], position[2], 1f
        };
        float[] inverse =
        {
            rotationMatrix[0], rotationMatrix[4], rotationMatrix[8], 0f,
            rotationMatrix[1], rotationMatrix[5], rotationMatrix[9], 0f,
            rotationMatrix[2], rotationMatrix[6], rotationMatrix[10], 0f,
            0f, 0f, 0f, 1f
        };
        inverse[12] = -(position[0] * inverse[0] + position[1] * inverse[4] + position[2] * inverse[8]);
        inverse[13] = -(position[0] * inverse[1] + position[1] * inverse[5] + position[2] * inverse[9]);
        inverse[14] = -(position[0] * inverse[2] + position[1] * inverse[6] + position[2] * inverse[10]);
        return inverse;
    }

    private static float[] CreateTransform(PoseTransform transform)
    {
        if (transform == null ||
            transform.position == null || transform.position.Length < 3 ||
            transform.rotation == null || transform.rotation.Length < 4 ||
            transform.scale == null || transform.scale.Length < 3)
        {
            throw new InvalidDataException("Avatar pose contains an invalid local transform.");
        }

        float x = transform.rotation[0];
        float y = transform.rotation[1];
        float z = transform.rotation[2];
        float w = transform.rotation[3];
        float xx = x * x, yy = y * y, zz = z * z;
        float xy = x * y, xz = x * z, yz = y * z;
        float wx = w * x, wy = w * y, wz = w * z;
        float sx = transform.scale[0];
        float sy = transform.scale[1];
        float sz = transform.scale[2];
        return new[]
        {
            (1f - 2f * (yy + zz)) * sx,
            (2f * (xy + wz)) * sx,
            (2f * (xz - wy)) * sx,
            0f,
            (2f * (xy - wz)) * sy,
            (1f - 2f * (xx + zz)) * sy,
            (2f * (yz + wx)) * sy,
            0f,
            (2f * (xz + wy)) * sz,
            (2f * (yz - wx)) * sz,
            (1f - 2f * (xx + yy)) * sz,
            0f,
            transform.position[0],
            transform.position[1],
            transform.position[2],
            1f
        };
    }

    private static int ClampToByte(float value)
    {
        return (int)Math.Max(0, Math.Min(255, Math.Round(value)));
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static string LastLines(string text, int count)
    {
        string[] lines = text.Replace("\r", "").Split('\n');
        return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - count)).ToArray());
    }

    private static string ExtractJsonObject(string text, int start)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int index = start; index < text.Length; index++)
        {
            char character = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }
                continue;
            }
            if (character == '"')
            {
                inString = true;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text.Substring(start, index - start + 1);
                }
            }
        }
        throw new InvalidDataException("The exported avatar pose JSON is incomplete.");
    }

    private sealed class PixelImage
    {
        internal readonly int Width;
        internal readonly int Height;
        private readonly byte[] _pixels;

        private PixelImage(int width, int height, byte[] pixels)
        {
            Width = width;
            Height = height;
            _pixels = pixels;
        }

        internal static PixelImage FromBitmap(Bitmap source)
        {
            using (var converted = new Bitmap(
                source.Width,
                source.Height,
                PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(converted))
                {
                    graphics.DrawImageUnscaled(source, 0, 0);
                }
                Rectangle rectangle = new Rectangle(
                    0,
                    0,
                    converted.Width,
                    converted.Height);
                BitmapData data = converted.LockBits(
                    rectangle,
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);
                try
                {
                    int rowLength = converted.Width * 4;
                    var pixels = new byte[rowLength * converted.Height];
                    for (int y = 0; y < converted.Height; y++)
                    {
                        Marshal.Copy(
                            IntPtr.Add(data.Scan0, y * data.Stride),
                            pixels,
                            y * rowLength,
                            rowLength);
                    }
                    return new PixelImage(
                        converted.Width,
                        converted.Height,
                        pixels);
                }
                finally
                {
                    converted.UnlockBits(data);
                }
            }
        }

        internal PixelImage Clone()
        {
            return new PixelImage(
                Width,
                Height,
                (byte[])_pixels.Clone());
        }

        internal PixelImage CloneScaled(int scale)
        {
            if (scale <= 1)
            {
                return Clone();
            }

            int scaledWidth = checked(Width * scale);
            int scaledHeight = checked(Height * scale);
            var scaled = new PixelImage(
                scaledWidth,
                scaledHeight,
                new byte[checked(scaledWidth * scaledHeight * 4)]);
            for (int y = 0; y < scaledHeight; y++)
            {
                float sourceY = ((y + 0.5f) / scale) - 0.5f;
                int y0 = Math.Max(0, (int)Math.Floor(sourceY));
                int y1 = Math.Min(Height - 1, y0 + 1);
                float ty = Math.Max(0f, Math.Min(1f, sourceY - y0));
                for (int x = 0; x < scaledWidth; x++)
                {
                    float sourceX = ((x + 0.5f) / scale) - 0.5f;
                    int x0 = Math.Max(0, (int)Math.Floor(sourceX));
                    int x1 = Math.Min(Width - 1, x0 + 1);
                    float tx = Math.Max(0f, Math.Min(1f, sourceX - x0));
                    var color = new float[4];
                    for (int component = 0; component < 4; component++)
                    {
                        float top = Component(x0, y0, component) +
                            (Component(x1, y0, component) -
                             Component(x0, y0, component)) * tx;
                        float bottom = Component(x0, y1, component) +
                            (Component(x1, y1, component) -
                             Component(x0, y1, component)) * tx;
                        color[component] = top + (bottom - top) * ty;
                    }
                    scaled.SetPixel(x, y, color);
                }
            }
            return scaled;
        }

        internal float Component(int x, int y, int component)
        {
            int offset = (y * Width + x) * 4;
            switch (component)
            {
                case 0: return _pixels[offset + 2] / 255f;
                case 1: return _pixels[offset + 1] / 255f;
                case 2: return _pixels[offset] / 255f;
                default: return _pixels[offset + 3] / 255f;
            }
        }

        internal void SetPixel(int x, int y, float[] color)
        {
            int offset = (y * Width + x) * 4;
            _pixels[offset] = (byte)ClampToByte(color[2] * 255f);
            _pixels[offset + 1] = (byte)ClampToByte(color[1] * 255f);
            _pixels[offset + 2] = (byte)ClampToByte(color[0] * 255f);
            _pixels[offset + 3] = (byte)ClampToByte(color[3] * 255f);
        }

        internal byte[] ToPng()
        {
            using (var bitmap = new Bitmap(
                Width,
                Height,
                PixelFormat.Format32bppArgb))
            {
                Rectangle rectangle = new Rectangle(0, 0, Width, Height);
                BitmapData data = bitmap.LockBits(
                    rectangle,
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);
                try
                {
                    int rowLength = Width * 4;
                    for (int y = 0; y < Height; y++)
                    {
                        Marshal.Copy(
                            _pixels,
                            y * rowLength,
                            IntPtr.Add(data.Scan0, y * data.Stride),
                            rowLength);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
                using (var stream = new MemoryStream())
                {
                    bitmap.Save(stream, ImageFormat.Png);
                    return stream.ToArray();
                }
            }
        }
    }

    private sealed class SelectedModel
    {
        internal readonly ExportModel Model;
        internal readonly string Category;
        internal SelectedModel(ExportModel model, string category) { Model = model; Category = category; }
    }

    private sealed class ConvertedBatch
    {
        internal string Name;
        internal uint CategoryMask;
        internal int ShaderId;
        internal byte PaletteMask;
        internal float[][] Palette;
        internal int VertexCount;
        internal float[] Positions;
        internal float[] Normals;
        internal float[] Uv;
        internal int[] Bindings;
        internal int[] Weights;
        internal float[] Colors;
        internal int[] Indices;
        internal float[] Diffuse;
        internal byte[] Texture;
    }

    public sealed class PoseRoot { public PoseJoint[] joints { get; set; } }
    public sealed class PoseJoint
    {
        public float[] bindPosition { get; set; }
        public float[] bindRotation { get; set; }
        public PoseTransform local { get; set; }
        public int parent { get; set; }
    }
    public sealed class PoseTransform
    {
        public float[] position { get; set; }
        public float[] rotation { get; set; }
        public float[] scale { get; set; }
    }
    public sealed class ExportRoot
    {
        public AvatarInfo avatarInfo { get; set; }
        public ExportModel[] models { get; set; }
    }
    public sealed class AvatarInfo { public string assetId { get; set; } }
    public sealed class ExportModel
    {
        public AvatarModelInfo avatarModel { get; set; }
        public ExportBatch[] batches { get; set; }
    }
    public sealed class AvatarModelInfo
    {
        public int exportedModelIdx { get; set; }
        public string modelId { get; set; }
    }
    public sealed class ExportBatch
    {
        public BatchInfo batchInfo { get; set; }
        public string name { get; set; }
        public int[] indices { get; set; }
        public ShaderParam[] shaderParams { get; set; }
        public float[] positions { get; set; }
        public float[] normals { get; set; }
        public int[] bindings { get; set; }
        public int[] weights { get; set; }
        public float[] colors { get; set; }
        public float[] uvs { get; set; }
        public float[] uvs2 { get; set; }
        public float[] uvs3 { get; set; }
        public float[] uvs4 { get; set; }
        public float[] uvs5 { get; set; }
        public float[] uvs6 { get; set; }
    }
    public sealed class BatchInfo
    {
        public int shaderId { get; set; }
    }
    public sealed class ShaderParam
    {
        public int type { get; set; }
        public int usage { get; set; }
        public int textureIndex { get; set; }
        public int uvLayer { get; set; }
        public string constant { get; set; }
    }
}
