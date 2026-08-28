using System.Text.RegularExpressions;

namespace IsaacProfileManager.Core.Services;

/// <summary>
/// Turns Steam's Workshop markup into something readable.
///
/// Authors write descriptions in BBCode, and a lot of Isaac mods paste the
/// whole Workshop page into <c>metadata.xml</c>. Shown raw it is a wall of
/// <c>[h2]</c>, <c>[b]</c> and <c>[url=...]</c>, which is what the library list
/// was displaying.
///
/// This strips rather than renders. The description is shown in a plain
/// TextBlock next to a mod name; it needs to read as a sentence, not become a
/// formatting engine.
/// </summary>
public static class BbCode
{
    // Only tags Steam actually defines. Matching any bracketed word would eat
    // ordinary prose — "[probably]" in a sentence, or a "[BETA]" in a title —
    // and silently changing an author's text is worse than leaving a stray tag.
    private const string Names =
        "b|i|u|s|strike|spoiler|noparse|code|h1|h2|h3|url|img|previewyoutube|" +
        "list|olist|quote|table|tr|th|td|hr|center|left|right";

    // [tag], [/tag] and [tag=value]. Deliberately not matched across newlines:
    // an unclosed bracket in prose must not swallow the rest of the text.
    private static readonly Regex Tag = new($@"\[/?(?:{Names})(?:=[^\]\n]*)?\]",
                                            RegexOptions.Compiled | RegexOptions.IgnoreCase |
                                            RegexOptions.CultureInvariant);

    /// <summary>Tags whose contents are a URL rather than text worth keeping.</summary>
    private static readonly Regex Embedded = new(@"\[(img|previewyoutube)(?:=[^\]\n]*)?\].*?\[/\1\]",
                                                 RegexOptions.Compiled | RegexOptions.IgnoreCase |
                                                 RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex BlankRuns = new(@"(\r?\n[ \t]*){3,}",
                                                  RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Readable text, or the original when there is nothing to strip.</summary>
    public static string Strip(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Content-bearing tags go first, with their contents. [img]…[/img] wraps
        // a URL, not a caption, so dropping only the tags would leave the raw
        // link sitting in the prose — unlike [url=…]label[/url], where the label
        // is exactly what should survive.
        var stripped = Embedded.Replace(text, string.Empty);
        stripped = Tag.Replace(stripped, string.Empty);

        // A [url=...]link[/url] leaves its label behind, which is what you want;
        // a bare image tag leaves nothing, and those often sat on their own line.
        stripped = BlankRuns.Replace(stripped, Environment.NewLine + Environment.NewLine);

        return stripped.Trim();
    }

    /// <summary>
    /// A one-line summary for a list row: the first sentence or line, shortened.
    /// </summary>
    public static string Summarise(string? text, int maxLength = 200)
    {
        var stripped = Strip(text);
        if (stripped.Length == 0) return string.Empty;

        var firstLine = stripped.Split('\n')
                                .Select(line => line.Trim())
                                .FirstOrDefault(line => line.Length > 0) ?? string.Empty;

        return firstLine.Length <= maxLength ? firstLine : firstLine[..maxLength].TrimEnd() + "...";
    }
}
