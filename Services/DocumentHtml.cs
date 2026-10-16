using System.Net;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Ganss.Xss;
using HtmlToOpenXml;

namespace TimeManagement.Services;

/// <summary>
/// HTML helpers for papers written in the Tutor's Write tab and for study
/// guides: sanitising what the editor sends (it's shown back in the page,
/// so only plain document formatting may survive - no scripts, styles,
/// event handlers or odd link schemes), counting words, and turning the
/// HTML into a real Word (.docx) file.
/// </summary>
public static partial class DocumentHtml
{
    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"</(p|h1|h2|h3|li|td|th|blockquote|pre)>|<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndPattern();

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’\-][\p{L}\p{N}]+)*")]
    private static partial Regex WordPattern();

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();

        sanitizer.AllowedTags.Clear();
        foreach (var tag in new[] { "p", "br", "h1", "h2", "h3", "strong", "b", "em", "i", "u", "s",
                     "ol", "ul", "li", "a", "blockquote", "pre", "code", "table", "thead", "tbody", "tr", "td", "th" })
        {
            sanitizer.AllowedTags.Add(tag);
        }

        sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in new[] { "href", "class", "data-list", "data-row", "colspan", "rowspan" })
        {
            sanitizer.AllowedAttributes.Add(attribute);
        }

        // Only the editor's own alignment/indent classes, nothing else.
        sanitizer.AllowedClasses.Clear();
        foreach (var cssClass in new[] { "ql-align-center", "ql-align-right", "ql-align-justify" }
                     .Concat(Enumerable.Range(1, 8).Select(i => $"ql-indent-{i}")))
        {
            sanitizer.AllowedClasses.Add(cssClass);
        }

        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto" })
        {
            sanitizer.AllowedSchemes.Add(scheme);
        }

        return sanitizer;
    }

    /// <summary>Strips everything but document formatting.</summary>
    public static string Sanitize(string? html) => string.IsNullOrWhiteSpace(html) ? string.Empty : Sanitizer.Sanitize(html);

    /// <summary>The visible text, with block elements on their own lines.</summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var withBreaks = BlockEndPattern().Replace(html, "\n");
        return WebUtility.HtmlDecode(TagPattern().Replace(withBreaks, " "));
    }

    public static int CountWords(string? html) => WordPattern().Matches(ToPlainText(html)).Count;

    /// <summary>A Word document holding <paramref name="title"/> as its
    /// heading, then the (already sanitised) HTML body.</summary>
    public static async Task<byte[]> ToDocxAsync(string title, string? subtitle, string html, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream();
        using (var package = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var mainPart = package.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());

            var header = $"<h1>{WebUtility.HtmlEncode(title)}</h1>" +
                         (string.IsNullOrWhiteSpace(subtitle) ? "" : $"<p><em>{WebUtility.HtmlEncode(subtitle)}</em></p>");

            var converter = new HtmlConverter(mainPart);
            await converter.ParseBody(header + html, cancellationToken);
            mainPart.Document.Save();
        }

        return stream.ToArray();
    }

    /// <summary>A safe file name for a download: letters, digits, spaces, dashes.</summary>
    public static string FileNameFor(string title, string extension)
    {
        var cleaned = new string([.. title.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : ' ')]).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        if (cleaned.Length == 0)
        {
            cleaned = "FlightPath document";
        }
        return (cleaned.Length > 80 ? cleaned[..80] : cleaned) + extension;
    }
}
