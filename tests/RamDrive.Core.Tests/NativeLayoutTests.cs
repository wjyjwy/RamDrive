using System.Runtime.InteropServices;
using FluentAssertions;
using WinFsp.Native.Interop;

namespace RamDrive.Core.Tests;

/// <summary>
/// Pins the in-memory layout of every interop struct against the native WinFsp ABI.
///
/// This exists because a wrong layout is INVISIBLE at build time and usually also at run
/// time: the struct is still the right SIZE, so no size assertion or buffer check catches
/// it — only the field OFFSETS are wrong, and the native side then reads garbage. That
/// exact failure mode was live in this repo: <see cref="FspFsctlNotifyInfo"/> carried
/// <c>Pack = 2</c>, which kept <c>sizeof</c> at the expected 12 while moving Filter from
/// offset 4 to 2 and Action from 8 to 6, silently corrupting every cache-invalidation
/// notification (the file system's sole coherence mechanism for a cached volume).
///
/// Offsets below are transcribed from the installed headers:
///   C:\Program Files (x86)\WinFsp\inc\winfsp\fsctl.h
///   C:\Program Files (x86)\WinFsp\inc\winfsp\winfsp.h
/// Each native struct also carries its own FSP_FSCTL_STATIC_ASSERT on sizeof, which is
/// quoted where relevant. If WinFsp ever changes one of these, the assert in the header
/// and this test must be updated together.
/// </summary>
public class NativeLayoutTests
{
    [Fact]
    public void NotifyInfo_MatchesFspFsctlNotifyInfo()
    {
        // fsctl.h:322-330 — UINT16 Size; UINT32 Filter; UINT32 Action; sizeof == 12.
        // The 12 bytes come from 2 bytes of padding after Size; the fields are NOT packed.
        Marshal.OffsetOf<FspFsctlNotifyInfo>(nameof(FspFsctlNotifyInfo.Size)).Should().Be(0);
        Marshal.OffsetOf<FspFsctlNotifyInfo>(nameof(FspFsctlNotifyInfo.Filter)).Should().Be(4);
        Marshal.OffsetOf<FspFsctlNotifyInfo>(nameof(FspFsctlNotifyInfo.Action)).Should().Be(8);
        Marshal.SizeOf<FspFsctlNotifyInfo>().Should().Be(12);
    }

    [Fact]
    public void NotifyInfo_WritesFieldsAtNativeOffsets()
    {
        // End-to-end proof of the above, independent of Marshal.OffsetOf: marshal a record
        // exactly as FileSystemHost.Notify does, then read the raw bytes back at the native
        // offsets. This is what the driver sees.
        const uint filter = 0x0000_0018; // FILE_NOTIFY_CHANGE_LAST_WRITE | CHANGE_SIZE
        const uint action = 3;           // FILE_ACTION_MODIFIED
        int size = Marshal.SizeOf<FspFsctlNotifyInfo>();

        var info = new FspFsctlNotifyInfo { Size = (ushort)(size + 8), Filter = filter, Action = action };
        var buffer = new byte[size + 8];
        IntPtr p = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            Marshal.StructureToPtr(info, p, fDeleteOld: false);
            Marshal.Copy(p, buffer, 0, buffer.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }

        BitConverter.ToUInt16(buffer, 0).Should().Be((ushort)(size + 8));
        BitConverter.ToUInt32(buffer, 4).Should().Be(filter);
        BitConverter.ToUInt32(buffer, 8).Should().Be(action);
    }

    [Fact]
    public void VolumeParams_MatchesFspFsctlVolumeParams()
    {
        // fsctl.h:192-266 — bitfield-packed 504-byte record. Pins the fields the adapter
        // sets plus the total size the header asserts.
        Marshal.SizeOf<FspVolumeParams>().Should().Be(504);
        Marshal.OffsetOf<FspVolumeParams>(nameof(FspVolumeParams.Version)).Should().Be(0);
        Marshal.OffsetOf<FspVolumeParams>(nameof(FspVolumeParams.SectorSize)).Should().Be(2);
        Marshal.OffsetOf<FspVolumeParams>(nameof(FspVolumeParams.VolumeCreationTime)).Should().Be(8);
        Marshal.OffsetOf<FspVolumeParams>(nameof(FspVolumeParams.VolumeSerialNumber)).Should().Be(16);
        Marshal.OffsetOf<FspVolumeParams>(nameof(FspVolumeParams.FileInfoTimeout)).Should().Be(32);
        Marshal.OffsetOf<FspVolumeParams>(nameof(FspVolumeParams.Flags)).Should().Be(36);
        Marshal.OffsetOf<FspVolumeParams>(nameof(FspVolumeParams.Prefix)).Should().Be(40);
        Marshal.OffsetOf<FspVolumeParams>(nameof(FspVolumeParams.FileSystemName)).Should().Be(424);
        Marshal.OffsetOf<FspVolumeParams>(nameof(FspVolumeParams.AdditionalFlags)).Should().Be(456);
    }

    [Fact]
    public void VolumeInfo_MatchesFspFsctlVolumeInfo()
    {
        // fsctl.h:268-276 — UINT64 TotalSize; UINT64 FreeSize; UINT16 VolumeLabelLength;
        // WCHAR VolumeLabel[32]; FSP_FSCTL_STATIC_ASSERT(88 == sizeof(FSP_FSCTL_VOLUME_INFO)).
        Marshal.SizeOf<FspVolumeInfo>().Should().Be(88);
        Marshal.OffsetOf<FspVolumeInfo>(nameof(FspVolumeInfo.TotalSize)).Should().Be(0);
        Marshal.OffsetOf<FspVolumeInfo>(nameof(FspVolumeInfo.FreeSize)).Should().Be(8);
        Marshal.OffsetOf<FspVolumeInfo>(nameof(FspVolumeInfo.VolumeLabelLength)).Should().Be(16);
        Marshal.OffsetOf<FspVolumeInfo>(nameof(FspVolumeInfo.VolumeLabel)).Should().Be(18);
    }

    [Fact]
    public void FileSystemInterface_HasSixtyFourSlots()
    {
        // winfsp.h:1094 — FSP_FSCTL_STATIC_ASSERT(sizeof(FSP_FILE_SYSTEM_INTERFACE)
        // == 64 * sizeof(NTSTATUS (*)())). Slot ORDER is checked by the adapter tests;
        // this only guards the total width, which a mis-counted reserved array would break.
        Marshal.SizeOf<FspFileSystemInterface>().Should().Be(64 * IntPtr.Size);
    }
}
