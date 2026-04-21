using System.Text;
using DoclingDotNet.Algorithms.Spatial;
using DoclingDotNet.Models;
using DoclingDotNet.Pipeline;

namespace DoclingDotNet.Export;

public static class TextExporter
{
    private sealed record SyntheticTextTable(
        int AnchorIndex,
        HashSet<int> CoveredIndices,
        IReadOnlyList<PdfTextCellDto> Cells);

    private sealed record SyntheticTextTableCandidate(
        int ElementIndex,
        PdfTextCellDto Cell,
        BoundingBox Box);

    public static string Export(PdfConversionRunResult result)
    {
        var sb = new StringBuilder();
        
        foreach (var page in result.Pages)
        {
            if (page.Assembled?.Elements != null)
            {
                var syntheticTables = DetectSyntheticTextTables(page.Assembled.Elements);
                var syntheticTableIndices = syntheticTables.Values
                    .SelectMany(table => table.CoveredIndices)
                    .ToHashSet();

                for (int i = 0; i < page.Assembled.Elements.Count; i++)
                {
                    if (syntheticTables.TryGetValue(i, out var syntheticTable))
                    {
                        ExportTableCells(syntheticTable.Cells, sb);
                        continue;
                    }

                    if (syntheticTableIndices.Contains(i))
                    {
                        continue;
                    }

                    var element = page.Assembled.Elements[i];
                    if (element is TableElement tableElement)
                    {
                        var supplementalCells = GetDeferredTableCells(page.Assembled.Elements, i, tableElement);
                        ExportTable(tableElement, sb, supplementalCells);
                    }
                    else
                    {
                        if (ShouldDeferToUpcomingTable(page.Assembled.Elements, i))
                        {
                            continue;
                        }

                        var text = element.Text;
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            sb.AppendLine(text);
                        }
                    }
                }
            }
            else
            {
                foreach (var cell in page.TextlineCells)
                {
                    var text = cell.Text;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sb.AppendLine(text);
                    }
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void ExportTable(TableElement tableElement, StringBuilder sb, IReadOnlyList<PdfTextCellDto>? supplementalCells = null)
    {
        var cells = tableElement.Cluster?.Cells;
        if ((cells == null || cells.Count == 0) && (supplementalCells == null || supplementalCells.Count == 0)) return;

        var tableCells = new List<PdfTextCellDto>();
        if (cells != null)
        {
            tableCells.AddRange(cells);
        }

        if (supplementalCells != null)
        {
            foreach (var supplementalCell in supplementalCells)
            {
                if (tableCells.All(existing => existing.Index != supplementalCell.Index))
                {
                    tableCells.Add(supplementalCell);
                }
            }
        }

        ExportTableCells(tableCells, sb);
    }

    private static void ExportTableCells(IReadOnlyList<PdfTextCellDto> tableCells, StringBuilder sb)
    {
        if (tableCells.Count == 0)
        {
            return;
        }

        var cellBoxes = tableCells
            .Select(c => new
            {
                Cell = c,
                Box = c.Rect.ToBoundingBox(),
                Text = c.Text?.Replace("\r", string.Empty)?.Replace("\n", " ")?.Trim() ?? string.Empty
            })
            .Where(c => !string.IsNullOrWhiteSpace(c.Text))
            .OrderByDescending(c => c.Box.T)
            .ThenBy(c => c.Box.L)
            .ToList();

        if (cellBoxes.Count == 0) return;

        var rows = new List<List<(PdfTextCellDto Cell, BoundingBox Box, string Text)>>();
        foreach (var item in cellBoxes)
        {
            var centerY = (item.Box.T + item.Box.B) / 2;
            var height = Math.Max(1.0, item.Box.T - item.Box.B);

            if (rows.Count == 0)
            {
                rows.Add([(item.Cell, item.Box, item.Text)]);
                continue;
            }

            var currentRow = rows[^1];
            var rowCenterY = currentRow.Average(r => (r.Box.T + r.Box.B) / 2);
            var rowHeight = Math.Max(1.0, currentRow.Average(r => r.Box.T - r.Box.B));
            var rowTolerance = Math.Max(6.0, Math.Max(height, rowHeight) * 0.6);

            if (Math.Abs(centerY - rowCenterY) <= rowTolerance)
            {
                currentRow.Add((item.Cell, item.Box, item.Text));
            }
            else
            {
                rows.Add([(item.Cell, item.Box, item.Text)]);
            }
        }

        foreach (var row in rows)
        {
            row.Sort((left, right) => left.Box.L.CompareTo(right.Box.L));
        }

        var anchorRow = rows
            .OrderByDescending(r => r.Count)
            .ThenBy(r => rows.IndexOf(r))
            .First();
        var cols = anchorRow
            .Select(c => (c.Box.L + c.Box.R) / 2)
            .ToList();

        var matrix = new string[rows.Count, cols.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            foreach (var cellStr in rows[i])
            {
                var center = (cellStr.Box.L + cellStr.Box.R) / 2;

                int closestCol = 0;
                double minDiff = double.MaxValue;
                for (int j = 0; j < cols.Count; j++)
                {
                    var diff = Math.Abs(center - cols[j]);
                    if (diff < minDiff)
                    {
                        minDiff = diff;
                        closestCol = j;
                    }
                }

                if (!string.IsNullOrEmpty(matrix[i, closestCol]))
                {
                    matrix[i, closestCol] += " " + cellStr.Text;
                }
                else
                {
                    matrix[i, closestCol] = cellStr.Text;
                }
            }
        }

        var colWidths = new int[cols.Count];
        for (int j = 0; j < cols.Count; j++)
        {
            int max = 1;
            for (int i = 0; i < rows.Count; i++)
            {
                var textObj = matrix[i, j] ?? string.Empty;
                if (textObj.Length > max) max = textObj.Length;
            }
            colWidths[j] = max;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            sb.Append("| ");
            for (int j = 0; j < cols.Count; j++)
            {
                var text = matrix[i, j] ?? string.Empty;
                sb.Append(text.PadRight(colWidths[j]));
                sb.Append(" | ");
            }

            sb.Length -= 1;
            sb.AppendLine();

            if (i == 0)
            {
                sb.Append("|");
                for (int j = 0; j < cols.Count; j++)
                {
                    sb.Append(' ');
                    sb.Append(new string('-', colWidths[j]));
                    sb.Append(" |" );
                }
                sb.AppendLine();
            }
        }
    }

    private static Dictionary<int, SyntheticTextTable> DetectSyntheticTextTables(IReadOnlyList<BasePageElement> elements)
    {
        var candidates = elements
            .Select((element, index) => new { element, index })
            .Where(item => item.element is TextElement
                           && item.element.Cluster.Cells.Count == 1
                           && !string.IsNullOrWhiteSpace(item.element.Text)
                           && !ShouldDeferToUpcomingTable(elements, item.index))
            .Select(item =>
            {
                var cell = item.element.Cluster.Cells[0];
                return new SyntheticTextTableCandidate(item.index, cell, cell.Rect.ToBoundingBox());
            })
            .OrderByDescending(item => item.Box.T)
            .ThenBy(item => item.Box.L)
            .ToList();

        if (candidates.Count == 0)
        {
            return [];
        }

        var rows = new List<List<SyntheticTextTableCandidate>>();
        foreach (var candidate in candidates)
        {
            if (rows.Count == 0)
            {
                rows.Add([candidate]);
                continue;
            }

            var currentRow = rows[^1];
            var rowCenterY = currentRow.Average(item => (item.Box.T + item.Box.B) / 2);
            var rowHeight = Math.Max(1.0, currentRow.Average(item => item.Box.T - item.Box.B));
            var tolerance = Math.Max(10.0, Math.Max(candidate.Box.Height, rowHeight) * 0.8);
            var centerY = (candidate.Box.T + candidate.Box.B) / 2;

            if (Math.Abs(centerY - rowCenterY) <= tolerance)
            {
                currentRow.Add(candidate);
            }
            else
            {
                rows.Add([candidate]);
            }
        }

        foreach (var row in rows)
        {
            row.Sort((left, right) => left.Box.L.CompareTo(right.Box.L));
        }

        var usedIndices = new HashSet<int>();
        var tables = new Dictionary<int, SyntheticTextTable>();

        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var headerRow = rows[rowIndex]
                .Where(candidate => !usedIndices.Contains(candidate.ElementIndex))
                .ToList();
            if (headerRow.Count < 3)
            {
                continue;
            }

            var columnCenters = GetColumnCenters(headerRow.Select(candidate => candidate.Cell));
            if (columnCenters.Count < 3)
            {
                continue;
            }

            var averageHeaderHeight = Math.Max(1.0, headerRow.Average(candidate => candidate.Box.Height));
            var horizontalTolerance = Math.Max(12.0, averageHeaderHeight * 1.5);
            var verticalGapLimit = Math.Max(60.0, averageHeaderHeight * 6.0);
            var tableCandidates = new List<SyntheticTextTableCandidate>(headerRow);
            var currentBottom = headerRow.Min(candidate => candidate.Box.B);

            for (int candidateRowIndex = rowIndex + 1; candidateRowIndex < rows.Count; candidateRowIndex++)
            {
                var candidateRow = rows[candidateRowIndex]
                    .Where(candidate => !usedIndices.Contains(candidate.ElementIndex))
                    .ToList();
                if (candidateRow.Count == 0)
                {
                    continue;
                }

                var rowTop = candidateRow.Max(candidate => candidate.Box.T);
                if (currentBottom - rowTop > verticalGapLimit)
                {
                    break;
                }

                var alignedCells = candidateRow.Count(candidate =>
                {
                    var centerX = (candidate.Box.L + candidate.Box.R) / 2;
                    return columnCenters.Any(columnCenter => Math.Abs(columnCenter - centerX) <= horizontalTolerance);
                });

                if (alignedCells >= 1)
                {
                    tableCandidates.AddRange(candidateRow);
                    currentBottom = candidateRow.Min(candidate => candidate.Box.B);
                }
            }

            var dataCandidates = tableCandidates
                .Where(candidate => !headerRow.Contains(candidate))
                .ToList();
            var alignedDataCells = dataCandidates.Count(candidate =>
            {
                var centerX = (candidate.Box.L + candidate.Box.R) / 2;
                return columnCenters.Any(columnCenter => Math.Abs(columnCenter - centerX) <= horizontalTolerance);
            });
            if (alignedDataCells < 2)
            {
                continue;
            }

            var anchorIndex = tableCandidates.Min(candidate => candidate.ElementIndex);
            var coveredIndices = tableCandidates
                .Select(candidate => candidate.ElementIndex)
                .ToHashSet();
            var tableCells = tableCandidates
                .Select(candidate => candidate.Cell)
                .DistinctBy(cell => cell.Index)
                .OrderBy(cell => cell.Index)
                .ToList();

            tables[anchorIndex] = new SyntheticTextTable(anchorIndex, coveredIndices, tableCells);
            foreach (var coveredIndex in coveredIndices)
            {
                usedIndices.Add(coveredIndex);
            }
        }

        return tables;
    }

    private static List<double> GetColumnCenters(IEnumerable<PdfTextCellDto> cells)
    {
        var centers = new List<double>();

        foreach (var center in cells
            .Select(cell =>
            {
                var box = cell.Rect.ToBoundingBox();
                return (box.L + box.R) / 2;
            })
            .OrderBy(value => value))
        {
            if (centers.Count == 0 || Math.Abs(center - centers[^1]) > 12.0)
            {
                centers.Add(center);
            }
        }

        return centers;
    }

    private static bool ShouldDeferToUpcomingTable(IReadOnlyList<BasePageElement> elements, int index)
    {
        if (elements[index] is not TextElement currentTextElement)
        {
            return false;
        }

        for (int nextIndex = index + 1; nextIndex < elements.Count; nextIndex++)
        {
            if (elements[nextIndex] is TableElement nextTable)
            {
                for (int candidateIndex = index; candidateIndex < nextIndex; candidateIndex++)
                {
                    if (!IsDeferredTableCell(elements[candidateIndex], nextTable))
                    {
                        return false;
                    }
                }

                return IsDeferredTableCell(currentTextElement, nextTable);
            }

            if (elements[nextIndex] is not TextElement)
            {
                return false;
            }
        }

        return false;
    }

    private static IReadOnlyList<PdfTextCellDto> GetDeferredTableCells(IReadOnlyList<BasePageElement> elements, int tableIndex, TableElement tableElement)
    {
        var deferredCells = new List<PdfTextCellDto>();

        for (int index = tableIndex - 1; index >= 0; index--)
        {
            if (!IsDeferredTableCell(elements[index], tableElement))
            {
                break;
            }

            deferredCells.Insert(0, elements[index].Cluster.Cells[0]);
        }

        return deferredCells;
    }

    private static bool IsDeferredTableCell(BasePageElement element, TableElement tableElement)
    {
        if (element is not TextElement || element.Cluster.Cells.Count != 1 || tableElement.Cluster?.Cells == null || tableElement.Cluster.Cells.Count == 0)
        {
            return false;
        }

        var tableBox = tableElement.Cluster.Bbox;
        var cell = element.Cluster.Cells[0];
        var cellBox = cell.Rect.ToBoundingBox();
        var columnCenters = tableElement.Cluster.Cells
            .Select(existingCell =>
            {
                var existingBox = existingCell.Rect.ToBoundingBox();
                return (existingBox.L + existingBox.R) / 2;
            })
            .Distinct()
            .ToList();

        var centerX = (cellBox.L + cellBox.R) / 2;
        var horizontalTolerance = Math.Max(12.0, cellBox.Height);
        var verticalTolerance = Math.Max(18.0, cellBox.Height * 1.5);

        return cellBox.L >= tableBox.L - horizontalTolerance
               && cellBox.R <= tableBox.R + horizontalTolerance
               && cellBox.T <= tableBox.T + verticalTolerance
               && cellBox.B >= tableBox.B - verticalTolerance
               && columnCenters.Any(columnCenter => Math.Abs(columnCenter - centerX) <= horizontalTolerance);
    }
}
