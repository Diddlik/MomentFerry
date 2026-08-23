using System.Text;
using System.Text.RegularExpressions;
using MomentFerry.Core.Domain;

namespace MomentFerry.Application.Services;

/// <summary>Values a filename template can draw on. The stem never carries an extension.</summary>
public sealed record FileNameContext(
    string Stem,
    DateTimeOffset CapturedAt,
    string? Camera,
    string? CameraMake,
    string? CameraModel,
    string SourceName,
    string? Owner,
    string EventName,
    string? EventType);

/// <summary>
/// Renders filename templates such as <c>{captured:yyyyMMdd_HHmmss}_{camera}_{seq:0000}</c>.
/// The extension is never part of a template: it is carried over from the source file so a template
/// cannot accidentally produce an extension-less or mistyped name.
/// </summary>
public static partial class FileNameTemplate
{
    private const string DefaultCapturedFormat = "yyyyMMdd_HHmmss";
    private const string DefaultSequenceFormat = "0000";

    [GeneratedRegex(@"\{(?<token>[a-zA-Z][a-zA-Z.]*)(?::(?<format>[^}]*))?\}", RegexOptions.ExplicitCapture)]
    private static partial Regex TokenPattern();

    /// <summary>True when the template numbers its output and therefore needs a free sequence value.</summary>
    public static bool UsesSequence(string? template)
        => !string.IsNullOrWhiteSpace(template) &&
           TokenPattern().Matches(template).Any(m => m.Groups["token"].Value.Equals("seq", StringComparison.OrdinalIgnoreCase));

    public static string Render(string? template, FileNameContext context, int sequence)
    {
        if (string.IsNullOrWhiteSpace(template)) return Clean(context.Stem);

        var rendered = TokenPattern().Replace(template, match =>
        {
            var token = match.Groups["token"].Value.ToLowerInvariant();
            var format = match.Groups["format"].Success ? match.Groups["format"].Value : null;
            return token switch
            {
                "name" => context.Stem,
                "captured" => context.CapturedAt.ToString(Format(format, DefaultCapturedFormat)),
                "year" => context.CapturedAt.Year.ToString("0000"),
                "month" => context.CapturedAt.Month.ToString("00"),
                "day" => context.CapturedAt.Day.ToString("00"),
                "camera" => context.Camera ?? string.Empty,
                "camera.make" => context.CameraMake ?? string.Empty,
                "camera.model" => context.CameraModel ?? string.Empty,
                "source" => context.SourceName,
                "owner" => context.Owner ?? string.Empty,
                "event.name" => context.EventName,
                "event.type" => context.EventType ?? string.Empty,
                "seq" => sequence.ToString(Format(format, DefaultSequenceFormat)),
                // An unknown token renders empty rather than leaking braces into a filename.
                _ => string.Empty
            };
        });

        var cleaned = Clean(rendered);
        // A template whose tokens were all empty must not produce a nameless file.
        return cleaned.Length > 0 ? cleaned : Clean(context.Stem);
    }

    private static string Format(string? requested, string fallback)
        => string.IsNullOrWhiteSpace(requested) ? fallback : requested;

    /// <summary>
    /// Makes the rendered text safe to use as a single filename segment, and tidies the separators left
    /// behind by tokens that resolved to nothing, so a missing camera does not leave "20260216__0001".
    /// </summary>
    private static string Clean(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        var collapsed = CollapsePattern().Replace(builder.ToString(), "$1");
        collapsed = collapsed.Trim().Trim('_', '-', '.', ' ');
        return collapsed;
    }

    [GeneratedRegex(@"([_\-. ])\1+")]
    private static partial Regex CollapsePattern();

    /// <summary>
    /// Applies the configured camera mappings to the raw model, falling back to the make when a file
    /// reports no model at all.
    /// </summary>
    public static string? ResolveCamera(
        string? cameraMake,
        string? cameraModel,
        IReadOnlyDictionary<string, string> cameraNames)
    {
        var raw = string.IsNullOrWhiteSpace(cameraModel) ? cameraMake : cameraModel;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var trimmed = raw.Trim();
        return cameraNames.TryGetValue(trimmed, out var mapped) ? mapped : trimmed;
    }

    public static IReadOnlyDictionary<string, string> BuildCameraNames(IEnumerable<CameraMapping> mappings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.From) || string.IsNullOrWhiteSpace(mapping.To)) continue;
            result[mapping.From.Trim()] = mapping.To.Trim();
        }

        return result;
    }
}
