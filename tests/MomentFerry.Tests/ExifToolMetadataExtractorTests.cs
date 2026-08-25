using MomentFerry.Core.Domain;
using MomentFerry.Infrastructure.Metadata;

namespace MomentFerry.Tests;

/// <summary>
/// Drives the extractor against a stub that stands in for ExifTool, so the tag names the extractor
/// asks for and the way it folds them into a camera identity are covered without needing ExifTool
/// itself. The JSON bodies are the shapes real files produce: a phone photo carries Make and Model,
/// a Samsung recording carries neither and hides the device in the Samsung and author fields.
/// </summary>
public sealed class ExifToolMetadataExtractorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "momentferry-exiftool",
        Guid.NewGuid().ToString("N"));

    private readonly Share _share = new()
    {
        Id = Guid.NewGuid(),
        Name = "Phone",
        Path = "/sources/phone",
        Role = ShareRole.Source,
        Enabled = true,
        DefaultTimeZone = "UTC"
    };

    public ExifToolMetadataExtractorTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public async Task QuickTimeDateWithoutOffset_IsReadAsUtc()
    {
        // Verified against the real file: exiftool reports "2026:04:21 12:00:28" for a recording made
        // at 14:00:28 local. QuickTime stores these fields in UTC. Reading them as the machine's own
        // local time shifted every video by the container's offset.
        var extractor = StubReturning("""
            [{"SourceFile":"x.mp4","MediaCreateDate":"2026:04:21 12:00:28"}]
            """);

        var metadata = await extractor.ExtractAsync(_share, "x.mp4", MediaType.Video);

        Assert.Equal(new DateTimeOffset(2026, 4, 21, 12, 0, 28, TimeSpan.Zero), metadata.CapturedAt);
        Assert.Equal("MediaCreateDate", metadata.TimestampSource);
        Assert.False(metadata.TimeZoneInferred);
    }

    [Fact]
    public async Task PhotoWithAZoneThatCannotBeResolved_FallsBackToUtcInsteadOfThrowing()
    {
        // Without tzdata even a correct id like Europe/Berlin throws, and a routing cycle that dies on
        // the first photo is worse than a capture time read as UTC and reported as inferred.
        var share = new Share
        {
            Id = Guid.NewGuid(),
            Name = "Phone",
            Path = "/sources/phone",
            Role = ShareRole.Source,
            DefaultTimeZone = "Mars/Olympus_Mons"
        };
        var extractor = StubReturning("""
            [{"SourceFile":"x.jpg","DateTimeOriginal":"2026:08:21 13:52:53"}]
            """);

        var metadata = await extractor.ExtractAsync(share, "x.jpg", MediaType.Image);

        Assert.Equal(new DateTimeOffset(2026, 8, 21, 13, 52, 53, TimeSpan.Zero), metadata.CapturedAt);
        Assert.True(metadata.TimeZoneInferred);
    }

    [Fact]
    public async Task PhotoWithAnExplicitOffset_KeepsTheOffsetTheCameraWrote()
    {
        // The reported case. The offset is what lets the filename carry 13:52:53 rather than the
        // 11:52:53 the normalised instant would render.
        var extractor = StubReturning("""
            [{"SourceFile":"x.jpg","DateTimeOriginal":"2026:08:21 13:52:53","OffsetTimeOriginal":"+02:00"}]
            """);

        var metadata = await extractor.ExtractAsync(_share, "x.jpg", MediaType.Image);

        Assert.Equal(TimeSpan.FromHours(2), metadata.CapturedAt!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 8, 21, 13, 52, 53, TimeSpan.FromHours(2)), metadata.CapturedAt);
        Assert.False(metadata.TimeZoneInferred);
    }

    [Fact]
    public async Task CreationDateWins_BecauseItCarriesTheRecordingOffset()
    {
        // A OnePlus recording: MediaCreateDate is UTC, CreationDate is the same moment in the zone it
        // was filmed in.
        var extractor = StubReturning("""
            [{"SourceFile":"x.mp4","CreationDate":"2024:12:01 18:50:08+01:00","MediaCreateDate":"2024:12:01 17:50:20"}]
            """);

        var metadata = await extractor.ExtractAsync(_share, "x.mp4", MediaType.Video);

        Assert.Equal("CreationDate", metadata.TimestampSource);
        Assert.Equal(TimeSpan.FromHours(1), metadata.CapturedAt!.Value.Offset);
        Assert.Equal(
            new DateTimeOffset(2024, 12, 1, 17, 50, 8, TimeSpan.Zero),
            metadata.CapturedAt!.Value.ToUniversalTime());
    }

    [Fact]
    public async Task SamsungOffset_ExpressesTheSameInstantInTheRecordingZone()
    {
        var extractor = StubReturning("""
            [{"SourceFile":"x.mp4","MediaCreateDate":"2026:04:21 12:00:28","SamsungAndroidUtcOffset":"+0200"}]
            """);

        var metadata = await extractor.ExtractAsync(_share, "x.mp4", MediaType.Video);

        Assert.Equal(TimeSpan.FromHours(2), metadata.CapturedAt!.Value.Offset);
        Assert.Equal(14, metadata.CapturedAt!.Value.Hour);
        Assert.Equal(
            new DateTimeOffset(2026, 4, 21, 12, 0, 28, TimeSpan.Zero),
            metadata.CapturedAt!.Value.ToUniversalTime());
        Assert.False(metadata.TimeZoneInferred);
    }

    [Fact]
    public async Task PhotoWithoutOffset_StillFallsBackToTheShareZoneAndSaysSo()
    {
        // A photo timestamp really is local wall-clock time with nothing to anchor it, so the share
        // zone stays an assumption here and must keep being reported as one.
        var extractor = StubReturning("""
            [{"SourceFile":"x.jpg","DateTimeOriginal":"2026:04:21 14:00:28"}]
            """);

        var metadata = await extractor.ExtractAsync(_share, "x.jpg", MediaType.Image);

        Assert.True(metadata.TimeZoneInferred);
        Assert.Equal(14, metadata.CapturedAt!.Value.Hour);
        Assert.Equal(TimeSpan.Zero, metadata.CapturedAt!.Value.Offset);
    }

    [Fact]
    public async Task PhotoWithExplicitOffset_IsExact()
    {
        var extractor = StubReturning("""
            [{"SourceFile":"x.jpg","DateTimeOriginal":"2026:04:21 14:00:28","OffsetTimeOriginal":"+02:00"}]
            """);

        var metadata = await extractor.ExtractAsync(_share, "x.jpg", MediaType.Image);

        Assert.False(metadata.TimeZoneInferred);
        Assert.Equal(
            new DateTimeOffset(2026, 4, 21, 12, 0, 28, TimeSpan.Zero),
            metadata.CapturedAt!.Value.ToUniversalTime());
    }

    [Fact]
    public async Task PhotoWithMakeAndModel_IsReportedUnchanged()
    {
        var extractor = StubReturning("""
            [{"SourceFile":"x.jpg","Make":"samsung","Model":"Galaxy S25","MediaCreateDate":"2026:08:07 14:21:56"}]
            """);

        var metadata = await extractor.ExtractAsync(_share, "x.jpg", MediaType.Image);

        Assert.Equal("samsung", metadata.CameraMake);
        Assert.Equal("Galaxy S25", metadata.CameraModel);
    }

    [Fact]
    public async Task SamsungVideoWithoutModel_UsesTheMarketingNameFromTheAuthorField()
    {
        // moov/udta of a Galaxy S25 recording: no Make, no Model, "SM-S931B" in the Samsung maker note and
        // "Galaxy S25" in auth. The video must end up named like a photo from the same phone.
        var extractor = StubReturning("""
            [{"SourceFile":"x.mp4","SamsungModel":"SM-S931B","Author":"Galaxy S25","MediaCreateDate":"2026:08:07 14:21:56"}]
            """);

        var metadata = await extractor.ExtractAsync(_share, "x.mp4", MediaType.Video);

        Assert.Equal("Galaxy S25", metadata.CameraModel);
    }

    [Fact]
    public async Task SamsungVideoWithoutAuthor_FallsBackToTheModelCode()
    {
        var extractor = StubReturning("""
            [{"SourceFile":"x.mp4","SamsungModel":"SM-S931B","MediaCreateDate":"2026:08:07 14:21:56"}]
            """);

        var metadata = await extractor.ExtractAsync(_share, "x.mp4", MediaType.Video);

        Assert.Equal("SM-S931B", metadata.CameraModel);
    }

    [Fact]
    public async Task AndroidVideo_UsesTheAndroidKeys()
    {
        var extractor = StubReturning("""
            [{"SourceFile":"x.mp4","AndroidManufacturer":"Google","AndroidModel":"Pixel 9","MediaCreateDate":"2026:08:07 14:21:56"}]
            """);

        var metadata = await extractor.ExtractAsync(_share, "x.mp4", MediaType.Video);

        Assert.Equal("Google", metadata.CameraMake);
        Assert.Equal("Pixel 9", metadata.CameraModel);
    }

    [Fact]
    public async Task AuthorAlone_IsNotTreatedAsACamera()
    {
        // Without the Samsung model the author field is free text and could be a person's name.
        var extractor = StubReturning("""
            [{"SourceFile":"x.mp4","Author":"Anna Schmidt","MediaCreateDate":"2026:08:07 14:21:56"}]
            """);

        var metadata = await extractor.ExtractAsync(_share, "x.mp4", MediaType.Video);

        Assert.Null(metadata.CameraModel);
    }

    [Fact]
    public async Task WarningExit_StillUsesTheMetadataItPrinted()
    {
        var extractor = StubReturning(
            """
            [{"SourceFile":"x.mp4","Model":"DJI Osmo Action","MediaCreateDate":"2026:08:07 14:21:56"}]
            """,
            exitCode: 1);

        var metadata = await extractor.ExtractAsync(_share, "x.mp4", MediaType.Video);

        Assert.Equal("DJI Osmo Action", metadata.CameraModel);
        Assert.NotNull(metadata.CapturedAt);
    }

    private ExifToolMetadataExtractor StubReturning(string json, int exitCode = 0)
    {
        var payload = json.Trim().Replace("\r", string.Empty).Replace("\n", string.Empty);
        string stub;

        if (OperatingSystem.IsWindows())
        {
            stub = Path.Combine(_directory, "exiftool.cmd");
            File.WriteAllText(stub, $"@echo off\r\necho {payload.Replace("%", "%%").Replace("^", "^^").Replace("&", "^&").Replace("<", "^<").Replace(">", "^>").Replace("|", "^|")}\r\nexit /b {exitCode}\r\n");
        }
        else
        {
            stub = Path.Combine(_directory, "exiftool.sh");
            File.WriteAllText(stub, $"#!/bin/sh\ncat <<'MOMENTFERRY_JSON'\n{payload}\nMOMENTFERRY_JSON\nexit {exitCode}\n");
            File.SetUnixFileMode(
                stub,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return new ExifToolMetadataExtractor(stub);
    }
}
