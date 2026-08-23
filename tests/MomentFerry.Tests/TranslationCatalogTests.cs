using System.Text.Json;

namespace MomentFerry.Tests;

/// <summary>
/// Guards the UI translation catalogs: a language added without every key would
/// silently fall back to English strings inside an otherwise translated page.
/// </summary>
public sealed class TranslationCatalogTests
{
    private static string I18nDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return Path.Combine(directory!.FullName, "src", "MomentFerry.Web", "wwwroot", "i18n");
        }
    }

    private static Dictionary<string, string> Load(string file)
    {
        var content = File.ReadAllText(file);
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        Assert.True(start >= 0 && end > start, $"{Path.GetFileName(file)} does not assign an object literal.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(content[start..(end + 1)])!;
    }

    [Fact]
    public void EveryLanguageCoversTheSameKeys()
    {
        var files = Directory.GetFiles(I18nDirectory, "*.js").OrderBy(x => x).ToArray();
        Assert.NotEmpty(files);

        var reference = Load(Path.Combine(I18nDirectory, "de.js"));
        Assert.NotEmpty(reference);

        foreach (var file in files)
        {
            var catalog = Load(file);
            var missing = reference.Keys.Except(catalog.Keys).ToArray();
            var unknown = catalog.Keys.Except(reference.Keys).ToArray();

            Assert.True(missing.Length == 0, $"{Path.GetFileName(file)} is missing: {string.Join(" | ", missing)}");
            Assert.True(unknown.Length == 0, $"{Path.GetFileName(file)} has unknown keys: {string.Join(" | ", unknown)}");
            Assert.DoesNotContain(catalog, entry => string.IsNullOrWhiteSpace(entry.Value));
        }
    }
}
