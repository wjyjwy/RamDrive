using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.Memory;

namespace RamDrive.Core.Tests;

/// <summary>
/// Configuration validation (P2-6): RamDriveOptions must refuse values that would crash or
/// dead-end the volume (PageSizeKb=0 → divide-by-zero, negative capacity → negative pool, ...).
/// The PagePool constructor throws InvalidOperationException with a descriptive message.
/// </summary>
public class RamDriveOptionsValidationTests
{
    [Fact]
    public void DefaultOptions_AreValid()
    {
        new RamDriveOptions().Validate().Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 64)]
    [InlineData(-1, 64)]
    [InlineData(-100, 64)]
    public void PagePool_CapacityMb_NonPositive_ThrowsWithMessage(long capacityMb, int pageSizeKb)
    {
        var opts = new OptionsWrapper<RamDriveOptions>(new RamDriveOptions
        {
            CapacityMb = capacityMb,
            PageSizeKb = pageSizeKb,
        });

        var act = () => new PagePool(opts, NullLogger<PagePool>.Instance);
        act.Should().Throw<InvalidOperationException>().WithMessage("*CapacityMb*");
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(2, -8)]
    public void PagePool_PageSizeKb_NonPositive_ThrowsWithMessage(long capacityMb, int pageSizeKb)
    {
        var opts = new OptionsWrapper<RamDriveOptions>(new RamDriveOptions
        {
            CapacityMb = capacityMb,
            PageSizeKb = pageSizeKb,
        });

        var act = () => new PagePool(opts, NullLogger<PagePool>.Instance);
        act.Should().Throw<InvalidOperationException>().WithMessage("*PageSizeKb*");
    }

    [Fact]
    public void PagePool_PageLargerThanCapacity_Throws()
    {
        var opts = new OptionsWrapper<RamDriveOptions>(new RamDriveOptions
        {
            CapacityMb = 1,
            PageSizeKb = 4096, // 4MB pages against a 1MB volume
        });

        var act = () => new PagePool(opts, NullLogger<PagePool>.Instance);
        act.Should().Throw<InvalidOperationException>().WithMessage("*fewer than 1 page*");
    }

    [Fact]
    public void VolumeLabel_Over32Chars_IsRejectedByValidation()
    {
        new RamDriveOptions { VolumeLabel = new string('X', 33) }
            .Validate()
            .Should().ContainSingle(e => e.Contains("VolumeLabel"));
    }

    [Fact]
    public void ValidOptions_CreatePoolWithoutError()
    {
        var opts = new OptionsWrapper<RamDriveOptions>(new RamDriveOptions { CapacityMb = 1, PageSizeKb = 64 });
        using var pool = new PagePool(opts, NullLogger<PagePool>.Instance);
        pool.MaxPages.Should().Be(16);
        pool.PageSize.Should().Be(64 * 1024);
    }
}