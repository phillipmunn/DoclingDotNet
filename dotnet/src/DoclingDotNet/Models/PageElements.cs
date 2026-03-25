using System.Collections.Generic;
using System.Text.Json.Serialization;
using DoclingDotNet.Algorithms.Layout;

namespace DoclingDotNet.Models;

public abstract class BasePageElement
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("page_no")]
    public int PageNo { get; set; }

    [JsonPropertyName("cluster")]
    public LayoutCluster Cluster { get; set; } = new();

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

public sealed class TextElement : BasePageElement { }

public sealed class FigureElement : BasePageElement
{
    [JsonPropertyName("data")]
    public object? Data { get; set; }
}

public sealed class TableElement : BasePageElement
{
    [JsonPropertyName("data")]
    public object? Data { get; set; } // Map to actual table data type later
}

public sealed class ContainerElement : BasePageElement { }

public sealed class AssembledUnit
{
    [JsonPropertyName("elements")]
    public List<BasePageElement> Elements { get; set; } = [];

    [JsonPropertyName("headers")]
    public List<BasePageElement> Headers { get; set; } = [];

    [JsonPropertyName("body")]
    public List<BasePageElement> Body { get; set; } = [];
}
