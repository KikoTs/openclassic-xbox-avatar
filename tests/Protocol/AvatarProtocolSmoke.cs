using System;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class AvatarProtocolSmoke
{
    private const BindingFlags Hidden =
        BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;

    private static int Main(string[] args)
    {
        if (args.Length < 2 || args.Length > 3)
        {
            Console.Error.WriteLine(
                "Usage: AvatarProtocolSmoke <mod.dll> <avatar.ocavatar> [game folder]");
            return 2;
        }

        // The mod references the game assembly, so resolving its types needs the
        // client on hand. Default to the folder the avatar was loaded from,
        // which is inside the game folder in a real install.
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

        Assembly mod = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        Type packetType = ModType(mod, "ZZAvatarSyncMessage");
        object packet = Activator.CreateInstance(packetType, true);
        MethodInfo receive = packetType.GetMethod("RecieveData", Hidden);
        MethodInfo send = packetType.GetMethod("SendData", Hidden);

        byte[] payload = Enumerable.Range(0, 3000)
            .Select(index => (byte)(index * 17))
            .ToArray();
        Set(packetType, packet, "Protocol", (byte)1);
        Set(packetType, packet, "Kind", (byte)4);
        Set(packetType, packet, "TransferId", 0x12345678u);
        Set(packetType, packet, "TotalLength", 6000);
        Set(packetType, packet, "ChunkIndex", (ushort)0);
        Set(packetType, packet, "ChunkCount", (ushort)2);
        Set(packetType, packet, "Hash", Enumerable.Repeat((byte)0x5a, 32).ToArray());
        Set(packetType, packet, "Payload", payload);

        byte[] serialized;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            send.Invoke(packet, new object[] { writer });
            writer.Flush();
            serialized = stream.ToArray();
        }
        Require(serialized.Length == 3048, "maximum chunk is not 3048 bytes");
        object roundTrip = Activator.CreateInstance(packetType, true);
        using (var stream = new MemoryStream(serialized))
        using (var reader = new BinaryReader(stream))
        {
            receive.Invoke(roundTrip, new object[] { reader });
        }
        byte[] restored = (byte[])Get(packetType, roundTrip, "Payload");
        Require(payload.SequenceEqual(restored), "packet round-trip changed payload");

        byte[] oversized = (byte[])serialized.Clone();
        oversized[46] = 0xb9;
        oversized[47] = 0x0b; // 3001
        ExpectFailure(receive, packetType, oversized, typeof(InvalidDataException));
        ExpectFailure(
            receive,
            packetType,
            serialized.Take(40).ToArray(),
            typeof(EndOfStreamException));

        Type assetType = ModType(mod, "AvatarAsset");
        object asset = assetType.GetMethod("Load", Hidden).Invoke(
            null,
            new object[] { Path.GetFullPath(args[1]) });
        object body = Get(assetType, asset, "BaseBodyBatch");
        object glove = Get(assetType, asset, "OuterHandBatch");
        System.Collections.ICollection gloveBatches =
            (System.Collections.ICollection)Get(
                assetType,
                asset,
                "OuterHandBatches");
        Require(body != null, "v3 asset has no base body");
        Require(glove != null, "v3 asset did not classify outfit glove");
        Require(gloveBatches.Count >= 1,
            "combined outfit exposed no glove material batches");
        Type batchType = glove.GetType();
        uint gloveCategory = (uint)Get(batchType, glove, "CategoryMask");
        Require((gloveCategory & 0x80u) != 0,
            "glove category bit was not preserved");
        Require(!(bool)Get(batchType, glove, "IsBareHandShell"),
            "black outfit glove was classified as bare skin");

        Array batches = (Array)Get(assetType, asset, "Batches");
        bool topPreserved = batches.Cast<object>().Any(value =>
            ((uint)Get(value.GetType(), value, "CategoryMask") & 0x8u) != 0);
        Require(topPreserved, "upper-body/sleeve component category is missing");

        object[] headGeometry = batches.Cast<object>().Where(value =>
        {
            string name = (string)Get(value.GetType(), value, "Name");
            return name.StartsWith("00000001-", StringComparison.OrdinalIgnoreCase) &&
                name.IndexOf(":head:", StringComparison.OrdinalIgnoreCase) >= 0;
        }).ToArray();
        Require(headGeometry.Length == 2,
            "the two mirrored Xbox head halves were not both preserved");
        object[] faceLayers = batches.Cast<object>().Where(value =>
            ((string)Get(value.GetType(), value, "Name"))
                .IndexOf(":face-layer-", StringComparison.OrdinalIgnoreCase) >= 0)
            .ToArray();
        Require(faceLayers.Length >= 70 && faceLayers.Length <= 72,
            "the complete static face, facial-hair, 5-frame brow, and 14-frame eye/mouth overlays are incomplete");
        Require(faceLayers.All(value =>
            ((byte[])Get(value.GetType(), value, "TexturePng")).Length > 0),
            "a face overlay has no baked RGBA texture");
        Require(faceLayers.All(value =>
            (int)Get(value.GetType(), value, "FaceTextureUsage") >= 0 &&
            (int)Get(value.GetType(), value, "FaceFrame") >= 0),
            "a face overlay name did not decode into expression metadata");
        // Eye shadow (usage 6) is optional and is absent when the editor's
        // selected avatar has no makeup/shadow layer.
        int[] requiredFaceUsages = { 5, 7, 8, 9, 10, 11, 12 };
        Require(requiredFaceUsages.All(usage => faceLayers.Any(value =>
            (int)Get(value.GetType(), value, "FaceTextureUsage") == usage)),
            "facial hair, skin features, or expression layers are missing");
        Require(new FileInfo(Path.GetFullPath(args[1])).Length <= 4 * 1024 * 1024,
            "avatar exceeds the network transfer limit");

        Type bridge = ModType(mod, "AvatarNetworkBridge");

        byte[] stockDescription = Enumerable.Range(0, 10)
            .Select(index => (byte)(0x20 + index))
            .ToArray();
        MethodInfo appendMarker = bridge.GetMethod(
            "AppendCapabilityMarker",
            Hidden);
        MethodInfo stripMarker = bridge.GetMethod(
            "TryStripCapabilityMarker",
            Hidden);
        byte[] decoratedDescription = (byte[])appendMarker.Invoke(
            null,
            new object[] { stockDescription });
        Require(decoratedDescription.Length == stockDescription.Length + 8,
            "capability advertisement marker has an unexpected size");
        object[] stripArguments = { decoratedDescription, null };
        Require((bool)stripMarker.Invoke(null, stripArguments),
            "valid capability advertisement was rejected");
        Require(stockDescription.SequenceEqual((byte[])stripArguments[1]),
            "capability marker stripping changed the stock description");
        object[] plainArguments = { stockDescription, null };
        Require(!(bool)stripMarker.Invoke(null, plainArguments),
            "unmodified vanilla description was treated as mod-capable");
        byte[] wrongVersion = (byte[])decoratedDescription.Clone();
        wrongVersion[wrongVersion.Length - 1]++;
        object[] wrongArguments = { wrongVersion, null };
        Require(!(bool)stripMarker.Invoke(null, wrongArguments),
            "incompatible capability protocol version was accepted");

        MethodInfo sendPacket = packetType.GetMethod("SendPacket", Hidden);
        MethodInfo isPeerCapable = bridge.GetMethod("IsPeerCapable", Hidden);
        Require(Calls(sendPacket, isPeerCapable),
            "custom packet sender has no final capability gate");

        Assembly game = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(value =>
                value.GetName().Name == "CastleMinerZ") ??
            Assembly.Load("CastleMinerZ");
        Type playerExists = game.GetType(
            "DNA.CastleMinerZ.Net.PlayerExistsMessage",
            true);
        MethodInfo stockPlayerExistsSend = playerExists.GetMethod(
            "Send",
            BindingFlags.Public | BindingFlags.Static);
        MethodInfo advertise = bridge.GetMethod(
            "SendCapabilityAdvertisement",
            Hidden);
        Require(Calls(advertise, stockPlayerExistsSend),
            "capability advertisement does not use the stock message path");

        bridge.GetMethod("OnGamerJoined", Hidden).Invoke(null, new object[] { null });
        Require(!(bool)bridge.GetMethod("OnMessage", Hidden).Invoke(
            null,
            new object[] { null }),
            "non-avatar messages must fall through to Castle Miner Z");

        Console.WriteLine(
            "PASS: v3 independent material passes/gloves, two head halves, 70-72 RGBA face layers, " +
            "stock-safe capability advertisement, strict peer send gate, " +
            "null-safe pre-join bridge, 3048-byte network round-trip, " +
            "oversize rejection, and truncation rejection.");
        return 0;
    }

    private static bool Calls(MethodInfo source, MethodInfo target)
    {
        MethodBody body = source.GetMethodBody();
        byte[] il = body == null ? null : body.GetILAsByteArray();
        if (il == null)
        {
            return false;
        }
        for (int index = 0; index + 4 < il.Length; index++)
        {
            if (il[index] != 0x28 && il[index] != 0x6f)
            {
                continue;
            }
            int token = BitConverter.ToInt32(il, index + 1);
            try
            {
                MethodBase called = source.Module.ResolveMethod(token);
                if (called == target)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
            }
        }
        return false;
    }

    private static void ExpectFailure(
        MethodInfo receive,
        Type packetType,
        byte[] bytes,
        Type expected)
    {
        try
        {
            object packet = Activator.CreateInstance(packetType, true);
            using (var stream = new MemoryStream(bytes))
            using (var reader = new BinaryReader(stream))
            {
                receive.Invoke(packet, new object[] { reader });
            }
            throw new Exception("Malformed packet was accepted.");
        }
        catch (TargetInvocationException exception)
        {
            Require(expected.IsInstanceOfType(exception.InnerException),
                "wrong malformed-packet exception: " + exception.InnerException);
        }
    }

    private static object Get(Type type, object instance, string name)
    {
        return type.GetField(name, Hidden).GetValue(instance);
    }

    private static void Set(Type type, object instance, string name, object value)
    {
        type.GetField(name, Hidden).SetValue(instance, value);
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
