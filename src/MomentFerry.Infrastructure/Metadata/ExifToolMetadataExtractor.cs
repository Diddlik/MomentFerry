using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MomentFerry.Application.Abstractions;
using MomentFerry.Core.Domain;

namespace MomentFerry.Infrastructure.Metadata;

public sealed class ExifToolMetadataExtractor(string executable = "exiftool") : IMediaMetadataExtractor
{
    public async Task<MediaMetadata> ExtractAsync(
        Share share,
        string path,
        MediaType mediaType,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in new[]
            {
                "-json",
                "-n",
                "-DateTimeOriginal",
                "-OffsetTimeOriginal",
                "-CreateDate",
                "-ModifyDate",
                "-MediaCreateDate",
                "-TrackCreateDate",
                "-Make",
                "-Model",
                "-AndroidManufacturer",
                "-AndroidModel",
                "-ModelName",
                "-Author",
                "-ImageWidth",
                "-ImageHeight",
                "-Duration",
                "-MIMEType",
                path
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var output = await outputTask;
            var error = await errorTask;

            // A non-zero exit still carries usable JSON when ExifTool merely warned, for example about a
            // tag this build does not know. Losing every field over a warning would be worse than the
            // warning itself.
            if (process.ExitCode != 0 && !output.TrimStart().StartsWith('['))
            {
                return Empty(error.Length == 0 ? $"ExifTool exited with code {process.ExitCode}." : error.Trim());
            }

            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return Empty("ExifTool returned no metadata.");
            }

            var root = document.RootElement[0];
            var (capturedAt, source, inferred) = ResolveTimestamp(root, share, mediaType);

            return new MediaMetadata(
                capturedAt,
                source,
                inferred,
                GetString(root, "Make") ?? GetString(root, "AndroidManufacturer"),
                ResolveModel(root),
                GetInt32(root, "ImageWidth"),
                GetInt32(root, "ImageHeight"),
                GetDouble(root, "Duration"),
                GetString(root, "MIMEType"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or JsonException or FormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return Empty(ex.Message);
        }
    }

    /// <summary>
    /// Phone recordings usually carry no Make/Model at all. Android writes com.android.model into the
    /// QuickTime keys, and Samsung writes its model code into the smta box plus the marketing device
    /// name ("Galaxy S25") into the user-data author field. The author field is only trusted when the
    /// Samsung box proves who wrote the file, because everywhere else it is free text that could name
    /// a person. Preferring the marketing name keeps a video named like a photo from the same phone.
    /// </summary>
    private static string? ResolveModel(JsonElement root)
    {
        var model = GetString(root, "Model") ?? GetString(root, "AndroidModel");
        if (!string.IsNullOrWhiteSpace(model)) return model;

        var samsungModel = GetString(root, "ModelName");
        if (string.IsNullOrWhiteSpace(samsungModel)) return null;

        return GetString(root, "Author") ?? samsungModel;
    }

    private static (DateTimeOffset? Value, string? Source, bool Inferred) ResolveTimestamp(
        JsonElement root,
        Share share,
        MediaType mediaType)
    {
        var candidates = mediaType == MediaType.Video
            ? new[] { "MediaCreateDate", "CreateDate", "TrackCreateDate" }
            : new[] { "DateTimeOriginal", "CreateDate", "ModifyDate" };

        var explicitOffset = GetString(root, "OffsetTimeOriginal");

        foreach (var field in candidates)
        {
            var raw = GetString(root, field);
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (field == "DateTimeOriginal" && !string.IsNullOrWhiteSpace(explicitOffset) && !HasOffset(raw))
            {
                raw += explicitOffset.StartsWith('+') || explicitOffset.StartsWith('-')
                    ? explicitOffset
                    : $"+{explicitOffset}";
            }

            if (TryParseWithOffset(raw, out var absolute))
            {
                return (absolute, field, false);
            }

            if (TryParseLocal(raw, out var local))
            {
                var zoneId = string.IsNullOrWhiteSpace(share.DefaultTimeZone)
                    ? TimeZoneInfo.Local.Id
                    : share.DefaultTimeZone;
                var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
                var offset = zone.GetUtcOffset(local);
                return (new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset), field, true);
            }
        }

        return (null, null, false);
    }

    private static bool TryParseWithOffset(string value, out DateTimeOffset result)
    {
        var formats = new[]
        {
            "yyyy:MM:dd HH:mm:sszzz",
            "yyyy:MM:dd HH:mm:ss.FFFzzz",
            "yyyy:MM:dd HH:mm:ssK",
            "yyyy:MM:dd HH:mm:ss.FFFK",
            "yyyy-MM-dd'T'HH:mm:sszzz",
            "yyyy-MM-dd'T'HH:mm:ssK"
        };

        return DateTimeOffset.TryParseExact(
            value,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out result);
    }

    private static bool TryParseLocal(string value, out DateTime result)
    {
        var formats = new[]
        {
            "yyyy:MM:dd HH:mm:ss",
            "yyyy:MM:dd HH:mm:ss.FFF",
            "yyyy-MM-dd'T'HH:mm:ss",
            "yyyy-MM-dd'T'HH:mm:ss.FFF"
        };

        return DateTime.TryParseExact(
            value,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out result);
    }

    private static bool HasOffset(string value)
    {
        if (value.EndsWith('Z')) return true;
        if (value.Length < 6) return false;
        var suffix = value[^6..];
        return (suffix[0] == '+' || suffix[0] == '-') && suffix[3] == ':';
    }

    private static string? GetString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static int? GetInt32(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static double? GetDouble(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetDouble(out var result) ? result : null;

    private static MediaMetadata Empty(string error) =>
        new(null, null, false, null, null, null, null, null, null, error);
}
