namespace MomentFerry.Core.Domain;

public sealed class Share
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required string Path { get; init; }
    public ShareRole Role { get; init; }
    public bool Enabled { get; init; } = true;
    public string? Owner { get; init; }
    public string? Group { get; init; }
    public string? Preset { get; init; }
    public int StabilitySeconds { get; init; } = 30;
    public bool Recursive { get; init; } = true;
    public string? DefaultTimeZone { get; init; }
    public IReadOnlyList<string> IgnorePatterns { get; init; } = Array.Empty<string>();
    public IReadOnlySet<MediaType> AllowedMediaTypes { get; init; } = new HashSet<MediaType>
    {
        MediaType.Image,
        MediaType.Video
    };

    /// <summary>Source role: extensions treated as images. Empty falls back to the built-in list.</summary>
    public IReadOnlyList<string> ImageExtensions { get; init; } = Array.Empty<string>();

    /// <summary>Source role: extensions treated as videos. Empty falls back to the built-in list.</summary>
    public IReadOnlyList<string> VideoExtensions { get; init; } = Array.Empty<string>();

    /// <summary>Destination role: subfolder for images below the event folder. Empty keeps media together.</summary>
    public string? ImageSubfolder { get; init; }

    /// <summary>Destination role: subfolder for videos below the event folder. Empty keeps media together.</summary>
    public string? VideoSubfolder { get; init; }

    public IReadOnlyList<string> EffectiveImageExtensions
        => ImageExtensions.Count > 0 ? ImageExtensions : MediaExtensionDefaults.Images;

    public IReadOnlyList<string> EffectiveVideoExtensions
        => VideoExtensions.Count > 0 ? VideoExtensions : MediaExtensionDefaults.Videos;

    public string? SubfolderFor(MediaType mediaType) => mediaType switch
    {
        MediaType.Image => ImageSubfolder,
        MediaType.Video => VideoSubfolder,
        _ => null
    };
}
