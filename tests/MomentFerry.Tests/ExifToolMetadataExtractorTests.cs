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
        // moov/udta of a Galaxy S25 recording: no Make, no Model, "SM-S931B" in the smta box and
        // "Galaxy S25" in auth. The video must end up named like a photo from the same phone.
        var extractor = StubReturning("""
            [{"SourceFile":"x.mp4","ModelName":"SM-S931B","Author":"Galaxy S25","MediaCreateDate":"2026:08:07 14:21:56"}]
            """);

        var metadata = await extractor.ExtractAsync(_share, "x.mp4", MediaType.Video);

        Assert.Equal("Galaxy S25", metadata.CameraModel);
    }

    [Fact]
    public async Task SamsungVideoWithoutAuthor_FallsBackToTheModelCode()
    {
        var extractor = StubReturning("""
            [{"SourceFile":"x.mp4","ModelName":"SM-S931B","MediaCreateDate":"2026:08:07 14:21:56"}]
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
        // Without the Samsung box the author field is free text and could be a person's name.
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
