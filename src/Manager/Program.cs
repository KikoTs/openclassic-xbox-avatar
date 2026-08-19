using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

bool launchedByDoubleClick = args.Length == 0;
bool localInstallMode = args.Length == 0 ||
    (args.Length == 1 && args[0].Equals("--install", StringComparison.OrdinalIgnoreCase));
bool localRemoveMode = args.Length == 1 &&
    (args[0].Equals("--remove", StringComparison.OrdinalIgnoreCase) ||
     args[0].Equals("--disable", StringComparison.OrdinalIgnoreCase));

try
{
    if (localRemoveMode)
    {
        RestoreLocalExecutable(AppContext.BaseDirectory);
        Console.WriteLine();
        Console.WriteLine("Xbox Avatar add-on disabled. The independent Refresh/stability layer was restored.");
        return 0;
    }

    if (!localInstallMode && args.Length != 3)
    {
        Console.Error.WriteLine("Community install: copy this tool and OpenClassicAvatarMod.dll beside CastleMinerZ.exe, then double-click it.");
        Console.Error.WriteLine("Disable: OpenClassic Xbox Avatar Manager.exe --remove");
        Console.Error.WriteLine("Developer usage: AvatarModPatcher <input CastleMinerZ.exe> <OpenClassicAvatarMod.dll> <output CastleMinerZ.exe>");
        return 2;
    }

    string inputPath = localInstallMode
        ? Path.Combine(AppContext.BaseDirectory, "CastleMinerZ.exe")
        : Path.GetFullPath(args[0]);
    string modPath = localInstallMode
        ? Path.Combine(AppContext.BaseDirectory, "OpenClassicAvatarMod.dll")
        : Path.GetFullPath(args[1]);
    string outputPath = localInstallMode
        ? Path.Combine(AppContext.BaseDirectory, "CastleMinerZ.avatar-patching.tmp.exe")
        : Path.GetFullPath(args[2]);

    RequireFile(inputPath, "CastleMinerZ.exe");
    RequireFile(modPath, "OpenClassicAvatarMod.dll");

    using ModuleDefMD game = ModuleDefMD.Load(inputPath);
    using ModuleDefMD mod = ModuleDefMD.Load(modPath);

TypeDef player = game.GetTypes().Single(type => type.FullName == "DNA.CastleMinerZ.Player");
MethodDef constructor = player.Methods.Single(method =>
    method.IsInstanceConstructor &&
    method.Parameters.Count(parameter => !parameter.IsHiddenThisParameter) == 2 &&
    method.Parameters.Where(parameter => !parameter.IsHiddenThisParameter).First().Type.FullName == "DNA.Net.GamerServices.NetworkGamer");
FieldDef avatarField = player.Fields.Single(field => field.Name == "Avatar");
FieldDef gamerField = player.Fields.Single(field => field.Name == "Gamer");

TypeDef factory = mod.GetTypes().Single(type => type.FullName == "OpenClassic.XboxAvatar.AvatarEntityFactory");
MethodDef create = factory.Methods.Single(method => method.Name == "Create");
TypeDef networkBridge = mod.GetTypes().Single(type => type.FullName == "OpenClassic.XboxAvatar.AvatarNetworkBridge");
MethodDef registerNetwork = networkBridge.Methods.Single(method => method.Name == "Register");
MethodDef processNetworkMessage = networkBridge.Methods.Single(method => method.Name == "OnMessage");
MethodDef processGamerJoined = networkBridge.Methods.Single(method => method.Name == "OnGamerJoined");
MethodDef updateNetwork = networkBridge.Methods.Single(method => method.Name == "Update");
var importer = new Importer(game, ImporterOptions.TryToUseDefs);
IMethod createReference = importer.Import(create);
IMethod registerNetworkReference = importer.Import(registerNetwork);
IMethod processNetworkMessageReference = importer.Import(processNetworkMessage);
IMethod processGamerJoinedReference = importer.Import(processGamerJoined);
IMethod updateNetworkReference = importer.Import(updateNetwork);

IList<Instruction> instructions = constructor.Body.Instructions;
bool alreadyHooked = instructions.Any(instruction =>
    instruction.OpCode.Code == Code.Call &&
    instruction.Operand is IMethod method &&
    method.DeclaringType.FullName == "OpenClassic.XboxAvatar.AvatarEntityFactory" &&
    method.Name == "Create");
if (localInstallMode && alreadyHooked)
{
    Console.WriteLine("Xbox Avatar add-on is already enabled. No files were changed.");
    PauseWhenDoubleClicked(launchedByDoubleClick);
    return 0;
}
// 1.9.9 moved the stock proxy model entity into the game assembly and renamed
// it. It is constructed at the same point in Player::.ctor and has the same
// shape, so accept whichever name this client uses.
string[] stockModelEntityTypes =
{
    "DNA.Avatars.AvatarModelEntity",      // pre-1.9.9
    "DNA.CastleMinerZ.PlayerModelEntity", // 1.9.9 and later
};
Instruction[] constructorCalls = Array.Empty<Instruction>();
foreach (string stockModelEntityType in stockModelEntityTypes)
{
    constructorCalls = instructions.Where(instruction =>
        instruction.OpCode.Code == Code.Newobj &&
        instruction.Operand is IMethod method &&
        method.DeclaringType.FullName == stockModelEntityType &&
        method.Name == ".ctor").ToArray();
    if (constructorCalls.Length > 0)
    {
        break;
    }
}
if (!alreadyHooked && constructorCalls.Length != 1)
{
    throw new InvalidOperationException(
        "Expected exactly one stock model entity construction in Player::.ctor (" +
        string.Join(" or ", stockModelEntityTypes) + "); found " + constructorCalls.Length + ".");
}

if (!alreadyHooked)
{
    int callIndex = instructions.IndexOf(constructorCalls[0]);
    instructions.Insert(callIndex++, Instruction.Create(OpCodes.Ldarg_0));
    instructions.Insert(callIndex++, Instruction.Create(OpCodes.Ldfld, avatarField));
    instructions.Insert(callIndex++, Instruction.Create(OpCodes.Ldarg_0));
    instructions.Insert(callIndex++, Instruction.Create(OpCodes.Ldfld, gamerField));
    instructions[callIndex].OpCode = OpCodes.Call;
    instructions[callIndex].Operand = createReference;
    constructor.Body.MaxStack += 2;
}

TypeDef program = game.GetTypes().Single(type => type.FullName == "DNA.CastleMinerZ.Program");
MethodDef main = program.Methods.Single(method => method.Name == "Main");
InsertAfterCall(
    main,
    "DNA.Reflection.CommonAssembly",
    "Initalize",
    new[] { Instruction.Create(OpCodes.Call, registerNetworkReference) },
    registerNetworkReference);

TypeDef gameType = game.GetTypes().Single(type => type.FullName == "DNA.CastleMinerZ.CastleMinerZGame");
MethodDef onMessage = gameType.Methods.Single(method => method.Name == "OnMessage");
InsertConsumingMessageHook(onMessage, processNetworkMessageReference);

MethodDef onGamerJoined = gameType.Methods.Single(method => method.Name == "OnGamerJoined");
InsertAtStart(
    onGamerJoined,
    new[]
    {
        Instruction.Create(OpCodes.Ldarg_1),
        Instruction.Create(OpCodes.Call, processGamerJoinedReference)
    },
    processGamerJoinedReference);

MethodDef update = gameType.Methods.Single(method =>
    method.Name == "Update" &&
    method.Parameters.Count(parameter => !parameter.IsHiddenThisParameter) == 1);
MoveCallBeforeReturns(update, updateNetworkReference);

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
var options = new ModuleWriterOptions(game);
options.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
game.Write(outputPath, options);

using ModuleDefMD verification = ModuleDefMD.Load(outputPath);
MethodDef verifiedConstructor = verification.GetTypes()
    .Single(type => type.FullName == "DNA.CastleMinerZ.Player")
    .Methods.Single(method => method.IsInstanceConstructor &&
        method.Parameters.Count(parameter => !parameter.IsHiddenThisParameter) == 2 &&
        method.Parameters.Where(parameter => !parameter.IsHiddenThisParameter).First().Type.FullName == "DNA.Net.GamerServices.NetworkGamer");
Instruction factoryCall = verifiedConstructor.Body.Instructions.Single(instruction =>
    instruction.OpCode.Code == Code.Call &&
    instruction.Operand is IMethod method &&
    method.FullName.Contains("OpenClassic.XboxAvatar.AvatarEntityFactory::Create", StringComparison.Ordinal));
Console.WriteLine("Patched local-player avatar factory: " + factoryCall.Operand);
VerifyCall(verification, "DNA.CastleMinerZ.Program", "Main", "OpenClassic.XboxAvatar.AvatarNetworkBridge", "Register");
VerifyCall(verification, "DNA.CastleMinerZ.CastleMinerZGame", "OnMessage", "OpenClassic.XboxAvatar.AvatarNetworkBridge", "OnMessage");
VerifyConsumingMessageHook(verification);
VerifyCall(verification, "DNA.CastleMinerZ.CastleMinerZGame", "OnGamerJoined", "OpenClassic.XboxAvatar.AvatarNetworkBridge", "OnGamerJoined");
VerifyCall(verification, "DNA.CastleMinerZ.CastleMinerZGame", "Update", "OpenClassic.XboxAvatar.AvatarNetworkBridge", "Update");
VerifyUpdateEpilogue(verification);
Console.WriteLine("Patched avatar network registration, join, message, and throttled update hooks.");

    if (localInstallMode)
    {
        // dnlib memory-maps input assemblies on Windows. Release every mapping before
        // replacing the user's executable in place.
        verification.Dispose();
        mod.Dispose();
        game.Dispose();
        InstallLocalExecutable(inputPath, outputPath);
        Console.WriteLine();
        Console.WriteLine("Xbox Avatar add-on enabled successfully.");
        Console.WriteLine("Your Refresh/stability installation remains the independent base layer.");
        Console.WriteLine("Run this manager with --remove to restore that exact base executable.");
    }

    PauseWhenDoubleClicked(launchedByDoubleClick);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Xbox Avatar add-on was not changed:");
    Console.Error.WriteLine(error.Message);
    PauseWhenDoubleClicked(launchedByDoubleClick);
    return 1;
}

static void InstallLocalExecutable(string inputPath, string patchedPath)
{
    string gameDirectory = Path.GetDirectoryName(inputPath)!;
    string addOnDirectory = Path.Combine(gameDirectory, "OpenClassic Addons", "Xbox Avatar");
    string backupDirectory = Path.Combine(addOnDirectory, "Backups");
    string manifestPath = Path.Combine(addOnDirectory, "active-exe-backup.txt");
    Directory.CreateDirectory(backupDirectory);

    string inputHash = Sha256(inputPath);
    string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    string backupName = $"CastleMinerZ.pre-avatar.{timestamp}.{inputHash[..12]}.exe";
    string backupPath = Path.Combine(backupDirectory, backupName);
    File.Copy(inputPath, backupPath, overwrite: false);

    try
    {
        File.Copy(patchedPath, inputPath, overwrite: true);
        File.WriteAllText(manifestPath, backupName + Environment.NewLine);
    }
    catch
    {
        File.Copy(backupPath, inputPath, overwrite: true);
        throw;
    }
    finally
    {
        if (File.Exists(patchedPath))
        {
            File.Delete(patchedPath);
        }
    }

    Console.WriteLine("Base executable backup: " + backupPath);
}

static void RestoreLocalExecutable(string gameDirectory)
{
    string inputPath = Path.Combine(gameDirectory, "CastleMinerZ.exe");
    string addOnDirectory = Path.Combine(gameDirectory, "OpenClassic Addons", "Xbox Avatar");
    string backupDirectory = Path.Combine(addOnDirectory, "Backups");
    string manifestPath = Path.Combine(addOnDirectory, "active-exe-backup.txt");
    RequireFile(inputPath, "CastleMinerZ.exe");
    RequireFile(manifestPath, "the Xbox Avatar backup manifest");

    string backupName = File.ReadAllText(manifestPath).Trim();
    if (Path.GetFileName(backupName) != backupName || string.IsNullOrWhiteSpace(backupName))
    {
        throw new InvalidDataException("The Xbox Avatar backup manifest is invalid.");
    }

    string backupPath = Path.GetFullPath(Path.Combine(backupDirectory, backupName));
    string expectedBackupRoot = Path.GetFullPath(backupDirectory) + Path.DirectorySeparatorChar;
    if (!backupPath.StartsWith(expectedBackupRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException("The Xbox Avatar backup path escapes its backup directory.");
    }
    RequireFile(backupPath, "the pre-avatar executable backup");

    Directory.CreateDirectory(backupDirectory);
    string currentHash = Sha256(inputPath);
    string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    string retainedPatchedPath = Path.Combine(
        backupDirectory,
        $"CastleMinerZ.avatar-enabled.{timestamp}.{currentHash[..12]}.exe");
    File.Copy(inputPath, retainedPatchedPath, overwrite: false);
    File.Copy(backupPath, inputPath, overwrite: true);
    File.Delete(manifestPath);

    Console.WriteLine("Restored base executable: " + backupPath);
    Console.WriteLine("Retained avatar-enabled recovery copy: " + retainedPatchedPath);
}

static void RequireFile(string path, string description)
{
    if (!File.Exists(path))
    {
        throw new FileNotFoundException($"Could not find {description} beside the manager.", path);
    }
}

static string Sha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
}

static void PauseWhenDoubleClicked(bool shouldPause)
{
    if (!shouldPause || Console.IsInputRedirected)
    {
        return;
    }
    Console.WriteLine();
    Console.Write("Press any key to close...");
    Console.ReadKey(intercept: true);
    Console.WriteLine();
}

static void InsertAtStart(
    MethodDef method,
    IEnumerable<Instruction> added,
    IMethod expectedCall)
{
    if (HasCall(method, expectedCall))
    {
        return;
    }
    int index = 0;
    foreach (Instruction instruction in added)
    {
        method.Body.Instructions.Insert(index++, instruction);
    }
    method.Body.MaxStack += 1;
}

static void InsertConsumingMessageHook(MethodDef method, IMethod hook)
{
    IList<Instruction> body = method.Body.Instructions;
    for (int index = body.Count - 1; index >= 0; index--)
    {
        Instruction instruction = body[index];
        if (instruction.OpCode.Code is Code.Call or Code.Callvirt &&
            instruction.Operand is IMethod called &&
            called.DeclaringType.FullName == hook.DeclaringType.FullName &&
            called.Name == hook.Name)
        {
            body.RemoveAt(index);
            if (index > 0 && body[index - 1].OpCode.Code == Code.Ldarg_1)
            {
                body.RemoveAt(index - 1);
            }
        }
    }

    Instruction originalStart = body[0];
    body.Insert(0, Instruction.Create(OpCodes.Ldarg_1));
    body.Insert(1, Instruction.Create(OpCodes.Call, hook));
    body.Insert(2, Instruction.Create(OpCodes.Brfalse, originalStart));
    body.Insert(3, Instruction.Create(OpCodes.Ret));
    method.Body.MaxStack += 1;
}

static void MoveCallBeforeReturns(MethodDef method, IMethod expectedCall)
{
    IList<Instruction> body = method.Body.Instructions;
    for (int index = body.Count - 1; index >= 0; index--)
    {
        Instruction instruction = body[index];
        if (instruction.OpCode.Code is Code.Call or Code.Callvirt &&
            instruction.Operand is IMethod called &&
            called.DeclaringType.FullName == expectedCall.DeclaringType.FullName &&
            called.Name == expectedCall.Name)
        {
            body.RemoveAt(index);
        }
    }

    Instruction[] returns = body
        .Where(instruction => instruction.OpCode.Code == Code.Ret)
        .ToArray();
    if (returns.Length == 0)
    {
        throw new InvalidOperationException(
            "Update method has no return instruction for the avatar epilogue.");
    }
    foreach (Instruction returnInstruction in returns)
    {
        Instruction call = Instruction.Create(OpCodes.Call, expectedCall);
        RetargetBranches(body, returnInstruction, call);
        body.Insert(body.IndexOf(returnInstruction), call);
    }
}

static void RetargetBranches(
    IList<Instruction> body,
    Instruction oldTarget,
    Instruction newTarget)
{
    foreach (Instruction instruction in body)
    {
        if (instruction.Operand == oldTarget)
        {
            instruction.Operand = newTarget;
            continue;
        }
        if (instruction.Operand is Instruction[] targets)
        {
            for (int index = 0; index < targets.Length; index++)
            {
                if (targets[index] == oldTarget)
                {
                    targets[index] = newTarget;
                }
            }
        }
    }
}

static void InsertAfterCall(
    MethodDef method,
    string declaringType,
    string calledName,
    IEnumerable<Instruction> added,
    IMethod expectedCall)
{
    if (HasCall(method, expectedCall))
    {
        return;
    }
    IList<Instruction> body = method.Body.Instructions;
    Instruction anchor = body.Single(instruction =>
        instruction.OpCode.Code is Code.Call or Code.Callvirt &&
        instruction.Operand is IMethod called &&
        called.DeclaringType.FullName == declaringType &&
        called.Name == calledName);
    int index = body.IndexOf(anchor) + 1;
    foreach (Instruction instruction in added)
    {
        body.Insert(index++, instruction);
    }
}

static bool HasCall(MethodDef method, IMethod expected)
{
    return method.Body.Instructions.Any(instruction =>
        instruction.OpCode.Code is Code.Call or Code.Callvirt &&
        instruction.Operand is IMethod called &&
        called.DeclaringType.FullName == expected.DeclaringType.FullName &&
        called.Name == expected.Name);
}

static void VerifyCall(
    ModuleDefMD module,
    string typeName,
    string methodName,
    string calledType,
    string calledName)
{
    TypeDef type = module.GetTypes().Single(value => value.FullName == typeName);
    bool found = type.Methods.Where(method => method.Name == methodName).Any(method =>
        method.HasBody && method.Body.Instructions.Any(instruction =>
            instruction.OpCode.Code is Code.Call or Code.Callvirt &&
            instruction.Operand is IMethod called &&
            called.DeclaringType.FullName == calledType &&
            called.Name == calledName));
    if (!found)
    {
        throw new InvalidOperationException(
            "Missing verified hook " + typeName + "::" + methodName +
            " -> " + calledType + "::" + calledName + ".");
    }
}

static void VerifyUpdateEpilogue(ModuleDefMD module)
{
    MethodDef update = module.GetTypes()
        .Single(type => type.FullName == "DNA.CastleMinerZ.CastleMinerZGame")
        .Methods.Single(method =>
            method.Name == "Update" &&
            method.Parameters.Count(parameter => !parameter.IsHiddenThisParameter) == 1);
    IList<Instruction> body = update.Body.Instructions;
    Instruction[] returns = body
        .Where(instruction => instruction.OpCode.Code == Code.Ret)
        .ToArray();
    foreach (Instruction returnInstruction in returns)
    {
        int index = body.IndexOf(returnInstruction);
        if (index <= 0 ||
            body[index - 1].OpCode.Code != Code.Call ||
            body[index - 1].Operand is not IMethod called ||
            called.DeclaringType.FullName != "OpenClassic.XboxAvatar.AvatarNetworkBridge" ||
            called.Name != "Update")
        {
            throw new InvalidOperationException(
                "Avatar update/anchor hook is not in the game-update epilogue.");
        }
    }
}

static void VerifyConsumingMessageHook(ModuleDefMD module)
{
    MethodDef onMessage = module.GetTypes()
        .Single(type => type.FullName == "DNA.CastleMinerZ.CastleMinerZGame")
        .Methods.Single(method => method.Name == "OnMessage");
    IList<Instruction> body = onMessage.Body.Instructions;
    if (body.Count < 4 ||
        body[0].OpCode.Code != Code.Ldarg_1 ||
        body[1].OpCode.Code != Code.Call ||
        body[1].Operand is not IMethod called ||
        called.DeclaringType.FullName != "OpenClassic.XboxAvatar.AvatarNetworkBridge" ||
        called.Name != "OnMessage" ||
        body[2].OpCode.Code is not (Code.Brfalse or Code.Brfalse_S) ||
        body[3].OpCode.Code != Code.Ret)
    {
        throw new InvalidOperationException(
            "Avatar messages are not consumed before the stock OnMessage handler.");
    }
}
