// Comparators — compare IFileSystem result types between the production adapter
// and the reference filesystem. On divergence, throw DifferentialMismatchException
// (caller wraps with method name + path).

using WinFsp.Native;

namespace RamDrive.Diagnostics.DifferentialChecker;

public sealed class DifferentialMismatchException : Exception
{
    public DifferentialMismatchException(string message) : base(message) { }

    /// <summary>
    /// Every divergence observed in this process, in order. The exception is thrown from
    /// inside a WinFsp callback, where the only thing the kernel can do is turn it into
    /// STATUS_UNEXPECTED_IO_ERROR — so a test that merely completes "normally" would stay
    /// green even though the two file systems disagreed. Recording here lets a test assert
    /// "no divergence occurred" explicitly (see <see cref="AssertNone"/>).
    ///
    /// <para><b>Only meaningful for single-threaded workloads.</b> The differential adapter
    /// calls the two file systems SEQUENTIALLY, so in a concurrency stress test (see
    /// TortureTests) another thread can mutate the file between the two calls and the
    /// comparison reports a divergence that is an artefact of the harness, not a real
    /// behavioural difference. Callers that run concurrent workloads must not assert on
    /// this; use <see cref="Clear"/> before a deterministic section instead.</para>
    /// </summary>
    private static readonly List<string> _observed = [];

    private static readonly Lock _lock = new();

    public static IReadOnlyList<string> Observed
    {
        get { lock (_lock) return _observed.ToArray(); }
    }

    public static void Record(string message)
    {
        lock (_lock) _observed.Add(message);
    }

    public static void Clear()
    {
        lock (_lock) _observed.Clear();
    }

    /// <summary>Throws if any divergence was recorded since the last <see cref="Clear"/>.</summary>
    public static void AssertNone(string? context = null)
    {
        var all = Observed;
        if (all.Count == 0) return;
        throw new DifferentialMismatchException(
            (context is null ? "" : context + ": ") +
            $"{all.Count} differential divergence(s) recorded:{Environment.NewLine}" +
            string.Join(Environment.NewLine, all.Take(20)) +
            (all.Count > 20 ? $"{Environment.NewLine}... and {all.Count - 20} more" : ""));
    }
}

internal static class Comparators
{
    // Compare two FspFileInfo. Skip fields that legitimately differ between the
    // production adapter and MemfsReferenceFs:
    //   - timestamps: each adapter calls its own GetSystemTime() and they will differ
    //     by µs.
    //   - AllocationSize: RamDrive uses sparse allocation (pages only on write), memfs
    //     pre-allocates a byte[] for the whole logical size. Both are valid AllocationSize
    //     reports per WinFsp semantics; cache coherency only depends on FileSize.
    //   - IndexNumber: per-instance index counter, never matches.
    //   - HardLinks / EaSize: not modelled.
    public static void CompareFileInfo(string method, string? path, in FspFileInfo a, in FspFileInfo b)
    {
        if (a.FileAttributes != b.FileAttributes)
            throw New(method, path, $"FileAttributes ram=0x{a.FileAttributes:X} ref=0x{b.FileAttributes:X}");
        if (a.FileSize != b.FileSize)
            throw New(method, path, $"FileSize ram={a.FileSize} ref={b.FileSize}");
        // Reparse points are modelled by both sides: the tag (symlink / mount point / other)
        // reported in FileInfo MUST match, independent of the opaque blob compared separately.
        if (a.ReparseTag != b.ReparseTag)
            throw New(method, path, $"ReparseTag ram=0x{a.ReparseTag:X8} ref=0x{b.ReparseTag:X8}");
    }

    /// <summary>Compare the opaque reparse blob returned by Get(ReparsePoint)[ByName].</summary>
    public static void CompareReparseData(string method, string? path, byte[]? a, byte[]? b)
    {
        if (a is null != (b is null) || (a is not null && !a.SequenceEqual(b!)))
            throw New(method, path, $"ReparseData ram={a?.Length ?? -1} bytes ref={b?.Length ?? -1} bytes (content differs)");
    }

    public static void CompareStatus(string method, string? path, int a, int b)
    {
        if (a != b)
            throw New(method, path, $"NTSTATUS ram=0x{a:X8} ref=0x{b:X8}");
    }

    public static void CompareFsResult(string method, string? path, in FsResult a, in FsResult b)
    {
        CompareStatus(method, path, a.Status, b.Status);
        if (a.Status >= 0)
            CompareFileInfo(method, path, a.FileInfo, b.FileInfo);
    }

    public static void CompareCreateResult(string method, string? path, in CreateResult a, in CreateResult b)
    {
        CompareStatus(method, path, a.Status, b.Status);
        if (a.Status >= 0)
            CompareFileInfo(method, path, a.FileInfo, b.FileInfo);
    }

    public static void CompareReadResult(string method, string? path, in ReadResult a, in ReadResult b)
    {
        CompareStatus(method, path, a.Status, b.Status);
        if (a.Status >= 0 && a.BytesTransferred != b.BytesTransferred)
            throw New(method, path, $"BytesTransferred ram={a.BytesTransferred} ref={b.BytesTransferred}");
    }

    public static void CompareWriteResult(string method, string? path, in WriteResult a, in WriteResult b)
    {
        CompareStatus(method, path, a.Status, b.Status);
        if (a.Status >= 0)
        {
            if (a.BytesTransferred != b.BytesTransferred)
                throw New(method, path, $"BytesTransferred ram={a.BytesTransferred} ref={b.BytesTransferred}");
            CompareFileInfo(method, path, a.FileInfo, b.FileInfo);
        }
    }

    public static void CompareReadDirectoryResult(string method, string? path, in ReadDirectoryResult a, in ReadDirectoryResult b)
    {
        CompareStatus(method, path, a.Status, b.Status);
        // BytesTransferred for directory listings is buffer-format dependent (per-entry sizes
        // include FileInfo timestamps that differ between adapters). Comparing the count of
        // entries written is more meaningful but not directly available — skip the byte count.
    }

    public static void CompareVolumeInfo(string method, ulong totalA, ulong totalB, ulong freeA, ulong freeB, string labelA, string labelB)
    {
        if (totalA != totalB)
            throw New(method, null, $"totalSize ram={totalA} ref={totalB}");
        // freeSize legitimately diverges (memfs counts file bytes; RamDrive counts pages).
        // Skip free comparison.
        // Volume label legitimately differs.
    }

    private static DifferentialMismatchException New(string method, string? path, string detail)
        => new($"DIFF in {method}({path ?? "<null>"}): {detail}");
}
