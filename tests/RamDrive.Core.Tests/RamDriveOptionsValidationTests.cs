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

    [Fact]
    public void PermanentCache_WithoutNotifications_IsAllowedButWarned()
    {
        // Hazardous, but legal: the differential test leg uses exactly this combination to
        // keep its comparison a pure semantic one. Validate() must NOT reject it — the host
        // logs a warning instead (see WinFspHostedService).
        var errors = new RamDriveOptions
        {
            EnableKernelCache = true,
            EnableNotifications = false,
            FileInfoTimeoutMs = uint.MaxValue,
        }.Validate();

        errors.Should().BeEmpty();
    }

    [Fact]
    public void PermanentCache_WithNotifications_IsAccepted()
    {
        var errors = new RamDriveOptions
        {
            EnableKernelCache = true,
            EnableNotifications = true,
            FileInfoTimeoutMs = uint.MaxValue,
        }.Validate();

        errors.Should().BeEmpty();
    }

    [Fact]
    public void PermanentCache_WithKernelCacheDisabled_IsAccepted()
    {
        // EnableKernelCache=false pins the host FileInfoTimeout to 0, so the permanent
        // value is inert and the hazard does not exist.
        var errors = new RamDriveOptions
        {
            EnableKernelCache = false,
            EnableNotifications = false,
            FileInfoTimeoutMs = uint.MaxValue,
        }.Validate();

        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"R:\")]
    [InlineData("R:")]
    [InlineData("r:")]
    [InlineData(@"Z:\")]
    public void SupportedMountPointForms_AreAccepted(string mountPoint)
    {
        new RamDriveOptions { MountPoint = mountPoint }
            .Validate()
            .Should().NotContain(e => e.Contains("MountPoint"));
    }

    [Theory]
    [InlineData(@"R://")]            // forward slashes → "\\.\R://" reaches the native call
    [InlineData(@"\\.\R:")]          // host prepends "\\.\" again → "\\.\\.\R:"
    [InlineData(@"\\.\R:\")]
    [InlineData("R")]                // no colon
    [InlineData("1:")]               // not a letter
    [InlineData(@"\\server\share")]  // UNC is not supported by the host
    [InlineData(@"R:\Temp")]         // a path, not a mount point
    public void MalformedMountPoints_AreRejected(string mountPoint)
    {
        new RamDriveOptions { MountPoint = mountPoint }
            .Validate()
            .Should().Contain(e => e.Contains("MountPoint"),
                "a malformed mount point crashes FspFileSystemSetMountPointEx");
    }
}