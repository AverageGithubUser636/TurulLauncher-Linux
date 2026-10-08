using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TurulMC.Core.Recovery;

public sealed class RecoveryLaunchSettings
{
    public string MinecraftVersion { get; set; } = "";
    public string Loader { get; set; } = "";
    public string LoaderVersion { get; set; } = "";
    public string ModpackProjectId { get; set; } = "";
    public string ModpackVersionId { get; set; } = "";
    public string ModpackName { get; set; } = "";
}

public sealed class RecoverySnapshot
{
    public int Format { get; set; } = 1;
    public string Id { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string Reason { get; set; } = "";
    public long Bytes { get; set; }
    public RecoveryLaunchSettings Settings { get; set; } = new();
    public string[] PresentItems { get; set; } = [];
    public Dictionary<string, string> Hashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Local, bounded mod/config recovery. Never includes saves, accounts or logs.</summary>
public sealed class ModRecoveryService
{
    public const int RetentionCount = 5;
    public const long MaxSnapshotBytes = 4L * 1024 * 1024 * 1024;
    private const int MaxFiles = 50000;
    private const string TransactionName = ".turul-recovery-transaction";
    private static readonly string[] Items =
        ["mods", "config", "defaultconfigs"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _storage;

    public ModRecoveryService(string storage) => _storage = Path.GetFullPath(storage);

    private string Bucket(string instanceRoot)
    {
        var normalized = Path.GetFullPath(instanceRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        return Path.Combine(_storage, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))));
    }

    public IReadOnlyList<RecoverySnapshot> List(string instanceRoot, string instanceId)
    {
        var bucket = Bucket(instanceRoot);
        if (!Directory.Exists(bucket)) return [];
        EnsureNoLinks(bucket);
        var result = new List<RecoverySnapshot>();
        foreach (var directory in Directory.EnumerateDirectories(bucket))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            try
            {
                var snapshot = ReadSnapshot(directory, instanceId);
                result.Add(snapshot);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { }
        }
        return result.OrderByDescending(x => x.CreatedUtc).ToArray();
    }

    public RecoverySnapshot Create(string instanceRoot, string instanceId, string reason, RecoveryLaunchSettings settings)
    {
        RecoverInterruptedRestore(instanceRoot);
        return CreateCore(instanceRoot, instanceId, reason, settings);
    }

    private RecoverySnapshot CreateCore(string instanceRoot, string instanceId, string reason, RecoveryLaunchSettings settings)
    {
        Directory.CreateDirectory(instanceRoot);
        EnsureNoLinks(instanceRoot);
        var bucket = Bucket(instanceRoot);
        Directory.CreateDirectory(bucket);
        EnsureNoLinks(bucket);
        var id = Guid.NewGuid().ToString("N");
        var temp = Path.Combine(bucket, id + ".pending");
        var destination = Path.Combine(bucket, id);
        Directory.CreateDirectory(Path.Combine(temp, "payload"));
        var snapshot = new RecoverySnapshot
        {
            Id = id, InstanceId = instanceId, CreatedUtc = DateTime.UtcNow,
            Reason = reason, Settings = settings,
            PresentItems = Items.Where(x => Exists(Path.Combine(instanceRoot, x))).ToArray()
        };
        try
        {
            // Check the total before allocating the backup, not after copying gigabytes.
            long sourceBytes = 0;
            int sourceFiles = 0;
            foreach (var item in snapshot.PresentItems)
            {
                var source = Path.Combine(instanceRoot, item);
                foreach (var file in Directory.Exists(source) ? WalkFiles(source) : new[] { source })
                {
                    sourceBytes += new FileInfo(file).Length;
                    if (++sourceFiles > MaxFiles || sourceBytes > MaxSnapshotBytes)
                        throw new IOException("A modmentés túl nagy (maximum 4 GiB / 50 000 fájl). A módosítás nem indult el.");
                }
            }
            foreach (var item in snapshot.PresentItems)
                CopyItem(Path.Combine(instanceRoot, item), Path.Combine(temp, "payload", item));
            foreach (var file in WalkFiles(Path.Combine(temp, "payload")))
            {
                snapshot.Bytes += new FileInfo(file).Length;
                if (snapshot.Bytes > MaxSnapshotBytes || snapshot.Hashes.Count >= MaxFiles)
                    throw new IOException("A modmentés túl nagy (maximum 4 GiB / 50 000 fájl). A módosítás nem indult el.");
                var relative = Path.GetRelativePath(Path.Combine(temp, "payload"), file).Replace('\\', '/');
                snapshot.Hashes.Add(relative, Hash(file));
            }
            WriteDurable(Path.Combine(temp, "snapshot.json"), JsonSerializer.Serialize(snapshot, JsonOptions));
            Directory.Move(temp, destination); // Only complete snapshots become visible.
            // Retention happens after a new snapshot is complete; failed saves keep every previous point.
            foreach (var old in List(instanceRoot, instanceId).Skip(RetentionCount))
                TryDelete(Path.Combine(bucket, old.Id));
            return snapshot;
        }
        catch { TryDelete(temp); throw; }
    }

    public RecoverySnapshot Restore(string instanceRoot, string instanceId, string snapshotId,
        RecoveryLaunchSettings currentSettings)
    {
        RecoverInterruptedRestore(instanceRoot);
        if (!Guid.TryParseExact(snapshotId, "N", out _)) throw new InvalidDataException("Érvénytelen mentésazonosító.");
        var snapshotRoot = Path.Combine(Bucket(instanceRoot), snapshotId);
        var snapshot = ReadSnapshot(snapshotRoot, instanceId);
        ValidatePayload(snapshotRoot, snapshot); // Fail before touching the instance.
        Directory.CreateDirectory(instanceRoot);
        EnsureNoLinks(instanceRoot);
        foreach (var item in Items) EnsureNoLinks(Path.Combine(instanceRoot, item));
        var transaction = Path.Combine(instanceRoot, TransactionName);
        Directory.CreateDirectory(Path.Combine(transaction, "new"));
        Directory.CreateDirectory(Path.Combine(transaction, "old"));
        try
        {
            foreach (var item in snapshot.PresentItems)
                CopyItem(Path.Combine(snapshotRoot, "payload", item), Path.Combine(transaction, "new", item));
            // A restore is itself reversible. Stage first so pruning cannot remove the chosen payload.
            CreateCore(instanceRoot, instanceId, "Visszaállítás előtti állapot", currentSettings);
            var originals = Items.Where(x => Exists(Path.Combine(instanceRoot, x))).ToArray();
            WriteDurable(Path.Combine(transaction, "journal.json"), JsonSerializer.Serialize(originals));
            foreach (var item in Items)
            {
                var current = Path.Combine(instanceRoot, item);
                if (Exists(current)) MoveItem(current, Path.Combine(transaction, "old", item));
                var staged = Path.Combine(transaction, "new", item);
                if (Exists(staged)) MoveItem(staged, current);
            }
            WriteDurable(Path.Combine(transaction, "committed"), "1");
        }
        catch
        {
            RecoverInterruptedRestore(instanceRoot);
            throw;
        }
        TryDelete(transaction);
        return snapshot;
    }

    /// <summary>Finish or undo an interrupted directory swap before the next launch/mutation.</summary>
    public void RecoverInterruptedRestore(string instanceRoot)
    {
        var transaction = Path.Combine(instanceRoot, TransactionName);
        if (!Directory.Exists(transaction)) return;
        EnsureNoLinks(transaction);
        if (File.Exists(Path.Combine(transaction, "committed"))) { TryDelete(transaction); return; }
        var journalPath = Path.Combine(transaction, "journal.json");
        // No journal means the live instance has not been touched.
        if (!File.Exists(journalPath)) { TryDelete(transaction); return; }
        var originals = JsonSerializer.Deserialize<string[]>(File.ReadAllText(journalPath))
            ?? throw new InvalidDataException("A visszaállítási napló sérült; a mentett fájlok megmaradtak.");
        if (originals.Any(x => !Items.Contains(x))) throw new InvalidDataException("Érvénytelen visszaállítási napló.");
        foreach (var item in Items.Reverse())
        {
            var old = Path.Combine(transaction, "old", item);
            var current = Path.Combine(instanceRoot, item);
            EnsureNoLinks(current);
            if (Exists(old))
            {
                DeleteItem(current);
                MoveItem(old, current);
            }
            else if (!originals.Contains(item)) DeleteItem(current);
        }
        // Retain a completion marker until cleanup is finished; recovery can safely be called again.
        WriteDurable(Path.Combine(transaction, "committed"), "rollback");
        TryDelete(transaction);
    }

    private static RecoverySnapshot ReadSnapshot(string directory, string instanceId)
    {
        EnsureNoLinks(directory);
        var path = Path.Combine(directory, "snapshot.json");
        EnsureNoLinks(path);
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Túl nagy mentésleíró.");
        var snapshot = JsonSerializer.Deserialize<RecoverySnapshot>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Sérült mentésleíró.");
        if (snapshot.Format != 1 || snapshot.Id != Path.GetFileName(directory) || snapshot.InstanceId != instanceId ||
            snapshot.PresentItems == null || snapshot.Hashes == null || snapshot.Settings == null ||
            snapshot.PresentItems.Distinct().Count() != snapshot.PresentItems.Length ||
            snapshot.PresentItems.Any(x => !Items.Contains(x)))
            throw new InvalidDataException("A mentés nem ehhez az Instance-hez tartozik, vagy sérült.");
        return snapshot;
    }

    private static void ValidatePayload(string directory, RecoverySnapshot snapshot)
    {
        var payload = Path.Combine(directory, "payload");
        EnsureNoLinks(payload);
        if (!Directory.Exists(payload)) throw new InvalidDataException("Hiányzó mentett tartalom.");
        var present = Directory.EnumerateFileSystemEntries(payload).Select(Path.GetFileName).OrderBy(x => x).ToArray();
        if (!present.SequenceEqual(snapshot.PresentItems.OrderBy(x => x))) throw new InvalidDataException("Hiányos mentés.");
        var count = 0;
        long bytes = 0;
        foreach (var file in WalkFiles(payload))
        {
            var relative = Path.GetRelativePath(payload, file).Replace('\\', '/');
            bytes += new FileInfo(file).Length;
            if (++count > MaxFiles || bytes > MaxSnapshotBytes ||
                !snapshot.Hashes.TryGetValue(relative, out var expected) || Hash(file) != expected)
                throw new InvalidDataException("A mentés ellenőrzőösszege hibás. Nem történt visszaállítás.");
        }
        if (count != snapshot.Hashes.Count || bytes != snapshot.Bytes) throw new InvalidDataException("Hiányos mentés.");
    }

    private static IEnumerable<string> WalkFiles(string root)
    {
        EnsureNoLinks(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            EnsureNoLinks(entry);
            if (Directory.Exists(entry)) { foreach (var child in WalkFiles(entry)) yield return child; }
            else yield return entry;
        }
    }

    private static void CopyItem(string source, string destination)
    {
        EnsureNoLinks(source);
        EnsureNoLinks(destination);
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(destination);
            foreach (var child in Directory.EnumerateFileSystemEntries(source))
                CopyItem(child, Path.Combine(destination, Path.GetFileName(child)));
        }
        else
        {
            if (new FileInfo(source).Length > MaxSnapshotBytes) throw new IOException("Túl nagy fájl a modmentésben.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, false);
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void MoveItem(string source, string destination)
    {
        EnsureNoLinks(source);
        EnsureNoLinks(destination);
        if (Directory.Exists(source)) Directory.Move(source, destination); else File.Move(source, destination);
    }
    private static void DeleteItem(string path)
    {
        EnsureNoLinks(path);
        if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path);
    }
    private static void TryDelete(string path) { try { DeleteItem(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static void WriteDurable(string path, string value)
    {
        var temporary = path + ".writing-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temporary, path);
    }
    private static void EnsureNoLinks(string path)
    {
        // Reject junctions/symlinks in every ancestor, not just the final filename.
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A modmentés nem követ szimbolikus linket vagy junctiont: " + current);
        }
    }
}
