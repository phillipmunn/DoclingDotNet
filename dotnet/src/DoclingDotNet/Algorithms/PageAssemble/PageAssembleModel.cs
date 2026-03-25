using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DoclingDotNet.Algorithms.Spatial;
using DoclingDotNet.Models;
using DoclingDotNet.Algorithms.Layout;

namespace DoclingDotNet.Algorithms.PageAssemble;

public sealed partial class PageAssembleModel
{
    private static readonly Dictionary<string, string> LigatureMap = new()
    {
        { "\ufb00", "ff" },
        { "\ufb01", "fi" },
        { "\ufb02", "fl" },
        { "\ufb03", "ffi" },
        { "\ufb04", "ffl" },
        { "\ufb05", "st" },
        { "\ufb06", "st" }
    };

    [GeneratedRegex(@"([\ufb00-\ufb06])( (?=\w))?")]
    private static partial Regex LigatureRegex();

    [GeneratedRegex(@"\b[\w]+\b")]
    private static partial Regex WordRegex();

    private static readonly HashSet<string> TextElemLabels = [ "text", "title", "caption", "list_item", "page_header", "page_footer", "footnote", "section_header", "paragraph" ];
    private static readonly HashSet<string> PageHeaderLabels = [ "page_header" ];
    private static readonly HashSet<string> TableLabels = [ "table" ];
    private static readonly HashSet<string> FigureLabels = [ "picture", "figure" ];
    private static readonly HashSet<string> FormulaLabels = [ "formula" ];
    private static readonly HashSet<string> ContainerLabels = [ "form", "key_value_region" ];

    public string SanitizeText(List<string> lines, bool preserveLineBreaks = false)
    {
        if (lines.Count == 0) return string.Empty;
        var copy = new List<string>(lines);

        if (!preserveLineBreaks)
        {
            for (int i = 1; i < copy.Count; i++)
            {
                var prevLine = copy[i - 1];
                var line = copy[i];

                if (prevLine.EndsWith("-"))
                {
                    var prevWords = WordRegex().Matches(prevLine);
                    var lineWords = WordRegex().Matches(line);

                    if (prevWords.Count > 0 && lineWords.Count > 0 &&
                        char.IsLetterOrDigit(prevWords[^1].Value.Last()) &&
                        char.IsLetterOrDigit(lineWords[0].Value.First()))
                    {
                        copy[i - 1] = prevLine[..^1];
                    }
                }
                else
                {
                    copy[i - 1] += " ";
                }
            }
        }

        var sanitizedText = preserveLineBreaks
            ? string.Join(Environment.NewLine, copy)
            : string.Join("", copy);
        sanitizedText = sanitizedText.Replace("⁄", "/");
        sanitizedText = sanitizedText.Replace("’", "'");
        sanitizedText = sanitizedText.Replace("‘", "'");
        sanitizedText = sanitizedText.Replace("“", "\"");
        sanitizedText = sanitizedText.Replace("”", "\"");
        sanitizedText = sanitizedText.Replace("•", "·");

        sanitizedText = LigatureRegex().Replace(sanitizedText, m => LigatureMap[m.Groups[1].Value]);

        return sanitizedText.Trim();
    }

    public void ParsePage(SegmentedPdfPageDto page, int pageNo)
    {
        if (page.Predictions.Layout?.Clusters == null) return;

        var elements = new List<BasePageElement>();
        var headers = new List<BasePageElement>();
        var body = new List<BasePageElement>();

        foreach (var cluster in page.Predictions.Layout.Clusters)
        {
            BasePageElement? element = null;

            if (TextElemLabels.Contains(cluster.Label) || FormulaLabels.Contains(cluster.Label))
            {
                var textLines = GetTextLines(cluster);

                var textEl = new TextElement
                {
                    Label = cluster.Label,
                    Id = cluster.Id,
                    Text = SanitizeText(textLines, ShouldPreserveLineBreaks(cluster, textLines)),
                    PageNo = pageNo,
                    Cluster = cluster
                };

                element = textEl;
                elements.Add(textEl);

                if (PageHeaderLabels.Contains(cluster.Label))
                {
                    headers.Add(textEl);
                }
                else
                {
                    body.Add(textEl);
                }
            }
            else if (TableLabels.Contains(cluster.Label))
            {
                var tableEl = new TableElement
                {
                    Label = cluster.Label,
                    Id = cluster.Id,
                    PageNo = pageNo,
                    Cluster = cluster
                };
                element = tableEl;
                elements.Add(tableEl);
                body.Add(tableEl);
            }
            else if (FigureLabels.Contains(cluster.Label))
            {
                var figEl = new FigureElement
                {
                    Label = cluster.Label,
                    Id = cluster.Id,
                    Text = string.Empty,
                    PageNo = pageNo,
                    Cluster = cluster
                };
                element = figEl;
                elements.Add(figEl);
                body.Add(figEl);
            }
            else if (ContainerLabels.Contains(cluster.Label))
            {
                var containerEl = new ContainerElement
                {
                    Label = cluster.Label,
                    Id = cluster.Id,
                    PageNo = pageNo,
                    Cluster = cluster
                };
                element = containerEl;
                elements.Add(containerEl);
                body.Add(containerEl);
            }
        }

        page.Assembled = new AssembledUnit
        {
            Elements = elements,
            Headers = headers,
            Body = body
        };
    }

    private static List<string> GetTextLines(LayoutCluster cluster)
    {
        if (cluster.Cells.Count == 0)
        {
            return [];
        }

        return GroupCellsIntoRows(cluster.Cells)
            .Select(row => string.Join(" ", row
                .Select(cell => cell.Text.Replace("\x02", "-").Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text))))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();
    }

    private static bool ShouldPreserveLineBreaks(LayoutCluster cluster, IReadOnlyList<string> textLines)
    {
        if (textLines.Count <= 1)
        {
            return false;
        }

        if (cluster.Label == "list_item")
        {
            return true;
        }

        var rows = GroupCellsIntoRows(cluster.Cells);
        if (rows.Count < 3 || rows.Any(row => row.Count != 1) || cluster.Bbox.Width <= 0)
        {
            return false;
        }

        var averageRowWidth = rows.Average(row =>
        {
            var left = row.Min(cell => cell.Rect.ToBoundingBox().L);
            var right = row.Max(cell => cell.Rect.ToBoundingBox().R);
            return right - left;
        });

        return averageRowWidth / cluster.Bbox.Width <= 0.8;
    }

    private static List<List<PdfTextCellDto>> GroupCellsIntoRows(List<PdfTextCellDto> cells)
    {
        var rows = new List<List<PdfTextCellDto>>();

        foreach (var cell in cells
            .OrderByDescending(item => item.Rect.ToBoundingBox().T)
            .ThenBy(item => item.Rect.ToBoundingBox().L))
        {
            var box = cell.Rect.ToBoundingBox();
            var centerY = (box.T + box.B) / 2;
            var height = Math.Max(1.0, box.Height);

            if (rows.Count == 0)
            {
                rows.Add([cell]);
                continue;
            }

            var currentRow = rows[^1];
            var rowCenterY = currentRow.Average(item =>
            {
                var rowBox = item.Rect.ToBoundingBox();
                return (rowBox.T + rowBox.B) / 2;
            });
            var rowHeight = Math.Max(1.0, currentRow.Average(item => Math.Max(1.0, item.Rect.ToBoundingBox().Height)));
            var tolerance = Math.Max(6.0, Math.Max(height, rowHeight) * 0.6);

            if (Math.Abs(centerY - rowCenterY) <= tolerance)
            {
                currentRow.Add(cell);
            }
            else
            {
                rows.Add([cell]);
            }
        }

        foreach (var row in rows)
        {
            row.Sort((left, right) => left.Rect.ToBoundingBox().L.CompareTo(right.Rect.ToBoundingBox().L));
        }

        return rows;
    }
}
