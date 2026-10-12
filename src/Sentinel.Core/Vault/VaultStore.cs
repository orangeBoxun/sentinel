using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Sentinel.Core.Vault;

/// <summary>
/// One thing in the vault. <see cref="Path"/> is the name as the user sees it — stored only inside
/// the encrypted index, so the list of what is in there is not readable from the disk.
/// </summary>
public sealed record VaultEntry(string Id, string Path, long Size, string AddedAt)
{
    public string Name => Path.Contains('/') ? Path[(Path.LastIndexOf('/') + 1)..] : Path;
}

/// <summary>
/// The vault's file storage: an encrypted index, and one encrypted blob per file.
///
/// <para>
/// Here rather than on a single big container file for two reasons. A container file has to be
/// rewritten to change anything, which is the wrong shape for a folder people will drop things
/// into; and a per-file blob means a corrupted or interrupted write costs one file rather than
/// everything. The cost is that blob sizes leak — how much is in the vault, not what.
/// </para>
/// <para>
/// Everything is AES-GCM, including the index, and every chunk is authenticated against its
/// position and its file. That last part is not decoration: without it, chunks could be reordered
/// or spliced between files and the result would still decrypt cleanly.
/// </para>
/// </summary>
public sealed class VaultStore : IDisposable
{
    private static readonly byte[] Magic = "SVB1"u8.ToArray();

    private static readonly byte[] BlobInfoPrefix = "sentinel-vault-blob"u8.ToArray();

    private const int ChunkSize = 1 << 20;   // 1 MiB — bounds memory, nothing more
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int IdSize = 16;
    private const int HeaderSize = 4 + 4 + 8 + 8;

    private readonly byte[] _dataKey;
    private readonly List<VaultEntry> _entries = [];
    private bool _disposed;

    private VaultStore(string root, byte[] dataKey)
    {
        Root = root;
        _dataKey = dataKey;
    }

    public string Root { get; }

    public IReadOnlyList<VaultEntry> Entries => _entries;

    private string IndexPath => Path.Combine(Root, "index.enc");

    private string BlobsPath => Path.Combine(Root, "blobs");

    // ------------------------------------------------------------------ 打开

    /// <summary>Creates the directory layout for a brand new vault.</summary>
    public static VaultStore Create(string root, byte[] dataKey)
    {
        Directory.CreateDirectory(Path.Combine(root, "blobs"));
        var store = new VaultStore(root, dataKey.ToArray());
        store.Save();
        return store;
    }

    /// <summary>Null when the key is wrong — the index is authenticated, so this is a definite answer.</summary>
    public static VaultStore? Open(string root, byte[] dataKey)
    {
        var index = Path.Combine(root, "index.enc");
        if (!File.Exists(index))
            return null;

        var store = new VaultStore(root, dataKey.ToArray());
        return store.Load() ? store : null;
    }

    private bool Load()
    {
        try
        {
            var payload = DecryptFile(IndexPath, IndexKey());
            if (payload is null)
                return false;

            var index = System.Text.Json.JsonSerializer.Deserialize<List<VaultEntry>>(payload);
            _entries.Clear();
            _entries.AddRange(index ?? []);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// The index is encrypted with a key of its own rather than the data key. Same key material, but
    /// a distinct derivation — so a blob and the index can never be swapped for one another.
    /// </summary>
    private byte[] IndexKey() =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _dataKey, 32, null, "sentinel-vault-index"u8.ToArray());

    private byte[] FileKey(byte[] fileId)
    {
        var info = new byte[BlobInfoPrefix.Length + fileId.Length];
        BlobInfoPrefix.CopyTo(info, 0);
        fileId.CopyTo(info, BlobInfoPrefix.Length);

        return HKDF.DeriveKey(HashAlgorithmName.SHA256, _dataKey, 32, null, info);
    }

    // ------------------------------------------------------------------ 增

    /// <summary>
    /// Copies a file — or a whole directory tree — into the vault. The source is left alone; this
    /// is a copy, and the caller decides when to remove the original.
    /// </summary>
    public void Import(string sourcePath, string vaultPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var target = Normalize(vaultPath);

        if (Directory.Exists(sourcePath))
        {
            ImportDirectory(sourcePath, target);
        }
        else if (File.Exists(sourcePath))
        {
            ImportFile(sourcePath, target);
        }
        else
        {
            throw new FileNotFoundException($"找不到要放进保险箱的东西：{sourcePath}", sourcePath);
        }

        Save();
    }

    private void ImportDirectory(string source, string target)
    {
        Directory.CreateDirectory(Path.Combine(Root, "blobs"));

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            ImportFile(file, $"{target}/{relative}");
        }
    }

    private void ImportFile(string source, string target)
    {
        Directory.CreateDirectory(BlobsPath);

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(IdSize)).ToLowerInvariant();
        var blob = Path.Combine(BlobsPath, id + ".bin");
        var fileId = Convert.FromHexString(id);
        var key = FileKey(fileId);

        long length;

        try
        {
            using var input = File.OpenRead(source);
            length = input.Length;

            using var output = File.Create(blob);
            EncryptTo(input, output, key, fileId, length);
        }
        catch
        {
            // A half-written blob is invisible to the index, so leaving it would only waste space —
            // but deleting it here keeps the directory honest about what is actually in the vault.
            TryDelete(blob);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        // Replacing rather than duplicating: importing a file under a name that is already taken
        // means "this is the new version", which is what dropping a file onto the same spot does
        // everywhere else.
        var existing = Find(target);
        if (existing is not null)
        {
            DeleteBlob(existing.Id);
            _entries.Remove(existing);
        }

        _entries.Add(new VaultEntry(id, target, length, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss")));
    }

    // ------------------------------------------------------------------ 取

    public VaultEntry? Find(string vaultPath)
    {
        var target = Normalize(vaultPath);
        return _entries.FirstOrDefault(e => e.Path.Equals(target, StringComparison.Ordinal));
    }

    /// <summary>Everything under a folder, for the recursive operations.</summary>
    public IEnumerable<VaultEntry> Children(string folder)
    {
        var prefix = Normalize(folder) + "/";
        return _entries.Where(e => e.Path.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Writes a copy back out in the clear. The caller is responsible for what happens to it
    /// afterwards — the vault cannot protect a file it has handed back.
    /// </summary>
    public void Extract(string vaultPath, string destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var entry = Find(vaultPath) ?? throw new FileNotFoundException($"保险箱里没有：{vaultPath}");
        var fileId = Convert.FromHexString(entry.Id);
        var key = FileKey(fileId);

        try
        {
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using var input = File.OpenRead(Path.Combine(BlobsPath, entry.Id + ".bin"));
            using var output = File.Create(destination);
            DecryptTo(input, output, key, fileId, entry.Size);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Replaces a blob's content in place, keeping its name and position.</summary>
    public void Replace(string vaultPath, string sourceFile)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var entry = Find(vaultPath) ?? throw new FileNotFoundException($"保险箱里没有：{vaultPath}");
        var fileId = Convert.FromHexString(entry.Id);
        var key = FileKey(fileId);
        long length;

        try
        {
            using var input = File.OpenRead(sourceFile);
            length = input.Length;

            using var output = File.Create(Path.Combine(BlobsPath, entry.Id + ".bin"));
            EncryptTo(input, output, key, fileId, length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        _entries[_entries.FindIndex(e => e.Id == entry.Id)] =
            entry with { Size = length, AddedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss") };

        Save();
    }

    // ------------------------------------------------------------------ 删

    /// <summary>Removes a file, or a folder and everything under it. Returns how many entries went.</summary>
    public int Remove(string vaultPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var target = Normalize(vaultPath);
        var doomed = _entries
            .Where(e => e.Path.Equals(target, StringComparison.Ordinal)
                        || e.Path.StartsWith(target + "/", StringComparison.Ordinal))
            .ToList();

        foreach (var entry in doomed)
        {
            DeleteBlob(entry.Id);
            _entries.Remove(entry);
        }

        if (doomed.Count > 0)
            Save();

        return doomed.Count;
    }

    /// <summary>
    /// Overwrites before unlinking. Worth doing and worth being honest about: on a copy-on-write or
    /// journaling filesystem, and on any SSD with wear levelling, this reduces the window rather
    /// than closing it. It is not a guarantee that the bytes are gone.
    /// </summary>
    private void DeleteBlob(string id)
    {
        var path = Path.Combine(BlobsPath, id + ".bin");

        if (!File.Exists(path))
            return;

        try
        {
            var length = new FileInfo(path).Length;
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Write))
            {
                var buffer = new byte[Math.Min(length, ChunkSize)];
                RandomNumberGenerator.Fill(buffer);

                var remaining = length;
                while (remaining > 0)
                {
                    var count = (int)Math.Min(buffer.Length, remaining);
                    stream.Write(buffer, 0, count);
                    remaining -= count;
                }

                stream.Flush(true);
            }
        }
        catch
        {
            // Not being able to scrub the bytes is not a reason to leave it in the vault: it is
            // encrypted either way, and the index — which is what makes it "in the vault" — is
            // updated regardless.
        }

        // Retried, because a scanner holding the file for a moment would otherwise leave a blob the
        // user believes they deleted.
        IoRetry.Try(() => File.Delete(path));
    }

    // ------------------------------------------------------------------ 落盘

    public void Save()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Directory.CreateDirectory(Root);

        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_entries);
        EncryptBuffer(payload, IndexKey(), IndexPath);
    }

    private static void EncryptBuffer(byte[] plain, byte[] key, string path)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plain, cipher, tag);

        // Written to a temporary name first: an index truncated by a crash would take the whole
        // vault with it, and this is the one file where that is true.
        //
        // Retried, because this is the most frequently rewritten file in the vault — every import
        // and every removal goes through here — and Windows refuses to replace a file while a
        // scanner or an indexer is holding it open. Without the retry the user sees "放不进去：
        // 对路径的访问被拒绝" for something that would have worked a moment later.
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, [.. nonce, .. tag, .. cipher]);

        if (!IoRetry.Try(() => File.Move(temporary, path, overwrite: true)))
        {
            TryDelete(temporary);
            throw new IOException($"写不进去：{path} —— 文件可能正被别的程序占着。");
        }

        CryptographicOperations.ZeroMemory(key);
    }

    private byte[]? DecryptFile(string path, byte[] key)
    {
        try
        {
            var blob = File.ReadAllBytes(path);
            if (blob.Length < NonceSize + TagSize)
                return null;

            var plain = new byte[blob.Length - NonceSize - TagSize];

            using (var aes = new AesGcm(key, TagSize))
                aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), plain);

            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // ------------------------------------------------------------------ 分块加解密

    private static void EncryptTo(Stream input, Stream output, byte[] fileKey, byte[] fileId, long length)
    {
        var baseNonce = RandomNumberGenerator.GetBytes(8);
        var totalChunks = (int)((length + ChunkSize - 1) / ChunkSize);

        Span<byte> header = stackalloc byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], ChunkSize);
        baseNonce.CopyTo(header[8..]);
        BinaryPrimitives.WriteInt64LittleEndian(header[16..], length);
        output.Write(header);

        var buffer = new byte[ChunkSize];

        using var aes = new AesGcm(fileKey, TagSize);

        for (var index = 0; index < totalChunks; index++)
        {
            var count = ReadFully(input, buffer);
            var nonce = ChunkNonce(baseNonce, index);
            var aad = ChunkAad(fileId, index, totalChunks);

            var cipher = new byte[count];
            var tag = new byte[TagSize];
            aes.Encrypt(nonce, buffer.AsSpan(0, count), cipher, tag, aad);

            output.Write(tag);
            output.Write(cipher);
        }
    }

    private static void DecryptTo(Stream input, Stream output, byte[] fileKey, byte[] fileId, long length)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        if (ReadFully(input, header) != HeaderSize || !header[..4].SequenceEqual(Magic))
            throw new InvalidDataException("不是一个 Sentinel 保险箱文件。");

        var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        if (chunkSize <= 0 || chunkSize > (1 << 26))
            throw new InvalidDataException("分块大小不合理。");

        var baseNonce = header[8..16].ToArray();
        var storedLength = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);

        if (storedLength != length)
            throw new InvalidDataException("文件长度与索引不符，保险箱可能被改动过。");

        var totalChunks = (int)((length + chunkSize - 1) / chunkSize);

        using var aes = new AesGcm(fileKey, TagSize);

        for (var index = 0; index < totalChunks; index++)
        {
            var plainLength = (int)Math.Min(chunkSize, length - (long)index * chunkSize);

            var tag = new byte[TagSize];
            var cipher = new byte[plainLength];

            if (ReadFully(input, tag) != TagSize || ReadFully(input, cipher) != plainLength)
                throw new InvalidDataException("文件在中间被截断了。");

            var plain = new byte[plainLength];
            var nonce = ChunkNonce(baseNonce, index);
            var aad = ChunkAad(fileId, index, totalChunks);

            aes.Decrypt(nonce, cipher, tag, plain, aad);
            output.Write(plain);
        }
    }

    /// <summary>
    /// A chunk's nonce is its position under a per-file random prefix. Deterministic, so the nonce
    /// never has to be written down, and unique because the prefix is.
    /// </summary>
    private static byte[] ChunkNonce(byte[] baseNonce, int index)
    {
        var nonce = new byte[NonceSize];
        baseNonce.CopyTo(nonce, 0);
        BinaryPrimitives.WriteInt32LittleEndian(nonce.AsSpan(8), index);
        return nonce;
    }

    /// <summary>
    /// Ties each chunk to its file, its position, and how many chunks there should be — so chunks
    /// cannot be reordered, moved between files, or dropped from the end.
    /// </summary>
    private static byte[] ChunkAad(byte[] fileId, int index, int totalChunks)
    {
        var aad = new byte[IdSize + 8];
        fileId.CopyTo(aad, 0);
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(IdSize), index);
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(IdSize + 4), totalChunks);
        return aad;
    }

    private static int ReadFully(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
                break;

            total += read;
        }

        return total;
    }

    private static int ReadFully(Stream stream, byte[] buffer) => ReadFully(stream, buffer.AsSpan());

    // ------------------------------------------------------------------ 杂项

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Nothing to do about a file we were only tidying up.
        }
    }

    private static string Normalize(string vaultPath) =>
        vaultPath.Replace('\\', '/').Trim('/');

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CryptographicOperations.ZeroMemory(_dataKey);
    }
}
