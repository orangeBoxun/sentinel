using System.IO;
using System.Security.Cryptography;
using System.Text;
using Sentinel.Core.Vault;
using Xunit;

namespace Sentinel.Core.Tests;

public class VaultStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "sentinel-store-" + Guid.NewGuid().ToString("N"));

    private readonly byte[] _key = VaultCrypto.NewKey();

    private string Root => Path.Combine(_dir, "vault");

    private string Source => Path.Combine(_dir, "source");

    public VaultStoreTests()
    {
        Directory.CreateDirectory(Source);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_key);

        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // Leftover temp directory is not worth failing over.
        }
    }

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(Source, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Bytes(int count, int seed = 1)
    {
        var data = new byte[count];
        new Random(seed).NextBytes(data);
        return data;
    }

    // ------------------------------------------------------------------ 往返

    [Fact]
    public void A_file_survives_the_round_trip()
    {
        var content = Encoding.UTF8.GetBytes("一段要被保险箱保管的文字。");

        using var store = VaultStore.Create(Root, _key);
        store.Import(Write("notes.txt", content), "notes.txt");

        var output = Path.Combine(_dir, "out.txt");
        store.Extract("notes.txt", output);

        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void A_file_larger_than_one_chunk_survives()
    {
        // 3.5 MiB across four chunks: the last one short, which is where an off-by-one hides.
        var content = Bytes(3 * (1 << 20) + (1 << 19), seed: 7);

        using var store = VaultStore.Create(Root, _key);
        store.Import(Write("big.bin", content), "big.bin");

        var output = Path.Combine(_dir, "big-out.bin");
        store.Extract("big.bin", output);

        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void An_empty_file_survives()
    {
        using var store = VaultStore.Create(Root, _key);
        store.Import(Write("empty.txt", []), "empty.txt");

        var output = Path.Combine(_dir, "empty-out.txt");
        store.Extract("empty.txt", output);

        Assert.Empty(File.ReadAllBytes(output));
    }

    [Fact]
    public void A_directory_tree_keeps_its_shape()
    {
        Write(@"a\b\one.txt", [1, 2, 3]);
        Write(@"a\two.txt", [4, 5]);

        using var store = VaultStore.Create(Root, _key);
        store.Import(Path.Combine(Source, "a"), "文档");

        Assert.Equal(2, store.Entries.Count);
        Assert.Contains(store.Entries, e => e.Path == "文档/b/one.txt");
        Assert.Contains(store.Entries, e => e.Path == "文档/two.txt");

        var output = Path.Combine(_dir, "one-out.txt");
        store.Extract("文档/b/one.txt", output);
        Assert.Equal<byte[]>([1, 2, 3], File.ReadAllBytes(output));
    }

    [Fact]
    public void Everything_is_still_there_after_reopening()
    {
        using (var store = VaultStore.Create(Root, _key))
            store.Import(Write("a.txt", [9, 9, 9]), "a.txt");

        using var reopened = VaultStore.Open(Root, _key);
        Assert.NotNull(reopened);
        Assert.Single(reopened!.Entries);
        Assert.Equal("a.txt", reopened.Entries[0].Path);
    }

    [Fact]
    public void The_wrong_key_opens_nothing()
    {
        using (var store = VaultStore.Create(Root, _key))
            store.Import(Write("a.txt", [1]), "a.txt");

        var stranger = VaultCrypto.NewKey();
        Assert.Null(VaultStore.Open(Root, stranger));
    }

    // ------------------------------------------------------------------ 不泄漏

    [Fact]
    public void The_names_of_what_is_inside_are_not_on_the_disk()
    {
        const string name = "离婚协议书-不要给别人看.pdf";

        using (var store = VaultStore.Create(Root, _key))
            store.Import(Write("x.pdf", Encoding.UTF8.GetBytes("内容")), name);

        // This is the whole reason the index is encrypted: the files are unreadable either way, but
        // a plaintext index would still announce *what* is being kept.
        var needle = Encoding.UTF8.GetBytes(name);

        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            Assert.False(Contains(bytes, needle), $"{file} 里能直接读到文件名");
        }
    }

    [Fact]
    public void The_contents_cannot_be_read_from_the_disk()
    {
        var content = Encoding.UTF8.GetBytes("SENTINEL-VAULT-PLAINTEXT-MARKER");

        using (var store = VaultStore.Create(Root, _key))
            store.Import(Write("secret.txt", content), "secret.txt");

        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            Assert.False(Contains(File.ReadAllBytes(file), content), $"{file} 里能直接读到明文");
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return false;

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] == needle[j])
                    continue;

                match = false;
                break;
            }

            if (match)
                return true;
        }

        return false;
    }

    // ------------------------------------------------------------------ 改动

    [Fact]
    public void Importing_over_a_name_replaces_it()
    {
        using var store = VaultStore.Create(Root, _key);

        store.Import(Write("v1.txt", [1, 1, 1]), "same.txt");
        store.Import(Write("v2.txt", [2, 2]), "same.txt");

        Assert.Single(store.Entries);
        Assert.Equal(2, store.Entries[0].Size);

        var output = Path.Combine(_dir, "same-out.txt");
        store.Extract("same.txt", output);
        Assert.Equal<byte[]>([2, 2], File.ReadAllBytes(output));
    }

    [Fact]
    public void Replace_swaps_the_contents_and_keeps_the_name()
    {
        using var store = VaultStore.Create(Root, _key);
        store.Import(Write("old.txt", [1, 1, 1]), "doc.txt");

        var id = store.Entries[0].Id;
        store.Replace("doc.txt", Write("new.txt", [7, 7, 7, 7]));

        Assert.Equal(id, store.Entries[0].Id);
        Assert.Equal(4, store.Entries[0].Size);

        var output = Path.Combine(_dir, "doc-out.txt");
        store.Extract("doc.txt", output);
        Assert.Equal<byte[]>([7, 7, 7, 7], File.ReadAllBytes(output));
    }

    [Fact]
    public void Removing_a_folder_takes_everything_under_it()
    {
        Write(@"tree\a.txt", [1]);
        Write(@"tree\sub\b.txt", [2]);
        Write("keep.txt", [3]);

        using var store = VaultStore.Create(Root, _key);
        store.Import(Path.Combine(Source, "tree"), "树");
        store.Import(Path.Combine(Source, "keep.txt"), "keep.txt");

        Assert.Equal(3, store.Entries.Count);
        Assert.Equal(2, store.Remove("树"));

        Assert.Single(store.Entries);
        Assert.Equal("keep.txt", store.Entries[0].Path);
    }

    [Fact]
    public void Removing_a_file_leaves_no_blob_behind()
    {
        using var store = VaultStore.Create(Root, _key);
        store.Import(Write("gone.txt", [1, 2, 3]), "gone.txt");

        var blobs = Path.Combine(Root, "blobs");
        Assert.Single(Directory.GetFiles(blobs));

        Assert.Equal(1, store.Remove("gone.txt"));

        // Named, not just counted: this has failed intermittently, and "which file is still there"
        // is the whole diagnosis.
        var leftover = Directory.GetFiles(blobs);
        Assert.True(leftover.Length == 0, "还有残留：" + string.Join(", ", leftover.Select(System.IO.Path.GetFileName)));
    }

    // ------------------------------------------------------------------ 篡改

    [Fact]
    public void A_trimmed_blob_is_refused()
    {
        using var store = VaultStore.Create(Root, _key);
        store.Import(Write("big.bin", Bytes(3 * (1 << 20))), "big.bin");

        var blob = Directory.GetFiles(Path.Combine(Root, "blobs")).Single();
        var bytes = File.ReadAllBytes(blob);
        File.WriteAllBytes(blob, bytes[..(bytes.Length - 100)]);

        Assert.ThrowsAny<Exception>(() => store.Extract("big.bin", Path.Combine(_dir, "trimmed.bin")));
    }

    [Fact]
    public void Flipped_bits_in_a_blob_are_refused()
    {
        using var store = VaultStore.Create(Root, _key);
        store.Import(Write("a.bin", Bytes(4096, seed: 3)), "a.bin");

        var blob = Directory.GetFiles(Path.Combine(Root, "blobs")).Single();
        var bytes = File.ReadAllBytes(blob);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(blob, bytes);

        Assert.ThrowsAny<Exception>(() => store.Extract("a.bin", Path.Combine(_dir, "flipped.bin")));
    }

    [Fact]
    public void Swapping_two_blobs_is_refused()
    {
        using var store = VaultStore.Create(Root, _key);
        store.Import(Write("one.bin", Bytes(2048, seed: 1)), "one.bin");
        store.Import(Write("two.bin", Bytes(2048, seed: 2)), "two.bin");

        var blobs = Directory.GetFiles(Path.Combine(Root, "blobs"));
        Assert.Equal(2, blobs.Length);

        var first = store.Entries.Single(e => e.Path == "one.bin").Id;
        var second = store.Entries.Single(e => e.Path == "two.bin").Id;

        // Give the second file's blob the first file's id — every chunk is still individually valid,
        // and only the per-file binding in the AAD catches it.
        File.Move(
            Path.Combine(Root, "blobs", second + ".bin"),
            Path.Combine(Root, "blobs", first + ".bin"),
            overwrite: true);

        Assert.ThrowsAny<Exception>(() => store.Extract("one.bin", Path.Combine(_dir, "swapped.bin")));
    }

    [Fact]
    public void The_index_cannot_be_replaced_with_another_vaults()
    {
        using (var store = VaultStore.Create(Root, _key))
            store.Import(Write("a.txt", [1]), "a.txt");

        var otherRoot = Path.Combine(_dir, "other");
        using (var other = VaultStore.Create(otherRoot, _key))
            other.Import(Write("b.txt", [2]), "b.txt");

        File.Copy(Path.Combine(otherRoot, "index.enc"), Path.Combine(Root, "index.enc"), overwrite: true);

        // Same data key, so the index decrypts — but it now lists a file whose blob was never here.
        var reopened = VaultStore.Open(Root, _key);
        Assert.NotNull(reopened);
        Assert.ThrowsAny<Exception>(() => reopened!.Extract("b.txt", Path.Combine(_dir, "b-out.txt")));

        reopened.Dispose();
    }
}
