namespace RamDrive.Core.FileSystem;

/// <summary>
/// NTFS-style component-name validation. Centralized for RamFileSystem.CreateFile /
/// CreateDirectory / Move. Duplicated checks also live in
/// <see cref="RamDrive.Core.Configuration.DirectoryNode.Validate"/> for config-time
/// reporting (keep the rule sets in sync).
/// </summary>
internal static class WindowsNameRules
{
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name is "." or "..") return false;
        if (name.Length > 255) return false;

        foreach (char c in name)
        {
            if (c < ' ' || c == '\u007F') return false;                 // control characters
            if (c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*') return false;
        }

        if (name.EndsWith('.') || name.EndsWith(' ')) return false;

        return !IsReservedBaseName(name);
    }

    /// <summary>Windows device names (CON, PRN, NUL, COM1-9, LPT1-9) — reserved with any extension.</summary>
    private static bool IsReservedBaseName(string name)
    {
        var trimmed = name.TrimEnd('.', ' ').ToUpperInvariant();
        int dot = trimmed.IndexOf('.');
        if (dot >= 0) trimmed = trimmed[..dot];
        return trimmed is "CON" or "PRN" or "AUX" or "NUL"
            or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
            or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9";
    }
}