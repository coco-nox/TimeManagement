using System.Text;
using DocumentFormat.OpenXml.Packaging;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;

namespace TimeManagement.Services;

/// <summary>
/// Pulls the plain text out of an uploaded PDF or Word document so it can
/// be saved alongside the file. Nothing reads this text yet - it's
/// groundwork for a future AI tutor feature that will need it.
/// </summary>
public static class TextExtractionService
{
    /// <summary>
    /// Tries to extract the text content of a document that has already
    /// been saved to <paramref name="filePath"/>. Returns null if the file
    /// type isn't one we know how to read, or if extraction fails for any
    /// reason - callers should treat that as "no text available" rather
    /// than an error, so a corrupt or oddly-formatted file never blocks
    /// the upload itself.
    /// </summary>
    public static string? TryExtractText(string filePath, string originalFileName)
    {
        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();

        try
        {
            return extension switch
            {
                ".pdf" => ExtractPdfText(filePath),
                ".docx" => ExtractDocxText(filePath),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractPdfText(string filePath)
    {
        using var pdfDocument = new PdfDocument(new PdfReader(filePath));

        var text = new StringBuilder();
        for (var pageNumber = 1; pageNumber <= pdfDocument.GetNumberOfPages(); pageNumber++)
        {
            var page = pdfDocument.GetPage(pageNumber);
            text.AppendLine(PdfTextExtractor.GetTextFromPage(page));
        }

        return text.ToString();
    }

    private static string ExtractDocxText(string filePath)
    {
        using var wordDocument = WordprocessingDocument.Open(filePath, isEditable: false);

        var mainPart = wordDocument.MainDocumentPart;
        if (mainPart == null)
        {
            return string.Empty;
        }

        return mainPart.Document?.Body?.InnerText ?? string.Empty;
    }
}
