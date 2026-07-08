using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Slate.Server.Storage;

namespace Slate.Server.Tests;

/// <summary>
/// Direct, DB-free tests of <see cref="IVaultStorage"/> - path validation, atomic writes, and the
/// in-memory write-marker registry used later by S5's file watcher for echo suppression. Each test
/// uses a fresh random vaultId (storage doesn't care whether a Vault row exists) so tests never
/// collide with each other on disk.
/// </summary>
[Collection(SlateTestCollection.Name)]
public class VaultStorageTests : IAsyncLifetime
{
    private readonly TestApp _app;
    private IVaultStorage _storage = null!;

    public VaultStorageTests(TestApp app)
    {
        _app = app;
    }

    public Task InitializeAsync()
    {
        _storage = _app.Services.GetRequiredService<IVaultStorage>();
        return _app.ResetDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    [InlineData("../../etc/passwd")]
    [InlineData("..")]
    [InlineData("a/..")]
    [InlineData(@"C:\x")]
    [InlineData(@"a\b")]
    [InlineData(@"a\..\..\x")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("trailing/")]
    [InlineData(".slate/conflicts/x.md")]
    [InlineData("notes/.slate")]
    [InlineData(".slate")]
    [InlineData("con.md")]
    [InlineData("CON")]
    [InlineData("COM1")]
    [InlineData("a/nul.txt")]
    [InlineData("a:b")]
    [InlineData("a<b>.md")]
    [InlineData("a|b.md")]
    [InlineData("a?b.md")]
    [InlineData("a*b.md")]
    public void SafePath_RejectsUnsafePaths(string path)
    {
        Assert.Throws<VaultPathException>(() => _storage.SafePath(path));
    }

    [Theory]
    [InlineData("note.md")]
    [InlineData("folder/note.md")]
    [InlineData("a/b/c/d.md")]
    [InlineData("weird but valid name.md")]
    [InlineData("2026-notes/july.md")]
    public void SafePath_AcceptsValidPaths(string path)
    {
        Assert.Equal(path, _storage.SafePath(path));
    }

    [Fact]
    public async Task WriteNoteAtomic_ProducesFileWithCorrectHashAndSize()
    {
        var vaultId = Guid.NewGuid();
        const string content = "# Hello\n\nWorld, this is a note.";

        var (hash, size) = await _storage.WriteNoteAtomicAsync(vaultId, "notes/a.md", content);

        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        Assert.Equal(expectedHash, hash);
        Assert.Equal(Encoding.UTF8.GetByteCount(content), size);

        var readBack = await _storage.ReadNoteAsync(vaultId, "notes/a.md");
        Assert.Equal(content, readBack);

        var fullPath = Path.Combine(_app.DataDir, "vaults", vaultId.ToString(), "notes", "a.md");
        Assert.True(File.Exists(fullPath));

        // No leftover temp file from the write-then-rename dance.
        var siblingFiles = Directory.GetFiles(Path.GetDirectoryName(fullPath)!);
        Assert.Single(siblingFiles);
    }

    [Fact]
    public async Task WriteNoteAtomic_Overwrite_ReplacesContentAndHash()
    {
        var vaultId = Guid.NewGuid();
        await _storage.WriteNoteAtomicAsync(vaultId, "note.md", "version one");
        var (hash, size) = await _storage.WriteNoteAtomicAsync(vaultId, "note.md", "version two, longer");

        var readBack = await _storage.ReadNoteAsync(vaultId, "note.md");
        Assert.Equal("version two, longer", readBack);
        Assert.Equal(Encoding.UTF8.GetByteCount("version two, longer"), size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("version two, longer"))), hash);
    }

    [Fact]
    public async Task ReadNote_MissingFile_ThrowsFileNotFoundException()
    {
        var vaultId = Guid.NewGuid();
        await Assert.ThrowsAsync<FileNotFoundException>(() => _storage.ReadNoteAsync(vaultId, "nope.md"));
    }

    [Fact]
    public async Task WriteNoteAtomic_RegistersWriteMarker_WasOurWriteMatchesThenConsumes()
    {
        var vaultId = Guid.NewGuid();
        var (hash, _) = await _storage.WriteNoteAtomicAsync(vaultId, "notes/b.md", "content");

        var fullPath = Path.Combine(_app.DataDir, "vaults", vaultId.ToString(), "notes", "b.md");

        Assert.True(_storage.WasOurWrite(fullPath, hash));
        // One-shot: a second check against the same (now-consumed) marker must not match again.
        Assert.False(_storage.WasOurWrite(fullPath, hash));
    }

    [Fact]
    public void WasOurWrite_WithWrongHash_ReturnsFalse()
    {
        var vaultId = Guid.NewGuid();
        var fullPath = Path.Combine(_app.DataDir, "vaults", vaultId.ToString(), "notes", "c.md");
        _storage.RegisterWrite(fullPath, "deadbeef");

        Assert.False(_storage.WasOurWrite(fullPath, "not-the-hash"));
    }

    [Fact]
    public void WasOurWrite_WithNoMarker_ReturnsFalse()
    {
        var vaultId = Guid.NewGuid();
        var fullPath = Path.Combine(_app.DataDir, "vaults", vaultId.ToString(), "notes", "never-written.md");

        Assert.False(_storage.WasOurWrite(fullPath, "any-hash"));
    }

    [Fact]
    public async Task Move_MovesFileToNewPath()
    {
        var vaultId = Guid.NewGuid();
        await _storage.WriteNoteAtomicAsync(vaultId, "a.md", "hi");

        await _storage.MoveAsync(vaultId, "a.md", "b/a.md");

        await Assert.ThrowsAsync<FileNotFoundException>(() => _storage.ReadNoteAsync(vaultId, "a.md"));
        Assert.Equal("hi", await _storage.ReadNoteAsync(vaultId, "b/a.md"));
    }

    [Fact]
    public async Task Delete_IsIdempotent()
    {
        var vaultId = Guid.NewGuid();
        await _storage.WriteNoteAtomicAsync(vaultId, "a.md", "hi");

        await _storage.DeleteAsync(vaultId, "a.md");
        await _storage.DeleteAsync(vaultId, "a.md"); // must not throw

        await Assert.ThrowsAsync<FileNotFoundException>(() => _storage.ReadNoteAsync(vaultId, "a.md"));
    }

    [Fact]
    public async Task ListAll_ExcludesReservedDotSlateFolder()
    {
        var vaultId = Guid.NewGuid();
        await _storage.WriteNoteAtomicAsync(vaultId, "a.md", "hi");
        await _storage.WriteNoteAtomicAsync(vaultId, "folder/b.md", "hi");
        _storage.CreateFolder(vaultId, "empty");

        // The reserved conflict-blob subtree is valid *on disk* (design spec) but must never be
        // surfaced by ListAll.
        var slateDir = Path.Combine(_app.DataDir, "vaults", vaultId.ToString(), ".slate", "conflicts");
        Directory.CreateDirectory(slateDir);
        File.WriteAllText(Path.Combine(slateDir, "x.md"), "conflict");

        var entries = _storage.ListAll(vaultId);

        Assert.Contains(entries, e => e.Path == "a.md" && !e.IsDirectory);
        Assert.Contains(entries, e => e.Path == "folder" && e.IsDirectory);
        Assert.Contains(entries, e => e.Path == "folder/b.md" && !e.IsDirectory);
        Assert.Contains(entries, e => e.Path == "empty" && e.IsDirectory);
        Assert.DoesNotContain(entries, e => e.Path.StartsWith(".slate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ListAll_ForMissingVaultRoot_ReturnsEmpty()
    {
        var entries = _storage.ListAll(Guid.NewGuid());
        Assert.Empty(entries);
    }

    [Fact]
    public void MoveFolder_MovesEntireSubtree()
    {
        var vaultId = Guid.NewGuid();
        _storage.CreateFolder(vaultId, "old/nested");

        _storage.MoveFolder(vaultId, "old", "new");

        Assert.False(_storage.FolderExists(vaultId, "old"));
        Assert.True(_storage.FolderExists(vaultId, "new"));
        Assert.True(_storage.FolderExists(vaultId, "new/nested"));
    }
}
