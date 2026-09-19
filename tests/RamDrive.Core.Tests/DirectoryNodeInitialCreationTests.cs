using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using RamDrive.Core.Memory;

namespace RamDrive.Core.Tests;

/// <summary>
/// Exercises the REAL InitialDirectories pipeline: a <see cref="DirectoryNode"/> tree
/// (the shape bound from appsettings.jsonc's "InitialDirectories") fed into
/// <see cref="RamFileSystem.CreateInitialDirectories"/>, then verified as actual
/// directories on the in-memory filesystem.
///
/// The integration tests of the same name only call <c>Directory.CreateDirectory</c>
/// directly on the mount — they verify the volume is writable, but would keep passing even
/// if the config → directory code were completely broken. These assert the real path.
/// </summary>
public class DirectoryNodeInitialCreationTests
{
    private static (RamFileSystem fs, PagePool pool) NewFs()
    {
        var pool = new PagePool(
            new OptionsWrapper<RamDriveOptions>(new RamDriveOptions { CapacityMb = 2, PageSizeKb = 64 }),
            NullLogger<PagePool>.Instance);
        return (new RamFileSystem(pool), pool);
    }

    private static DirectoryNode Tree(Action<DirectoryNode> build)
    {
        var root = new DirectoryNode();
        build(root);
        return root;
    }

    [Fact]
    public void ConfigTree_CreatesDirectories_AndReturnsCount()
    {
        var (fs, pool) = NewFs();
        using var _ = pool;
        using var __ = fs;

        var cfg = Tree(d =>
        {
            d["Temp"] = new();
            d["Cache"] = Tree(c => { c["App1"] = new(); c["App2"] = new(); });
            d["Work"] = Tree(c => { c["Build"] = Tree(b => { b["Output"] = new(); }); });
        });

        // Temp, Cache, App1, App2, Work, Build, Output = 7 directories.
        fs.CreateInitialDirectories(cfg).Should().Be(7);

        fs.FindNode(@"\Temp").Should().NotBeNull();
        fs.FindNode(@"\Cache").Should().NotBeNull();
        fs.FindNode(@"\Cache\App1").Should().NotBeNull();
        fs.FindNode(@"\Cache\App2").Should().NotBeNull();
        fs.FindNode(@"\Work\Build\Output").Should().NotBeNull();
    }

    [Fact]
    public void ConfigTree_SecondRun_SkipsExisting_CountsOnlyNew()
    {
        var (fs, pool) = NewFs();
        using var _ = pool;
        using var __ = fs;

        var cfg = Tree(d => { d["Cache"] = Tree(c => { c["App1"] = new(); }); });
        fs.CreateInitialDirectories(cfg).Should().Be(2);

        // Re-running with a NEW subtree under an existing parent creates only the new part.
        var grown = Tree(d =>
        {
            d["Cache"] = Tree(c =>
            {
                c["App1"] = new();
                c["App2"] = new();
            });
        });
        fs.CreateInitialDirectories(grown).Should().Be(1); // just App2

        fs.FindNode(@"\Cache\App1").Should().NotBeNull();
        fs.FindNode(@"\Cache\App2").Should().NotBeNull();
    }

    [Fact]
    public void ConfigTree_InvalidNames_ThrowsAndCreatesNothing()
    {
        var (fs, pool) = NewFs();
        using var _ = pool;
        using var __ = fs;

        var cfg = Tree(d =>
        {
            d["valid"] = new();
            d["has|pipe"] = new(); // invalid on Windows
        });

        var act = () => fs.CreateInitialDirectories(cfg);
        act.Should().Throw<ArgumentException>();

        // Validation runs before any mutation, so nothing below the root was created.
        fs.ListDirectory(@"\").Should().BeEmpty();
    }

    [Fact]
    public void ConfigTree_CreatedDirectories_AreWritableAndEnumerated()
    {
        var (fs, pool) = NewFs();
        using var _ = pool;
        using var __ = fs;

        fs.CreateInitialDirectories(Tree(d => { d["Temp"] = new(); }));

        // The created directory must be usable, not just present.
        fs.CreateFile(@"\Temp\file.txt").Should().NotBeNull();
        fs.ListDirectory(@"\Temp")!.Select(n => n.Name).Should().Contain("file.txt");
    }
}
