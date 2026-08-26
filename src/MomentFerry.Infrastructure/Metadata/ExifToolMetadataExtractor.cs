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
                "-CreationDate",
                "-SamsungAndroidUtcOffset",
                "-ModifyDate",
                "-MediaCreateDate",
                "-TrackCreateDate",
                "-Make",
                "-Model",
                "-AndroidManufacturer",
                "-AndroidModel",
                "-OplusProductModel",
                "-SamsungModel",
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
            var (capturedAt, source, inferred, reportedOffset) = ResolveTimestamp(root, share, mediaType);

            return new MediaMetadata(
                capturedAt,
                source,
                inferred,
                reportedOffset,
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
    /// QuickTime keys, which ExifTool reports as AndroidModel. OnePlus writes neither, and puts the
    /// marketing name into its own key com.oplus.product.model ("OnePlus 12"), reported as
    /// OplusProductModel. Samsung instead writes its model code
    /// into a maker note (SamsungModel, "SM-S921B") and the marketing device name into the user-data
    /// author field (QuickTime:Author, "Galaxy S24"). Author is only trusted once SamsungModel proves
    /// who wrote the file, because everywhere else it is free text that could name a person.
    /// Preferring the marketing name keeps a video named like a photo from the same phone.
    /// </summary>
    private static string? ResolveModel(JsonElement root)
    {
        var model = GetString(root, "Model")
            ?? GetString(root, "AndroidModel")
            ?? GetString(root, "OplusProductModel");
        if (!string.IsNullOrWhiteSpace(model)) return model;

        var samsungModel = GetString(root, "SamsungModel");
        if (string.IsNullOrWhiteSpace(samsungModel)) return null;

        return GetString(root, "Author") ?? samsungModel;
    }

    /// <summary>
    /// Resolves the capture instant, and reports separately whether the file itself stated the offset
    /// it was taken at. The two are not the same question: a QuickTime video's MediaCreateDate is a
    /// certain instant in UTC and says nothing about the wall-clock time on the camera, so pinning
    /// offset zero from it would name the file two hours before the clock the recording shows.
    /// </summary>
    private static (DateTimeOffset? Value, string? Source, bool Inferred, TimeSpan? ReportedOffset) ResolveTimestamp(
        JsonElement root,
        Share share,
        MediaType mediaType)
    {
        // CreationDate first for video: OnePlus and Apple write the real local time with its offset
        // there, which needs no assumption at all. The remaining QuickTime fields are UTC by
        // specification and carry no offset.
        var candidates = mediaType == MediaType.Video
            ? new[] { "CreationDate", "MediaCreateDate", "CreateDate", "TrackCreateDate" }
            : new[] { "DateTimeOriginal", "CreateDate", "ModifyDate" };

        var explicitOffset = GetString(root, "OffsetTimeOriginal");
        var recordedOffset = ParseUtcOffset(GetString(root, "SamsungAndroidUtcOffset"));

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
                // The value carried its own offset: OffsetTimeOriginal for a photo, CreationDate for a
                // recording. That is the camera's own statement about its clock.
                return (absolute, field, false, absolute.Offset);
            }

            if (!TryParseLocal(raw, out var local))
            {
                continue;
            }

            if (mediaType == MediaType.Video)
            {
                // QuickTime stores these in UTC. Reading them as the machine's local time shifted every
                // video by the container's offset, and claimed the zone was known while doing it.
                // Samsung records the offset it was filmed at separately, which lets the same instant
                // be expressed in the zone of the recording instead of in UTC.
                var instant = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Utc));
                return (
                    recordedOffset is null ? instant : instant.ToOffset(recordedOffset.Value),
                    field,
                    false,
                    // Only Samsung's tag states the zone it was filmed in. Without it the instant is
                    // certain and the wall-clock offset is unknown, which is not the same as zero.
                    recordedOffset);
            }

            // A photo timestamp really is local wall-clock time with nothing to anchor it, so the
            // share's zone is a genuine assumption and is reported as one. An unresolvable zone must
            // not take the extraction down with it: without tzdata even a correct id throws, and a
            // routing cycle that dies on the first photo is worse than a capture time read as UTC.
            var zoneId = string.IsNullOrWhiteSpace(share.DefaultTimeZone)
                ? TimeZoneInfo.Local.Id
                : share.DefaultTimeZone;
            TimeSpan offset;
            try
            {
                offset = TimeZoneInfo.FindSystemTimeZoneById(zoneId).GetUtcOffset(local);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                offset = TimeSpan.Zero;
            }

            return (new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset), field, true, null);
        }

        return (null, null, false, null);
    }

    /// <summary>
    /// Reads an offset as cameras write it, "+0200" or "+02:00", and "Z" for UTC.
    /// </summary>
    private static TimeSpan? ParseUtcOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        if (trimmed is "Z" or "z") return TimeSpan.Zero;

        var sign = trimmed[0] switch { '+' => 1, '-' => -1, _ => 0 };
        if (sign == 0) return null;

        var digits = trimmed[1..].Replace(":", string.Empty);
        if (digits.Length != 4 ||
            !int.TryParse(digits[..2], CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(digits[2..], CultureInfo.InvariantCulture, out var minutes) ||
            hours > 14 || minutes > 59)
        {
            return null;
        }

        return sign * new TimeSpan(hours, minutes, 0);
    }

    /// <summary>
    /// Only succeeds when the value actually carries an offset. The "K" format specifier matches an
    /// empty offset as well and then silently attaches the machine's own zone, which is how every
    /// video ended up shifted by the container's offset while being reported as certain.
    /// </summary>
    private static bool TryParseWithOffset(string value, out DateTimeOffset result)
    {
        result = default;
        if (!HasOffset(value)) return false;

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
