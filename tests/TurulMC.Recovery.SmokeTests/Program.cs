using System.Text.Json;
using TurulMC.Core.Recovery;

var suiteRoot = Path.Combine(Path.GetTempPath(), "TurulRecoveryTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(suiteRoot);
int passed = 0;
var settings = new RecoveryLaunchSettings { MinecraftVersion = "26.1.2", Loader = "fabric" };
void Check(bool condition, string description) { if (!condition) throw new Exception(description); }
void Write(string root, string relative, string content)
{
    var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content);
}
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
void Test(string name, Action<string, ModRecoveryService> action)
{
    var root = Path.Combine(suiteRoot, "case-" + passed);
    Directory.CreateDirectory(root);
    action(Path.Combine(root, "instance"), new ModRecoveryService(Path.Combine(root, "snapshots")));
    Console.WriteLine("PASS " + name); passed++;
}
string SnapshotDirectory(string instance, string id) => Directory.EnumerateDirectories(
    Path.Combine(Path.GetDirectoryName(instance)!, "snapshots"), id, SearchOption.AllDirectories).Single();
try
{
    Test("round trip preserves worlds, excludes unrelated data and supports undo", (root, recovery) =>
    {
        Write(root, "mods/a.jar", "old-mod"); Write(root, "mods/b.jar.disabled", "disabled");
        Write(root, "mods/.turul-meta/a.jar.json", "metadata"); Write(root, "config/a.toml", "old-config");
        Write(root, "defaultconfigs/server.toml", "defaults"); Write(root, "saves/world/level.dat", "world-before");
        Write(root, "options.txt", "video-options");
        var snapshot = recovery.Create(root, "a", "before install", settings);
        Check(snapshot.Hashes.Count == 5, "Only mod/config files should be saved");
        Write(root, "mods/a.jar", "new-mod"); Write(root, "mods/new.jar", "new"); Write(root, "config/a.toml", "new-config");
        Write(root, "saves/world/level.dat", "world-after");
        recovery.Restore(root, "a", snapshot.Id, settings);
        Check(File.ReadAllText(Path.Combine(root,"mods/a.jar")) == "old-mod", "Old mod missing");
        Check(!File.Exists(Path.Combine(root,"mods/new.jar")), "Added mod must be removed");
        Check(File.ReadAllText(Path.Combine(root,"config/a.toml")) == "old-config", "Config not restored");
        Check(File.ReadAllText(Path.Combine(root,"saves/world/level.dat")) == "world-after", "World was touched");
        Check(File.ReadAllText(Path.Combine(root,"options.txt")) == "video-options", "Options touched");
        recovery.Restore(root, "a", recovery.List(root, "a").First().Id, settings);
        Check(File.ReadAllText(Path.Combine(root,"mods/a.jar")) == "new-mod", "Undo failed");
        Check(File.Exists(Path.Combine(root,"mods/new.jar")), "Undo did not bring back new file");
    });
    Test("an empty snapshot restores the absence of mods and config", (root, recovery) =>
    {
        var point = recovery.Create(root, "a", "empty", settings);
        Write(root,"mods/new.jar","new"); Write(root,"config/new.json","config");
        recovery.Restore(root, "a", point.Id, settings);
        Check(!Directory.Exists(Path.Combine(root,"mods")) && !Directory.Exists(Path.Combine(root,"config")), "Expected original absent directories");
    });
    Test("retention is bounded and oldest retained snapshot can still be restored", (root,recovery) =>
    {
        for (int i=0;i<7;i++) { Write(root,"mods/a.jar",i.ToString()); recovery.Create(root,"a",i.ToString(),settings); }
        var points = recovery.List(root,"a"); Check(points.Count == 5,"Retention failed");
        var oldest = points.Last(); recovery.Restore(root,"a",oldest.Id,settings);
        Check(File.ReadAllText(Path.Combine(root,"mods/a.jar")) == oldest.Reason,"Pruning deleted selected restore data");
        Check(recovery.List(root,"a").Count == 5,"Undo exceeded retention");
    });
    Test("corrupt data is rejected without changing live files", (root,recovery) =>
    {
        Write(root,"mods/a.jar","original"); var point=recovery.Create(root,"a","before",settings);
        Write(root,"mods/a.jar","current"); Write(SnapshotDirectory(root,point.Id),"payload/mods/a.jar","tampered");
        Throws<InvalidDataException>(()=>recovery.Restore(root,"a",point.Id,settings));
        Check(File.ReadAllText(Path.Combine(root,"mods/a.jar"))=="current","Live file changed");
    });
    Test("foreign instance and path traversal IDs are rejected", (root,recovery) =>
    {
        Write(root,"mods/a.jar","original"); var point=recovery.Create(root,"a","before",settings);
        Throws<InvalidDataException>(()=>recovery.Restore(root,"b",point.Id,settings));
        Throws<InvalidDataException>(()=>recovery.Restore(root,"a","../../other",settings));
        Check(recovery.List(root,"b").Count==0,"Cross-instance snapshot leaked");
    });
    Test("missing payload file is rejected", (root,recovery) =>
    {
        Write(root,"mods/a.jar","original"); var point=recovery.Create(root,"a","before",settings);
        File.Delete(Path.Combine(SnapshotDirectory(root,point.Id),"payload/mods/a.jar"));
        Throws<InvalidDataException>(()=>recovery.Restore(root,"a",point.Id,settings));
    });
    Test("partial restore journal is rolled back idempotently", (root,recovery) =>
    {
        var transaction=Path.Combine(root,".turul-recovery-transaction");
        Write(transaction,"old/mods/a.jar","original"); Write(root,"mods/a.jar","partial-restore");
        Write(root,"config/a.toml","untouched"); Write(root,"defaultconfigs/added.toml","partial");
        Write(transaction,"journal.json",JsonSerializer.Serialize(new[]{"mods","config"}));
        recovery.RecoverInterruptedRestore(root); recovery.RecoverInterruptedRestore(root);
        Check(File.ReadAllText(Path.Combine(root,"mods/a.jar"))=="original","Rollback lost original");
        Check(File.ReadAllText(Path.Combine(root,"config/a.toml"))=="untouched","Untouched config lost");
        Check(!Directory.Exists(Path.Combine(root,"defaultconfigs")),"New directory not undone");
    });
    Test("completed restore cleanup does not roll back committed files", (root,recovery) =>
    {
        var transaction=Path.Combine(root,".turul-recovery-transaction");
        Write(transaction,"old/mods/a.jar","old"); Write(root,"mods/a.jar","restored");
        Write(transaction,"journal.json","[\"mods\"]"); Write(transaction,"committed","1");
        recovery.RecoverInterruptedRestore(root);
        Check(File.ReadAllText(Path.Combine(root,"mods/a.jar"))=="restored","Committed restore reverted");
        Check(!Directory.Exists(transaction),"Transaction remains");
    });
    Test("abandoned staging is cleaned without changing the instance", (root,recovery) =>
    {
        Write(root,"mods/a.jar","live"); Write(root,".turul-recovery-transaction/new/mods/a.jar","staged");
        recovery.RecoverInterruptedRestore(root);
        Check(File.ReadAllText(Path.Combine(root,"mods/a.jar"))=="live","Uncommitted staging affected files");
        Check(!Directory.Exists(Path.Combine(root,".turul-recovery-transaction")),"Stale staging remains");
    });
    Test("unknown exit code never invents a cause", (_,_) =>
    {
        var result=CrashExplanationService.Analyze("Process exited with code 1",1);
        Check(result.Code=="unknown" && !result.Recognized,"Exit code was diagnosed as a specific cause");
    });
    foreach(var pair in new[]{
        ("java.lang.OutOfMemoryError: Java heap space","memory"),
        ("Could not reserve enough space for object heap","memory-reservation"),
        ("java.lang.UnsupportedClassVersionError: Main has been compiled by a more recent version of the Java Runtime","java-version"),
        ("Found duplicate mods: mod a at a.jar and b.jar","duplicate-mod"),
        ("Mod 'Test' requires any version of fabric-api, which is missing!","mod-dependency"),
        ("Incompatible mods found! Mod A requires version 1.21 of Minecraft","mod-version"),
        ("GLFW error 65542: WGL: The driver does not support OpenGL","graphics"),
        ("java.util.zip.ZipException: zip END header not found","corrupt-file"),
        ("org.spongepowered.asm.mixin.transformer.throwables.MixinTransformerError","mixin")})
        Test("diagnosis " + pair.Item2,(_,_) => {
            var result=CrashExplanationService.Analyze(pair.Item1,1);
            Check(result.Code==pair.Item2 && result.Recognized,"Wrong diagnosis: "+result.Code);
            Check(result.Evidence.Contains(pair.Item1),"Evidence absent");
        });
    Test("old session logs are excluded", (root,_) =>
    {
        var started=DateTime.UtcNow;
        Write(root,"logs/latest.log","stale OutOfMemoryError");
        File.SetLastWriteTimeUtc(Path.Combine(root,"logs/latest.log"),started.AddHours(-1));
        Write(root,"crash-reports/old.txt","stale duplicate mods");
        File.SetLastWriteTimeUtc(Path.Combine(root,"crash-reports/old.txt"),started.AddHours(-1));
        Check(CrashExplanationService.ReadSessionLogs(root,started)=="","Stale diagnosis data included");
        Write(root,"crash-reports/new.txt","fresh GLFW error 65542");
        Check(CrashExplanationService.ReadSessionLogs(root,started).Contains("fresh"),"Fresh crash absent");
    });
    if (!OperatingSystem.IsWindows()) Test("symlinks are rejected in snapshot sources", (root,recovery) =>
    {
        var elsewhere=Path.Combine(Path.GetDirectoryName(root)!,"elsewhere"); Directory.CreateDirectory(elsewhere);
        Write(elsewhere,"private.txt","outside"); Directory.CreateDirectory(root);
        Directory.CreateSymbolicLink(Path.Combine(root,"mods"),elsewhere);
        Throws<IOException>(()=>recovery.Create(root,"a","symlink",settings));
        Check(File.ReadAllText(Path.Combine(elsewhere,"private.txt"))=="outside","External data touched");
    });
    Console.WriteLine($"All {passed} recovery/diagnostic checks passed.");
}
finally { Directory.Delete(suiteRoot,true); }
