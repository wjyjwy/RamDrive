using FluentAssertions;
using RamDrive.Core.FileSystem;
using WinFsp.Native;

namespace RamDrive.Core.Tests;

/// <summary>
/// Pure managed tests for <see cref="ReparsePointBuffer.CanReplace"/> — the 1:1 port of
/// WinFsp's FspFileSystemCanReplaceReparsePoint that gates FSCTL_SET/DELETE_REPARSE_POINT.
/// Runs cross-platform (no WinFsp / SDDL dependency).
/// </summary>
public class ReparsePointBufferTests
{
    private const uint Symlink = 0xA000000C;
    private const uint MountPoint = 0xA0000003;
    private const uint ThirdPartyTag = 0x20000000; // high bit clear == non-Microsoft

    private static byte[] Buffer(uint tag, uint? guidData1 = null)
    {
        var b = new byte[64];
        BitConverter.GetBytes(tag).CopyTo(b, 0);
        if (guidData1 is { } g)
            BitConverter.GetBytes(g).CopyTo(b, 4); // REPARSE_GUID_DATA_BUFFER.Data1
        return b;
    }

    [Fact]
    public void SameMicrosoftTag_IsAllowed()
    {
        ReparsePointBuffer.CanReplace(Buffer(Symlink), 64, Buffer(Symlink), 64)
            .Should().Be(NtStatus.Success);
        ReparsePointBuffer.CanReplace(Buffer(MountPoint), 24, Buffer(MountPoint), 30)
            .Should().Be(NtStatus.Success);
    }

    [Fact]
    public void DifferentTag_ReturnsTagMismatch()
    {
        ReparsePointBuffer.CanReplace(Buffer(Symlink), 64, Buffer(MountPoint), 64)
            .Should().Be(NtStatus.IoReparseTagMismatch);
    }

    [Fact]
    public void BufferShorterThanTagDword_ReturnsDataInvalid()
    {
        ReparsePointBuffer.CanReplace(new byte[3], 3, Buffer(Symlink), 64)
            .Should().Be(NtStatus.IoReparseDataInvalid);
        ReparsePointBuffer.CanReplace(Buffer(Symlink), 64, new byte[0], 0)
            .Should().Be(NtStatus.IoReparseDataInvalid);
        ReparsePointBuffer.CanReplace(null, 0, Buffer(Symlink), 64)
            .Should().Be(NtStatus.IoReparseDataInvalid);
    }

    [Fact]
    public void ThirdPartyTag_SameGuid_IsAllowed()
    {
        byte[] a = Buffer(ThirdPartyTag, guidData1: 0x11223344);
        for (int i = 4; i < 20; i++)                     // fill the whole 16-byte GUID
            a[i] = (byte)(0xA0 + i);
        byte[] b = (byte[])a.Clone();

        ReparsePointBuffer.CanReplace(a, a.Length, b, b.Length)
            .Should().Be(NtStatus.Success);
    }

    // Every byte of the 16-byte GUID must be compared: Data1(4B)@4, Data2(2B)@8,
    // Data3(2B)@10, Data4(8B)@12. The upstream FspFileSystemCanReplaceReparsePoint
    // compares it as four DWORDs at 4/8/12/16, so a mismatch anywhere blocks the
    // replacement. (A previous port only compared @4, @8, @12 and @16, silently
    // accepting changes to Data3 and to Data4[1..3]/[5..7].)
    [Theory]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)]
    [InlineData(16)] [InlineData(17)] [InlineData(18)] [InlineData(19)]
    public void ThirdPartyTag_ChangedGuidByte_ReturnsConflict(int offset)
    {
        byte[] a = Buffer(ThirdPartyTag, guidData1: 0x11223344);
        for (int i = 4; i < 20; i++)
            a[i] = (byte)(0xA0 + i);
        byte[] b = (byte[])a.Clone();
        b[offset] = (byte)(b[offset] ^ 0xFF);

        ReparsePointBuffer.CanReplace(a, a.Length, b, b.Length)
            .Should().Be(NtStatus.ReparseAttributeConflict);
    }

    [Fact]
    public void ThirdPartyTag_BufferTooShortForGuid_ReturnsConflict()
    {
        byte[] full = Buffer(ThirdPartyTag);
        byte[] truncated = new byte[8];
        Array.Copy(full, truncated, 8);

        ReparsePointBuffer.CanReplace(full, full.Length, truncated, truncated.Length)
            .Should().Be(NtStatus.ReparseAttributeConflict);
    }

    [Fact]
    public void TagLivesAtFirstDword()
    {
        ReparsePointBuffer.GetTag(Buffer(Symlink)).Should().Be(Symlink);
        ReparsePointBuffer.IsMicrosoftTag(Symlink).Should().BeTrue();
        ReparsePointBuffer.IsMicrosoftTag(ThirdPartyTag).Should().BeFalse();
    }
}
