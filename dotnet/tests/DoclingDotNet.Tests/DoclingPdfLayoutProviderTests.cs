using DoclingDotNet.Export;
using DoclingDotNet.Layout;
using DoclingDotNet.Pipeline;
using Xunit;

namespace DoclingDotNet.Tests;

public sealed class SkipIfModelNotDownloaded : TheoryAttribute
{
    public SkipIfModelNotDownloaded()
    {
        var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        var layoutModel = Path.Combine(assetsDir, "model.onnx");

        if (!File.Exists(layoutModel))
        {
            Skip = "Layout model not found. Please download the model and place it in the Assets directory.";
        }
    }
}


public class DoclingPdfLayoutProviderTests
{
    private readonly string _assetsDir;
    private readonly string _layoutModel;

    public DoclingPdfLayoutProviderTests()
    {
        _assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        _layoutModel = Path.Combine(_assetsDir, "model.onnx");
    }

    private static readonly string TableExpectedText = string.Join(Environment.NewLine,
    [
        "| Product Code | Description              | Quantity | Price  | SubTotal |",
        "| ------------ | ------------------------ | -------- | ------ | -------- |",
        "| PEN001       | Black Pen                | 2        | $3.00  | $6.00    |",
        "| PEN002       | Blue Pen                 | 2        | $3.00  | $6.00    |",
        "| PAPER001     | White Paper (100 Sheets) | 1        | $5.00  | $5.00    |",
        "| PRINTER001   | Printer Cartridge        | 1        | $25.00 | $25.00   |"
    ]);


    private static readonly string ListExpectedText = """
        Apples 
        Bananas 
        Carrots 
        Digestive Biscuits 
        Eggs 
        Flowers 
        Grapefruit 
        Hamburger 
        Ice Cream 
        Jacket Potato
        """;

    [SkipIfModelNotDownloaded]
    [InlineData("Table.pdf")]
    [InlineData("List.pdf")]
    public async Task ExecuteAsync_When_LayoutModelExists(string fileName)
    {
        var inputFilePath = Path.Combine(_assetsDir, fileName);

        // Skip if model hasn't been downloaded yet
        if (!File.Exists(_layoutModel)) return;

        if (!File.Exists(inputFilePath)) return;

        var onnxLayout = new OnnxLayoutProvider(_layoutModel);
        var runner = new DoclingPdfConversionRunner(layoutProviders: new[] { onnxLayout });

        var request = new PdfConversionRequest
        {
            FilePath = inputFilePath,
            EnableLayoutInference = true
        };

        var result = await runner.ExecuteAsync(request);

        var textContent = TextExporter.Export(result);

        // Assert Runner Status
        Assert.Equal(PipelineRunStatus.Succeeded, result.Pipeline.Status);
        Assert.DoesNotContain(result.Diagnostics, d => !d.Recoverable);
        Assert.True(result.LayoutInferenceApplied);
        Assert.True(result.LayoutPostprocessingApplied);

        if (fileName.Equals("Table.pdf", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(TableExpectedText, textContent);
        }
        else if (fileName.Equals("List.pdf", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(GetNormalizedLines(ListExpectedText), GetNormalizedLines(textContent));
        }
    }

    private static string[] GetNormalizedLines(string text)
    {
        return text
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
    }
}
