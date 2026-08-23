using MomentFerry.Application.Services;
using MomentFerry.Core.Domain;

namespace MomentFerry.Tests;

public sealed class FileNameTemplateTests
{
    private static FileNameContext Context(
        string stem = "img20260216_123056",
        string? camera = "OnePlus12",
        string? make = "OnePlus",
        string? model = "CPH2581") => new(
            stem,
            new DateTimeOffset(2026, 2, 16, 12, 30, 55, TimeSpan.Zero),
            camera,
            make,
            model,
            "Phone",
            "Pavel",
            "Italy 2026",
            "Vacation");

    [Fact]
    public void Render_ProducesTheNameFromTheIssueExample()
    {
        var result = FileNameTemplate.Render("{captured:yyyyMMdd_HHmmss}_{camera}_{seq:0000}", Context(), 132);

        Assert.Equal("20260216_123055_OnePlus12_0132", result);
    }

    [Fact]
    public void Render_KeepsTheOriginalStemWhenTheTemplateIsEmpty()
    {
        Assert.Equal("img20260216_123056", FileNameTemplate.Render("", Context(), 1));
        Assert.Equal("img20260216_123056", FileNameTemplate.Render(null, Context(), 1));
    }

    [Fact]
    public void Render_CollapsesSeparatorsLeftByEmptyTokens()
    {
        // No camera is known, so the template must not leave "20260216__0001" behind.
        var result = FileNameTemplate.Render(
            "{captured:yyyyMMdd}_{camera}_{seq:0000}",
            Context(camera: null, make: null, model: null),
            1);

        Assert.Equal("20260216_0001", result);
    }

    [Fact]
    public void Render_FallsBackToTheStemWhenEveryTokenIsEmpty()
    {
        var result = FileNameTemplate.Render("{camera}{owner:ignored}", Context(camera: null) with { Owner = null }, 1);

        Assert.Equal("img20260216_123056", result);
    }

    [Fact]
    public void Render_SanitizesTheSameCharactersOnEveryPlatform()
    {
        // Linux reports only '/' as invalid, so relying on the platform list would let the container
        // write names that are unusable when the same library is opened from Windows over SMB.
        var result = FileNameTemplate.Render("{event.name}_{name}", Context(stem: "a:b*c") with { EventName = "Trip/2026" }, 1);

        Assert.Equal("Trip_2026_a_b_c", result);
    }

    [Fact]
    public void Render_IgnoresUnknownTokensInsteadOfLeakingBraces()
    {
        var result = FileNameTemplate.Render("{name}_{nope}", Context(), 1);

        Assert.Equal("img20260216_123056", result);
        Assert.DoesNotContain('{', result);
    }

    [Theory]
    [InlineData("{seq}", true)]
    [InlineData("{SEQ:000}", true)]
    [InlineData("{captured:yyyy}", false)]
    [InlineData("", false)]
    public void UsesSequence_DetectsTemplatesThatNumberTheirOutput(string template, bool expected)
    {
        Assert.Equal(expected, FileNameTemplate.UsesSequence(template));
    }

    [Fact]
    public void ResolveCamera_AppliesTheMappingTableToTheReportedModel()
    {
        var names = FileNameTemplate.BuildCameraNames([new CameraMapping { From = "CPH2581", To = "OnePlus12" }]);

        Assert.Equal("OnePlus12", FileNameTemplate.ResolveCamera("OnePlus", "cph2581", names));
    }

    [Fact]
    public void ResolveCamera_KeepsTheReportedModelWhenNothingMapsIt()
    {
        var names = FileNameTemplate.BuildCameraNames([]);

        Assert.Equal("CPH2581", FileNameTemplate.ResolveCamera("OnePlus", "CPH2581", names));
        Assert.Equal("OnePlus", FileNameTemplate.ResolveCamera("OnePlus", null, names));
        Assert.Null(FileNameTemplate.ResolveCamera(null, null, names));
    }
}
