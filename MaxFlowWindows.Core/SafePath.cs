using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MaxFlowWindows.Core;

public static class SafePath
{
    private static readonly char[] InvalidPathChars = Path.GetInvalidPathChars();
    private static readonly char[] InvalidFileChars = Path.GetInvalidFileNameChars();

    public static bool IsValidFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            string full = Path.GetFullPath(path);
            return full.IndexOfAny(InvalidPathChars) < 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsUnderAllowedRoot(string? path, IEnumerable<string> allowedRoots)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            string fullPath = Path.GetFullPath(path);
            return allowedRoots.Any(root =>
            {
                string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch
        {
            return false;
        }
    }

    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "unnamed";

        string sanitized = new string(name
            .Select(ch => InvalidFileChars.Contains(ch) ? '_' : ch)
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed" : sanitized.Trim();
    }
}