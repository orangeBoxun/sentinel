using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sentinel.Core.Vault;

/// <summary>One file inside a backup, with the hash that proves it survived the trip.</summary>
public sealed record BackupFile(string Name, string Sha256, long Bytes);

public sealed record BackupManifest(int Version, string CreatedAt, List<BackupFile> Files);

/// <summary>
/// Getting the vault off the machine.
///
/// <para>
/// A backup here is a byte-for-byte copy of the vault directory in one file. Nothing is decrypted
/// and no new secret is introduced: what is inside is still encrypted with the data key, and that
/// key is still wrapped by the user's password and the guardian code. The point is not a new kind
/// of protection — it is that a disk that dies, or an uninstall run with <c>-Purge</c>, currently
/// takes everything with it.
/// </para>
/// <para>
/// <b>Restoring does not lift a seal.</b> The seal marker lives beside the config, outside the vault
/// directory, so putting back an older copy of the vault does not put back the door that copy had.
/// That is what makes a backup safe to keep: it cannot be used to undo the one thing the vault is
/// for.
/// </para>
/// </summary>
public static class VaultBackup
{
    public const string Extension = ".sentinelbackup";

    private const string ManifestEntry = "backup.json";
    private const string VaultFolder = "vault";
    private const string ExportRecordName = "export.json";
    private const string KeysFileName = "keys.json";
    private const int FormatVersion = 1;

    /// <summary>Working folders, both inside the vault directory. See <see cref="Restore"/>.</summary>
    private const string StagingFolder = ".restoring";

    private const string PreviousFolder = ".previous";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>A name that sorts by date, so a folder of these reads in order.</summary>
    public static string DefaultName(DateTimeOffset now) => $"Sentinel保险箱-{now:yyyyMMdd-HHmmss}{Extension}";

    // ------------------------------------------------------------------ 导出

    public static VaultResult Export(string configPath, string destination, DateTimeOffset? at = null)
    {
        if (!VaultGuard.VaultExists(configPath))
            return VaultResult.Fail("还没有保险箱，没有东西可以导出。");

        var root = VaultGuard.VaultRootFor(configPath);
        var when = at ?? DateTimeOffset.Now;
        var temporary = destination + ".partial";

        try
        {
            var files = Directory
                .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => IsVaultContent(root, path))
                .Select(path => new BackupFile(
                    Relative(root, path),
                    HashFile(path),
                    new FileInfo(path).Length))
                .OrderBy(file => file.Name, StringComparer.Ordinal)
                .ToList();

            var manifest = new BackupManifest(FormatVersion, when.ToString("O"), files);

            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                WriteManifest(zip, manifest);

                foreach (var file in files)
                    zip.CreateEntryFromFile(Path.Combine(root, file.Name.Replace('/', Path.DirectorySeparatorChar)),
                        $"{VaultFolder}/{file.Name}", CompressionLevel.Optimal);
            }

            if (!IoRetry.Try(() => File.Move(temporary, destination, overwrite: true)))
                throw new IOException($"写不进去：{destination}");

            WriteExportRecord(root, when, destination);

            return VaultResult.Success;
        }
        catch (Exception ex)
        {
            TryDelete(temporary);
            return VaultResult.Fail($"导出失败：{ex.Message}");
        }
    }

    /// <summary>
    /// When a backup was last made, or null if never. Drives the nag: a backup nobody has refreshed
    /// in months is not a backup, and the failure it protects against is the one you cannot retry.
    /// </summary>
    public static DateTimeOffset? LastExport(string configPath)
    {
        try
        {
            var path = Path.Combine(VaultGuard.VaultRootFor(configPath), ExportRecordName);
            if (!File.Exists(path))
                return null;

            var record = JsonSerializer.Deserialize<ExportRecord>(File.ReadAllText(path), Options);
            return record is not null && DateTimeOffset.TryParse(record.At, out var at) ? at : null;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ 恢复

    /// <summary>
    /// Puts a backup back.
    ///
    /// <para>
    /// Everything it does happens <b>inside</b> the vault directory, and that is not a stylistic
    /// choice. The vault sits in a directory the user cannot write — the install directory is
    /// read-only for them, and only <c>vault\</c> itself is writable. An earlier version extracted
    /// to a sibling of the vault and swapped the two, which is the tidy way to do it and which
    /// fails with access denied on every real install. The tests passed because a temp directory is
    /// writable everywhere; the only thing that would have caught it is asserting that nothing
    /// outside <c>vault\</c> is created, which the test now does.
    /// </para>
    /// <para>
    /// It is still a swap rather than an overwrite: the live contents are renamed into a holding
    /// folder first, so a failure part-way through puts them back instead of leaving nothing.
    /// </para>
    /// <para>
    /// <b>A restore brings back the credentials the backup was taken with.</b> The key file is part
    /// of the snapshot like everything else, so a password changed after the backup was made stops
    /// opening the vault and the earlier one starts again. That is a consequence of the file being
    /// a snapshot rather than a defect, but it is not something anyone would guess — the caller is
    /// expected to say it before asking for confirmation.
    /// </para>
    /// </summary>
    public static VaultResult Restore(string configPath, string source)
    {
        if (!File.Exists(source))
            return VaultResult.Fail($"找不到备份文件：{source}");

        var root = VaultGuard.VaultRootFor(configPath);
        var staging = Path.Combine(root, StagingFolder);
        var previous = Path.Combine(root, PreviousFolder);

        try
        {
            Directory.CreateDirectory(root);

            // A run that was killed between the two renames leaves the vault looking empty with
            // everything sitting in the holding folder. Put it back before anything else reads it.
            RecoverInterruptedSwap(configPath, root, previous);

            using var zip = ZipFile.OpenRead(source);

            var manifest = ReadManifest(zip);
            if (manifest is null)
                return VaultResult.Fail("这不是 Sentinel 的备份文件（里面没有 backup.json）。");

            if (manifest.Version > FormatVersion)
                return VaultResult.Fail($"这份备份是更新的版本（{manifest.Version}）做的，这个版本的程序读不了。");

            // Verify first, extract second. A half-restored vault is worse than a refused one, and
            // the whole point of the manifest is that a damaged backup can be refused before it
            // replaces something that still works.
            foreach (var file in manifest.Files)
            {
                var entry = zip.GetEntry($"{VaultFolder}/{file.Name}");
                if (entry is null)
                    return VaultResult.Fail($"备份不完整：缺少 {file.Name}");

                if (!HashEntry(entry).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return VaultResult.Fail($"备份已损坏：{file.Name} 的内容对不上清单里的哈希。");
            }

            TryDeleteDirectory(staging);

            foreach (var file in manifest.Files)
            {
                // The list comes from the backup file, which may have come from anywhere. Refusing
                // anything that resolves outside the staging folder is what stops a crafted backup
                // writing wherever it likes.
                var target = SafeJoin(staging, file.Name);
                if (target is null)
                    return VaultResult.Fail($"备份里的路径不合法：{file.Name}");

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                using var input = zip.GetEntry($"{VaultFolder}/{file.Name}")!.Open();
                using var output = File.Create(target);
                input.CopyTo(output);
            }

            // The swap. Both renames stay within vault\ — same volume, so they are renames rather
            // than copies, whatever the vault's size.
            TryDeleteDirectory(previous);
            Directory.CreateDirectory(previous);

            foreach (var entry in Directory.GetFileSystemEntries(root))
            {
                var name = Path.GetFileName(entry);
                if (name is StagingFolder or PreviousFolder)
                    continue;

                if (!IoRetry.Try(() => Directory.Move(entry, Path.Combine(previous, name))))
                    return VaultResult.Fail("换不上去：保险箱正被别的程序占着（索引器或杀毒软件可能正拿着它）。过一会儿再试。");
            }

            try
            {
                foreach (var entry in Directory.GetFileSystemEntries(staging))
                {
                    if (!IoRetry.Try(() => Directory.Move(entry, Path.Combine(root, Path.GetFileName(entry)))))
                        throw new IOException("换上去的时候被占用了");
                }
            }
            catch
            {
                foreach (var entry in Directory.GetFileSystemEntries(previous))
                    Directory.Move(entry, Path.Combine(root, Path.GetFileName(entry)));

                TryDeleteDirectory(previous);
                TryDeleteDirectory(staging);
                return VaultResult.Fail("换上去的时候失败了 —— 原来的保险箱已经放回原位，没有动。");
            }

            TryDeleteDirectory(previous);
            TryDeleteDirectory(staging);

            // The record described a backup of a vault that no longer exists here.
            TryDelete(Path.Combine(root, ExportRecordName));

            return VaultResult.Success;
        }
        catch (InvalidDataException)
        {
            return VaultResult.Fail("这个文件不是有效的备份（压缩包损坏或格式不对）。");
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(staging);

            // Whatever went wrong, the vault must not be left empty with its contents in the
            // holding folder.
            RecoverInterruptedSwap(configPath, root, previous);

            return VaultResult.Fail($"恢复失败：{ex.Message}");
        }
    }

    /// <summary>
    /// Undoes a swap that was interrupted: the holding folder is only still there because the
    /// process died between renaming the old contents away and renaming the new ones in.
    /// </summary>
    private static void RecoverInterruptedSwap(string configPath, string root, string previous)
    {
        if (!Directory.Exists(previous))
            return;

        if (File.Exists(Path.Combine(root, KeysFileName)))
            return;   // the vault is intact; the holding folder is a leftover

        foreach (var entry in Directory.GetFileSystemEntries(previous))
            Directory.Move(entry, Path.Combine(root, Path.GetFileName(entry)));

        TryDeleteDirectory(previous);
    }

    // ------------------------------------------------------------------ 杂项

    private static void WriteManifest(ZipArchive zip, BackupManifest manifest)
    {
        using var stream = zip.CreateEntry(ManifestEntry).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(manifest, Options));
    }

    private static BackupManifest? ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(ManifestEntry);
        if (entry is null)
            return null;

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        return JsonSerializer.Deserialize<BackupManifest>(reader.ReadToEnd(), Options);
    }

    private static void WriteExportRecord(string root, DateTimeOffset at, string destination)
    {
        try
        {
            var record = new ExportRecord
            {
                At = at.ToString("O"),
                Destination = destination,
            };

            File.WriteAllText(Path.Combine(root, ExportRecordName), JsonSerializer.Serialize(record, Options));
        }
        catch
        {
            // Losing the record only costs an inaccurate "last exported" line, never a backup.
        }
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>
    /// Dot-prefixed entries are this class's own bookkeeping — the export record, and the holding
    /// folders a restore uses — rather than anything the user put in the vault. Backing them up
    /// would copy the working state of one machine onto another.
    /// </summary>
    private static bool IsVaultContent(string root, string path)
    {
        var relative = Relative(root, path);

        return !relative.StartsWith('.')
            && !Path.GetFileName(relative).Equals(ExportRecordName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? SafeJoin(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string HashEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Tidying up only.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Tidying up only.
        }
    }

    private sealed record ExportRecord
    {
        public string At { get; set; } = "";

        public string Destination { get; set; } = "";
    }
}
