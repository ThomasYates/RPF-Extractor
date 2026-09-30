using System.Text;

namespace RpfToFiveM.Core.Rpf;

/// <summary>Turns names from untrusted archives into safe Windows path segments.</summary>
public static class SafePath
{
    private static readonly HashSet<char> Invalid = new(Path.GetInvalidFileNameChars());

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Segment(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(Invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c);
        var s = sb.ToString().TrimEnd('.', ' ').Trim();

        if (s.Length == 0 || s == "." || s == "..") return "_";
        if (Reserved.Contains(Path.GetFileNameWithoutExtension(s))) s = "_" + s;
        return s;
    }

    /// <summary>Sanitises every segment of a '/' or '\' separated relative path.</summary>
    public static string Relative(string path) =>
        string.Join(Path.DirectorySeparatorChar,
            path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Select(Segment));
}
