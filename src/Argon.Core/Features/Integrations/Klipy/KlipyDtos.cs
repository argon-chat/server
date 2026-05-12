namespace Argon.Features.Integrations.Klipy;

using System.Text.Json.Serialization;

#region API Response Envelope

public record KlipyResponse<T>
{
    [JsonPropertyName("result")]
    public bool Result { get; init; }

    [JsonPropertyName("data")]
    public T? Data { get; init; }
}

/// <summary>Paginated response: data.data[] + data.has_next</summary>
public record KlipyPagedData<T>
{
    [JsonPropertyName("data")]
    public List<T>? Data { get; init; }

    [JsonPropertyName("has_next")]
    public bool HasNext { get; init; }
}

#endregion

#region Media Item

public record KlipyMediaItem
{
    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("blur_preview")]
    public string? BlurPreview { get; init; }

    [JsonPropertyName("file")]
    public KlipyDimensions? File { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

#endregion

#region File Dimensions (hd / md / sm / xs)

public record KlipyDimensions
{
    [JsonPropertyName("hd")]
    public KlipyFileTypes? Hd { get; init; }

    [JsonPropertyName("md")]
    public KlipyFileTypes? Md { get; init; }

    [JsonPropertyName("sm")]
    public KlipyFileTypes? Sm { get; init; }

    [JsonPropertyName("xs")]
    public KlipyFileTypes? Xs { get; init; }
}

public record KlipyFileTypes
{
    [JsonPropertyName("gif")]
    public KlipyFileMetadata? Gif { get; init; }

    [JsonPropertyName("webp")]
    public KlipyFileMetadata? Webp { get; init; }

    [JsonPropertyName("mp4")]
    public KlipyFileMetadata? Mp4 { get; init; }
}

public record KlipyFileMetadata
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }
}

#endregion

#region Categories

public record KlipyCategoriesData
{
    [JsonPropertyName("locale")]
    public string? Locale { get; init; }

    [JsonPropertyName("categories")]
    public List<KlipyCategory>? Categories { get; init; }
}

public record KlipyCategory
{
    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("query")]
    public string Query { get; init; } = string.Empty;

    [JsonPropertyName("preview_url")]
    public string PreviewUrl { get; init; } = string.Empty;
}

#endregion
