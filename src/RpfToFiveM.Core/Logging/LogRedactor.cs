using System.Text;
using System.Text.RegularExpressions;

namespace RpfToFiveM.Core.Logging;

/// <summary>
/// Strips private information from log text so logs can be shared: known folders become
/// labels such as &lt;source&gt;, any other absolute path becomes &lt;path&gt; (keeping only
/// the file name), and the Windows user name becomes &lt;user&gt;. Paths inside archives
/// (e.g. "dlc.rpf/x64/...") are relative and kept, since they're what makes errors traceable.
/// </summary>
public sealed partial class LogRedactor
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "with", "for", "to", "and", "from", "in", "at", "is", "was", "not", "could", "because", "on", "by",
    };

    private readonly List<(Regex Pattern, string Label)> _known = new();
    private readonly Regex? _userName;

    /// <param name="knownFolders">Folder to label pairs; longer folders are matched first.</param>
    public LogRedactor(IEnumerable<(string Path, string Label)> knownFolders, string? userName)
    {
        foreach (var (path, label) in knownFolders
                     .Where(k => !string.IsNullOrWhiteSpace(k.Path))
                     .Select(k => (Path: k.Path.TrimEnd('\\', '/'), k.Label))
                     .Where(k => k.Path.Length >= 3)
                     .OrderByDescending(k => k.Path.Length))
        {
            // Any slash direction, any case, and only whole folder names ("bob" must not match "bobby").
            var pattern = string.Join(@"[\\/]", path.Split('\\', '/').Select(Regex.Escape));
            _known.Add((new Regex(pattern + @"(?=[\\/]|$|[^\w\-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), label));
        }

        // Very short names would mangle ordinary words.
        if (!string.IsNullOrWhiteSpace(userName) && userName.Trim().Length >= 3)
            _userName = new Regex(@"\b" + Regex.Escape(userName.Trim()) + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>Builds a redactor for this machine: the user's profile, plus the given folders.</summary>
    public static LogRedactor ForCurrentUser(IEnumerable<(string Path, string Label)> folders)
    {
        var all = folders.ToList();
        all.Add((Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<home>"));
        return new LogRedactor(all, Environment.UserName);
    }

    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var (pattern, label) in _known) text = pattern.Replace(text, label);
        text = RedactUnknownPaths(text);
        if (_userName is not null) text = _userName.Replace(text, "<user>");
        return text;
    }

    /// <summary>Replaces any remaining drive or network path with &lt;path&gt;, keeping the file name.</summary>
    private static string RedactUnknownPaths(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        foreach (Match m in PathStart().Matches(text))
        {
            if (m.Index < i) continue;
            int end = PathEnd(text, m.Index + m.Length);
            var path = text[m.Index..end].TrimEnd('.', ' ', ',', ';');
            end = m.Index + path.Length;

            sb.Append(text, i, m.Index - i);
            var last = path.Split('\\', '/').LastOrDefault(s => s.Length > 0) ?? "";
            sb.Append(last.Contains('.') && path.Length > m.Length ? @"<path>\" + last : "<path>");
            i = end;
        }
        sb.Append(text, i, text.Length - i);
        return sb.ToString();
    }

    /// <summary>
    /// Paths can contain spaces, so a path ends at a quote/bracket/newline, at ", " or ". ",
    /// or where a space is followed by an ordinary sentence word ("... is denied").
    /// </summary>
    private static int PathEnd(string text, int from)
    {
        for (int j = from; j < text.Length; j++)
        {
            char c = text[j];
            if (c is '\'' or '"' or '<' or '>' or '|' or '\r' or '\n' or '(' or ')') return j;
            bool nextIsSpaceOrEnd = j + 1 >= text.Length || char.IsWhiteSpace(text[j + 1]);
            if (c is ',' or ';' or '.' && nextIsSpaceOrEnd) return j;
            if (c == ' ')
            {
                int wordEnd = j + 1;
                while (wordEnd < text.Length && char.IsLetter(text[wordEnd])) wordEnd++;
                if (StopWords.Contains(text[(j + 1)..wordEnd])) return j;
            }
        }
        return text.Length;
    }

    [GeneratedRegex(@"(?<![\w<])(?:[A-Za-z]:[\\/]|\\\\[^\\/\s]+[\\/])")]
    private static partial Regex PathStart();
}
