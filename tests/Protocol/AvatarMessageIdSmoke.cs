using System;
using System.Linq;
using System.Reflection;

internal static class AvatarMessageIdSmoke
{
    private const BindingFlags Hidden =
        BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;

    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine(
                "Usage: AvatarMessageIdSmoke <game.exe> <common.dll> <mod.dll>");
            return 2;
        }

        string gameFolder = System.IO.Path.GetDirectoryName(
            System.IO.Path.GetFullPath(args[0]));

        // Assemblies loaded from a byte array are not findable by name, so the
        // resolver has to hand back the ones already loaded here. It must also
        // probe .exe: the game assembly this mod references is CastleMinerZ.exe.
        Assembly common = null;
        Assembly game = null;
        Assembly mod = null;
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs resolveArgs)
        {
            string wanted = new AssemblyName(resolveArgs.Name).Name;
            foreach (Assembly loaded in new[] { common, game, mod })
            {
                if (loaded != null && loaded.GetName().Name == wanted)
                {
                    return loaded;
                }
            }
            foreach (string extension in new[] { ".dll", ".exe" })
            {
                string dependency = System.IO.Path.Combine(gameFolder, wanted + extension);
                if (System.IO.File.Exists(dependency))
                {
                    return Assembly.Load(System.IO.File.ReadAllBytes(dependency));
                }
            }
            return null;
        };

        common = Assembly.Load(System.IO.File.ReadAllBytes(args[1]));
        game = Assembly.Load(System.IO.File.ReadAllBytes(args[0]));
        mod = Assembly.Load(System.IO.File.ReadAllBytes(args[2]));
        Type packet = ModType(mod, "ZZAvatarSyncMessage");
        Type message = packet.BaseType.BaseType;
        Type reflection = message.Assembly.GetType(
            "DNA.Reflection.ReflectionTools",
            true);
        MethodInfo register = reflection.GetMethod("RegisterAssembly", Hidden);
        register.Invoke(null, new object[] { game, message.Assembly });
        register.Invoke(null, new object[] { game, game });
        ModType(mod, "AvatarNetworkBridge")
            .GetMethod("Register", Hidden)
            .Invoke(null, null);

        object instance = Activator.CreateInstance(packet, true);
        Type[] types = (Type[])message.GetField("_messageTypes", Hidden)
            .GetValue(null);
        if (types == null || types.Length == 0)
        {
            throw new Exception("Message registry was not populated.");
        }
        if (types[types.Length - 1] != packet)
        {
            throw new Exception(
                "Avatar message is not appended after stock packet IDs: " +
                types[types.Length - 1].FullName);
        }
        byte id = (byte)message.GetProperty("MessageID", Hidden)
            .GetValue(instance, null);
        if (id != types.Length - 1)
        {
            throw new Exception("Avatar packet ID does not equal final registry slot.");
        }
        if (types.Take(types.Length - 1).Any(type =>
            type.Assembly == mod))
        {
            throw new Exception("A mod packet shifted a stock message ID.");
        }

        Console.WriteLine(
            "PASS: " + (types.Length - 1) +
            " stock packet IDs preserved; avatar sync appended at ID " + id + ".");
        return 0;
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
