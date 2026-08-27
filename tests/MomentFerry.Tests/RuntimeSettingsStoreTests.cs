using MomentFerry.Application.Abstractions;
using MomentFerry.Infrastructure.Runtime;

namespace MomentFerry.Tests;

public sealed class RuntimeSettingsStoreTests
{
    [Fact]
    public async Task ExistingSettingsWithoutParallelism_UseSafeDefault()
    {
        var directory = Path.Combine(Path.GetTempPath(), "momentferry-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "runtime-settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, """{"dryRun":true,"maxFilesPerSharePerCycle":200}""");

            var settings = await new JsonRuntimeSettingsStore(path, new()).GetAsync();

            Assert.Equal(2, settings.MaxParallelMetadataReads);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Update_PersistsStorageReserveAutomaticUpdateAndPasswordProtection()
    {
        var directory = Path.Combine(Path.GetTempPath(), "momentferry-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "runtime-settings.json");
        try
        {
            var store = new JsonRuntimeSettingsStore(path, new());
            await store.UpdateAsync(new MomentFerryRuntimeSettings(
                MinimumFreeSpaceReserveBytes: 2L * 1024 * 1024 * 1024,
                AutomaticImageUpdatesEnabled: true,
                PasswordProtectionEnabled: true));

            var reloaded = await new JsonRuntimeSettingsStore(path, new()).GetAsync();

            Assert.Equal(2L * 1024 * 1024 * 1024, reloaded.MinimumFreeSpaceReserveBytes);
            Assert.True(reloaded.AutomaticImageUpdatesEnabled);
            Assert.True(reloaded.PasswordProtectionEnabled);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }
}
