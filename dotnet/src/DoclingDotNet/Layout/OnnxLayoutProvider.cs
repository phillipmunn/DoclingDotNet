using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DoclingDotNet.Algorithms.Layout;
using DoclingDotNet.Algorithms.Spatial;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace DoclingDotNet.Layout;

public sealed class OnnxLayoutProvider : IDoclingLayoutProvider, IDisposable
{
    private readonly string _modelPath;
    private InferenceSession? _session;
    private bool _initialized;
    private readonly object _lock = new();

    private static readonly string[] Labels = [
        "caption", "footnote", "formula", "list_item", "page_footer", "page_header",
        "picture", "section_header", "table", "text", "title", "document_index",
        "code", "checkbox_selected", "checkbox_unselected", "form", "key_value_region"
    ];

    public OnnxLayoutProvider(string modelPath)
    {
        _modelPath = modelPath;
    }

    public string Name => "onnx_rtdetr";
    public int Priority => 10;
    public LayoutProviderCapabilities Capabilities { get; } = new() { IsExternal = false, IsTrusted = true };

    public bool IsAvailable()
    {
        return File.Exists(_modelPath);
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;
            if (!File.Exists(_modelPath))
            {
                throw new InvalidOperationException($"ONNX model not found at '{_modelPath}'.");
            }
            
            PdfiumNative.FPDF_InitLibrary();
            var sessionOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2)
            };
            _session = new InferenceSession(_modelPath, sessionOptions);
            _initialized = true;
        }
    }

    public async Task<LayoutProcessResult> ProcessAsync(LayoutProcessRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            EnsureInitialized();
        }
        catch (Exception ex)
        {
            return LayoutProcessResult.FatalFailure("ModelLoadError", ex.Message);
        }

        var results = new Dictionary<int, IReadOnlyList<LayoutCluster>>();
        
        // Use a background task to keep the caller async while doing heavy FFI/Math
        var runTask = Task.Run(() =>
        {
            var doc = IntPtr.Zero;
            GCHandle? pdfBytesHandle = null;
            try
            {
                if (request.PdfBytes != null)
                {
                    pdfBytesHandle = GCHandle.Alloc(request.PdfBytes, GCHandleType.Pinned);
                    doc = PdfiumNative.FPDF_LoadMemDocument(
                        pdfBytesHandle.Value.AddrOfPinnedObject(),
                        request.PdfBytes.Length,
                        null);
                }
                else if (!string.IsNullOrEmpty(request.FilePath))
                {
                    doc = PdfiumNative.FPDF_LoadDocument(request.FilePath, null);
                }
                else
                {
                    throw new InvalidOperationException(
                        "LayoutProcessRequest must have either FilePath or PdfBytes set.");
                }

                if (doc == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to load PDF document into Pdfium.");
                }

                for (int i = 0; i < request.Pages.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var pageDto = request.Pages[i];
                    var angle = (int)pageDto.Dimension.Angle;
                    
                    // Note: Docling normally loads page index derived from DTO, but here we assume sequential
                    var pageIndex = i; 
                    var page = PdfiumNative.FPDF_LoadPage(doc, pageIndex);
                    if (page == IntPtr.Zero) continue;

                    try
                    {
                        var width = PdfiumNative.FPDF_GetPageWidth(page);
                        var height = PdfiumNative.FPDF_GetPageHeight(page);

                        // Rendering at native resolution (e.g. 2480×3508 for A4@300dpi) is wasteful
                        // because the image is immediately resized to 640×640 for inference.
                        // Cap the raster size to 960px on the longest side to cut rendering work
                        // significantly while still providing enough detail for the model.
                        const int maxRenderPx = 960;
                        float scale = Math.Min(1f, maxRenderPx / (float)Math.Max(width, height));
                        int pxWidth = Math.Max(1, (int)Math.Ceiling(width * scale));
                        int pxHeight = Math.Max(1, (int)Math.Ceiling(height * scale));

                        using var bitmap = new SKBitmap(pxWidth, pxHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                        var fpdfBitmap = PdfiumNative.FPDFBitmap_CreateEx(pxWidth, pxHeight, 4, bitmap.GetPixels(), bitmap.RowBytes);
                        
                        // Fill white background
                        PdfiumNative.FPDFBitmap_FillRect(fpdfBitmap, 0, 0, pxWidth, pxHeight, 0xFFFFFFFF);
                        
                        // Render
                        PdfiumNative.FPDF_RenderPageBitmap(fpdfBitmap, page, 0, 0, pxWidth, pxHeight, 0, 0); // FPDF_ANNOT = 1, but 0 is standard
                        PdfiumNative.FPDFBitmap_Destroy(fpdfBitmap);

                        // RT-DETR Preprocessing
                        using var resized = bitmap.Resize(new SKImageInfo(640, 640, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium);
                        if (resized == null) throw new InvalidOperationException("Failed to resize page bitmap.");

                        var tensor = CreateNormalizedTensor(resized);

                        var targetSizesTensor = new DenseTensor<long>(new[] { 1, 2 });
                        targetSizesTensor[0, 0] = (long)Math.Ceiling(height); // RT-DETR expects [height, width]
                        targetSizesTensor[0, 1] = (long)Math.Ceiling(width);

                        var inputs = new List<NamedOnnxValue>
                        {
                            NamedOnnxValue.CreateFromTensor("images", tensor),
                            NamedOnnxValue.CreateFromTensor("orig_target_sizes", targetSizesTensor)
                        };

                        using var runResult = _session!.Run(inputs);

                        var labelsTensor = runResult.First(v => v.Name == "labels").AsTensor<long>();
                        var boxesTensor = runResult.First(v => v.Name == "boxes").AsTensor<float>();
                        var scoresTensor = runResult.First(v => v.Name == "scores").AsTensor<float>();

                        var clusters = new List<LayoutCluster>();
                        int numBoxes = boxesTensor.Dimensions[1];
                        
                        int clusterId = 1;
                        for (int b = 0; b < numBoxes; b++)
                        {
                            float score = scoresTensor[0, b];
                            if (score < 0.1f) continue; // Early filter 

                            long labelId = labelsTensor[0, b];
                            if (labelId < 0 || labelId >= Labels.Length) continue;

                            string label = Labels[labelId];

                            float xmin = boxesTensor[0, b, 0];
                            float ymin = boxesTensor[0, b, 1];
                            float xmax = boxesTensor[0, b, 2];
                            float ymax = boxesTensor[0, b, 3];

                            // ONNX postprocessor outputs absolute [xmin, ymin, xmax, ymax]
                            // Convert TOPLEFT coordinates to BOTTOMLEFT (PDF origin)
                            var bbox = new BoundingBox(xmin, height - ymax, xmax, height - ymin);

                            clusters.Add(new LayoutCluster
                            {
                                Id = clusterId++,
                                Label = label,
                                Confidence = score,
                                Bbox = bbox
                            });
                        }

                        results[angle] = clusters;
                    }
                    finally
                    {
                        PdfiumNative.FPDF_ClosePage(page);
                    }
                }
            }
            finally
            {
                if (doc != IntPtr.Zero)
                {
                    PdfiumNative.FPDF_CloseDocument(doc);
                }
                pdfBytesHandle?.Free();
            }
            
            return LayoutProcessResult.Succeeded(results, "ONNX inference completed successfully.");
        }, cancellationToken);

        try
        {
            return await runTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return LayoutProcessResult.RecoverableFailure("OnnxInferenceError", ex.Message);
        }
    }

    private static DenseTensor<float> CreateNormalizedTensor(SKBitmap bitmap)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, 640, 640 });

        // Pre-compute per-channel fused constants so the inner loop only needs
        // multiply + subtract instead of two divisions per pixel per channel:
        //   (pixel / 255 - mean) / std  ==  pixel * invStd255 - bias
        const float inv255 = 1f / 255f;
        float invStdR = inv255 / 0.229f, biasR = 0.485f / 0.229f;
        float invStdG = inv255 / 0.224f, biasG = 0.456f / 0.224f;
        float invStdB = inv255 / 0.225f, biasB = 0.406f / 0.225f;

        const int width = 640;
        const int height = 640;
        const int channelStride = width * height;

        unsafe
        {
            byte* srcPtr = (byte*)bitmap.GetPixels().ToPointer();
            Span<float> destSpan = tensor.Buffer.Span;

            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    int srcIdx = (rowOffset + x) * 4;

                    // SKColorType.Bgra8888 format
                    byte b = srcPtr[srcIdx];
                    byte g = srcPtr[srcIdx + 1];
                    byte r = srcPtr[srcIdx + 2];

                    int pixelIdx = rowOffset + x;

                    destSpan[0 * channelStride + pixelIdx] = r * invStdR - biasR;
                    destSpan[1 * channelStride + pixelIdx] = g * invStdG - biasG;
                    destSpan[2 * channelStride + pixelIdx] = b * invStdB - biasB;
                }
            }
        }

        return tensor;
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}