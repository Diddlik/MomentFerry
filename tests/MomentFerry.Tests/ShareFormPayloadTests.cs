namespace MomentFerry.Tests;

public sealed class ShareFormPayloadTests
{
    [Fact]
    public void Submit_IncludesSelectedRenamePreset()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var appJs = File.ReadAllText(Path.Combine(directory!.FullName, "src", "MomentFerry.Web", "wwwroot", "app.js"));
        var start = appJs.IndexOf("$('shareForm').addEventListener('submit'", StringComparison.Ordinal);
        var end = appJs.IndexOf("$('groupForm').addEventListener('submit'", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        Assert.Contains("renamePresetId: $('sharePreset').value || null", appJs[start..end]);
    }
}
