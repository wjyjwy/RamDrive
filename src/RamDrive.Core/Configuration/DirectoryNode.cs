using System.Buffers;

namespace RamDrive.Core.Configuration;

/// <summary>
/// Represents a node in the initial directory tree configuration.
/// Each key is a subdirectory name; each value is its subtree.
/// An empty node (<c>{}</c>) is a leaf directory with no children.
/// </summary>
public sealed class DirectoryNode : Dictionary<string, DirectoryNode>
{
    private const int MaxDepth = 32;

    private static readonly SearchValues<char> InvalidNameChars = SearchValues.Create("<>:\"/\\|?*");

    public DirectoryNode() : base(StringComparer.OrdinalIgnoreCase) { }

    /// <summary>
    /// Validates the directory tree. Returns a list of human-readable error strings (empty = valid).
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        ValidateRecursive(this, "", errors, 0);
        return errors;
    }

    private static void ValidateRecursive(DirectoryNode entries, string parentPath, List<string> errors, int depth)
    {
        if (depth >= MaxDepth)
        {
            errors.Add($"  - \"{parentPath}\": directory nesting exceeds {MaxDepth} levels");
            return;
        }

        foreach (var (name, children) in entries)
        {
            var displayPath = string.IsNullOrEmpty(parentPath) ? name : parentPath + @"\" + name;
            bool nameInvalid = false;

            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"  - Empty directory name under \"{parentPath}\"");
                continue; // no meaningful children to check under a blank name
            }

            if (name.AsSpan().IndexOfAny(InvalidNameChars) >= 0)
            {
                errors.Add($"  - \"{displayPath}\": contains invalid character(s). Avoid < > : \" / \\ | ? *");
                nameInvalid = true;
            }

            if (!nameInvalid && IsReservedName(name))
            {
                errors.Add($"  - \"{displayPath}\": is a Windows reserved name (CON, PRN, NUL, etc.)");
                nameInvalid = true;
            }

            if (!nameInvalid && name.Length > 255)
            {
                errors.Add($"  - \"{displayPath}\": name exceeds 255 characters");
            }

            bool hasControlChar = false;
            foreach (char c in name)
            {
                if (c < ' ' || c == '\u007F')
                {
                    hasControlChar = true;
                    break;
                }
            }

            if (!nameInvalid && hasControlChar)
            {
                errors.Add($"  - \"{displayPath}\": contains control character(s)");
            }

            if (!nameInvalid && (name.EndsWith('.') || name.EndsWith(' ')))
            {
                errors.Add($"  - \"{displayPath}\": ends with '.' or ' ' (invalid on Windows)");
            }

            // Always validate children so the user sees all errors at once
            if (children.Count > 0)
                ValidateRecursive(children, displayPath, errors, depth + 1);
        }
    }

    private static bool IsReservedName(string name)
    {
        // MUST match WindowsNameRules.IsReservedBaseName: a reserved device name is
        // reserved with ANY extension, so "CON.txt" must be rejected too. Previously
        // this only trimmed trailing '.'/' ' and never cut at the first '.', so a config
        // entry like "CON.txt" passed config-time validation and was then silently
        // refused by RamFileSystem.CreateDirectory (WindowsNameRules) — a directory the
        // user asked for simply never appeared. Keep both rule sets identical.
        var upper = name.TrimEnd('.', ' ').ToUpperInvariant();
        int dot = upper.IndexOf('.');
        if (dot >= 0) upper = upper[..dot];
        return upper is "CON" or "PRN" or "AUX" or "NUL"
            or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
            or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9";
    }
}
