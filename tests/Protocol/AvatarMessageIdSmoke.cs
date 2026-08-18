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
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs resolveArgs)
        {
            string dependency = System.IO.Path.Combine(
                gameFolder,
                new AssemblyName(resolveArgs.Name).Name + ".dll");
            return System.IO.File.Exists(dependency)
                ? Assembly.Load(System.IO.File.ReadAllBytes(dependency))
                : null;
        };

        Assembly common = Assembly.Load(System.IO.File.ReadAllBytes(args[1]));
        Assembly game = Assembly.Load(System.IO.File.ReadAllBytes(args[0]));
        Assembly mod = Assembly.Load(System.IO.File.ReadAllBytes(args[2]));
        Type packet = mod.GetType(
            "OpenClassic.XboxAvatar.ZZOpenClassicAvatarSyncMessage",
            true);
        Type message = packet.BaseType.BaseType;
        Type reflection = message.Assembly.GetType(
            "DNA.Reflection.ReflectionTools",
            true);
        MethodInfo register = reflection.GetMethod("RegisterAssembly", Hidden);
        register.Invoke(null, new object[] { game, message.Assembly });
        register.Invoke(null, new object[] { game, game });
        mod.GetType("OpenClassic.XboxAvatar.AvatarNetworkBridge", true)
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
            type.FullName.StartsWith("OpenClassic.", StringComparison.Ordinal)))
        {
            throw new Exception("A mod packet shifted a stock message ID.");
        }

        Console.WriteLine(
            "PASS: " + (types.Length - 1) +
            " stock packet IDs preserved; avatar sync appended at ID " + id + ".");
        return 0;
    }
}
