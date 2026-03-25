using System.Collections.Generic;
using System.Text.Json.Serialization;
using DoclingDotNet.Algorithms.Layout;

namespace DoclingDotNet.Models;

public sealed class DoclingPagePredictions
{
    [JsonPropertyName("layout")]
    public LayoutPrediction? Layout { get; set; }
}

public sealed class LayoutPrediction
{
    [JsonPropertyName("clusters")]
    public List<LayoutCluster> Clusters { get; set; } = [];
}
