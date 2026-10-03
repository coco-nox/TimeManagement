using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace TimeManagement.Services;

/// <summary>
/// Turns a tutor answer's plain-text formatting - blank-line-separated
/// paragraphs, "- "/"* " bullet lines, "1. " numbered lines, and "**bold**"
/// emphasis, the conventions asked for in TutorChatService's system
/// instruction - into actual HTML elements. Without this, a multi-part
/// answer collapses into one run-on block since Razor/browsers ignore plain
/// "\n" characters. Mirrored in JavaScript by renderFormattedAnswer in
/// Pages/Tutor/Index.cshtml for answers appended without a page reload; keep
/// the two in sync if the formatting conventions change.
/// </summary>
public static partial class ChatAnswerFormatter
{
    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldMarker();

    [GeneratedRegex(@"^(?:[-*]|\d+\.)\s+")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"^\d+\.\s+")]
    private static partial Regex NumberedListLine();

    /// <summary>Renders one chat message's content as safe HTML. All source
    /// text is HTML-encoded before any markup is added, so this is safe to
    /// use even though the text ultimately came from an AI response.</summary>
    public static IHtmlContent ToHtml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return HtmlString.Empty;
        }

        var html = new StringBuilder();
        var blocks = Regex.Split(text.Trim(), @"\n\s*\n");

        foreach (var block in blocks)
        {
            var lines = block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0)
            {
                continue;
            }

            var isBulleted = lines.All(l => l.StartsWith("- ", StringComparison.Ordinal) || l.StartsWith("* ", StringComparison.Ordinal));
            var isNumbered = !isBulleted && lines.All(l => NumberedListLine().IsMatch(l));

            if (isBulleted || isNumbered)
            {
                html.Append(isBulleted ? "<ul class=\"mb-2 ps-3\">" : "<ol class=\"mb-2 ps-3\">");
                foreach (var line in lines)
                {
                    html.Append("<li>").Append(FormatInline(ListMarker().Replace(line, string.Empty))).Append("</li>");
                }
                html.Append(isBulleted ? "</ul>" : "</ol>");
            }
            else
            {
                html.Append("<p class=\"mb-2\">")
                    .Append(string.Join("<br>", lines.Select(FormatInline)))
                    .Append("</p>");
            }
        }

        return new HtmlString(html.ToString());
    }

    // Encodes the line first (so raw "<"/"&" etc. from the AI can never
    // become markup), then layers **bold** support on top of the now-safe,
    // encoded text.
    private static string FormatInline(string line)
    {
        var encoded = HtmlEncoder.Default.Encode(line);
        return BoldMarker().Replace(encoded, "<strong>$1</strong>");
    }
}
