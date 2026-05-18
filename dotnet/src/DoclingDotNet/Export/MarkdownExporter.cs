using System.Text;
using System.Linq;
using DoclingDotNet.Models;
using DoclingDotNet.Pipeline;

namespace DoclingDotNet.Export;

public static class MarkdownExporter
{
    private static readonly HashSet<string> HeadingLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "section_header",
        "title"
    };

    public static string Export(PdfConversionRunResult result)
    {
        // Pre-size to avoid StringBuilder internal resizing across thousands of elements
        var estimatedCapacity = result.Pages.Sum(p =>
            p.Assembled?.Elements.Count ?? p.TextlineCells.Count) * 80;
        var sb = new StringBuilder(Math.Max(256, estimatedCapacity));

        foreach (var page in result.Pages)
        {
            if (page.Assembled?.Elements is { Count: > 0 } elements)
            {
                // Use layout-model labels for accurate heading detection on assembled pages
                foreach (var element in elements)
                {
                    var text = element.Text;
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    if (HeadingLabels.Contains(element.Label))
                    {
                        sb.AppendLine($"# {text}");
                    }
                    else
                    {
                        sb.AppendLine(text);
                    }
                    sb.AppendLine();
                }
            }
            else
            {
                // Fallback for non-PDF backends that set FontName on raw text cells
                foreach (var cell in page.TextlineCells)
                {
                    var text = cell.Text;
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    if (cell.FontName.Contains("Heading", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"# {text}");
                    }
                    else
                    {
                        sb.AppendLine(text);
                    }
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString().TrimEnd();
    }
}
