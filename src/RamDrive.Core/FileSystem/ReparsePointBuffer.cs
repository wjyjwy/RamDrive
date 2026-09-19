namespace RamDrive.Core.FileSystem;

/// <summary>
/// Helpers for raw Win32 reparse-point data buffers. The buffers are opaque to the file
/// system except for their leading tag — we store whatever the client passed to
/// <c>FSCTL_SET_REPARSE_POINT</c> and hand it back unchanged for
/// <c>FSCTL_GET_REPARSE_POINT</c> and for the WinFsp 2.x user-mode name resolver.
///
/// <para>Layouts (see &lt;winnt.h&gt;):</para>
/// <code>
/// // Microsoft-tag buffer (symlinks, junctions)
/// struct REPARSE_DATA_BUFFER {
///     ULONG ReparseTag;          // offset 0
///     USHORT ReparseDataLength;  // offset 4
///     USHORT Reserved;           // offset 6
///     union { ... } ;            // offset 8
/// }
/// // Third-party-tag buffer
/// struct REPARSE_GUID_DATA_BUFFER {
///     ULONG ReparseTag;          // offset 0
///     GUID   ReparseGuid;        // offset 4  (Data1@4, Data2@8, Data3@10, Data4[8]@12)
///     USHORT ReparseDataLength;  // offset 20
///     USHORT Reserved;           // offset 22
///     BYTE   DataBuffer[];       // offset 24 — header size
/// }
/// </code>
/// </summary>
internal static class ReparsePointBuffer
{
    /// <summary>IO_REPARSE_TAG_SYMLINK — file and directory symbolic links.</summary>
    public const uint SymlinkTag = 0xA000000C;

    /// <summary>IO_REPARSE_TAG_MOUNT_POINT — directory junctions.</summary>
    public const uint MountPointTag = 0xA0000003;

    /// <summary>sizeof(REPARSE_GUID_DATA_BUFFER_HEADER_SIZE).</summary>
    public const int GuidHeaderSize = 24;

    /// <summary>Minimum buffer we ever inspect: the leading tag DWORD.</summary>
    public const int MinimumSize = sizeof(uint);

    /// <summary>The reparse tag is the first DWORD of every reparse buffer.</summary>
    public static uint GetTag(byte[] reparseData)
        => BitConverter.ToUInt32(reparseData, 0);

    /// <summary>
    /// Microsoft tags carry the high bit (0x80000000); non-Microsoft tags carry a GUID
    /// instead. Mirrors the <c>IsReparseTagMicrosoft</c> macro from winnt.h.
    /// </summary>
    public static bool IsMicrosoftTag(uint reparseTag)
        => (reparseTag & 0x80000000u) != 0;

    /// <summary>
    /// Decide whether an existing reparse point may be replaced by a new buffer.
    /// 1:1 port of <c>FspFileSystemCanReplaceReparsePoint</c> (winfsp src/dll/fsop.c):
    /// the tags must match, and for non-Microsoft tags the identifying GUID must match.
    ///
    /// <para>Called for both <c>FSCTL_SET_REPARSE_POINT</c> (on a node that already has
    /// one) and <c>FSCTL_DELETE_REPARSE_POINT</c>. Returns an NTSTATUS:
    /// <see cref="WinFsp.Native.NtStatus.Success"/>, <c>STATUS_IO_REPARSE_DATA_INVALID</c>,
    /// <c>STATUS_IO_REPARSE_TAG_MISMATCH</c> or <c>STATUS_REPARSE_ATTRIBUTE_CONFLICT</c>.</para>
    /// </summary>
    public static int CanReplace(byte[]? current, int currentSize, byte[]? replace, int replaceSize)
    {
        if (current is null || replace is null
            || currentSize < MinimumSize || replaceSize < MinimumSize)
        {
            return WinFsp.Native.NtStatus.IoReparseDataInvalid;
        }

        uint currentTag = BitConverter.ToUInt32(current, 0);
        if (currentTag != BitConverter.ToUInt32(replace, 0))
            return WinFsp.Native.NtStatus.IoReparseTagMismatch;

        if (!IsMicrosoftTag(currentTag))
        {
            // For third-party tags the identifying GUID is part of the header: a buffer
            // too short to carry it, or any GUID field difference, blocks the replacement.
            if (currentSize < GuidHeaderSize || replaceSize < GuidHeaderSize)
                return WinFsp.Native.NtStatus.ReparseAttributeConflict;

            // REPARSE_GUID_DATA_BUFFER.ReparseGuid lives at offset 4 (a .NET Guid):
            //  Data1 = 4 bytes @4, Data2 = 2 bytes @8, Data3 = 2 bytes @10,
            //  Data4 = 8 bytes @12. Must compare the WHOLE 16 bytes.
            // 1:1 port of FspFileSystemCanReplaceReparsePoint (winfsp src/dll/fsop.c),
            // which compares Data4 as two 4-byte DWORDS — i.e. every byte of the GUID.
            if (!current.AsSpan(4, 16).SequenceEqual(replace.AsSpan(4, 16)))
            {
                return WinFsp.Native.NtStatus.ReparseAttributeConflict;
            }
        }

        return WinFsp.Native.NtStatus.Success;
    }
}
