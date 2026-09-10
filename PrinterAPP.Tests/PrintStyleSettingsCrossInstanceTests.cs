using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// The saved-settings-reach-the-printer contract (2026-09-10 partner feedback: the venue changed
/// the text format settings, saved, and every print stayed exactly the same). Two defects hid in
/// sequence: the print service read styles ONCE into a field at construction (it is a singleton),
/// and the settings service cached per instance while the settings page saves through a DIFFERENT
/// instance than the one the print service reads through. The contract is cross-instance: a save
/// through one instance must be visible to a load through another, on the very next read.
/// </summary>
public class PrintStyleSettingsCrossInstanceTests
{
    private sealed class TempPathProvider : IAppDataPathProvider
    {
        public TempPathProvider()
        {
            AppDataDirectory = Path.Combine(Path.GetTempPath(), "pa-style-" + Guid.NewGuid().ToString("N"));
            LegacyAppDataDirectory = AppDataDirectory;
        }

        public string AppDataDirectory { get; }
        public string LegacyAppDataDirectory { get; }
    }

    [Fact]
    public void A_save_through_one_instance_is_visible_to_a_load_through_another()
    {
        var pathProvider = new TempPathProvider();
        var pageInstance = new PrintStyleSettingsService(pathProvider);
        var printServiceInstance = new PrintStyleSettingsService(pathProvider);

        // The pre-fix behaviour, reproduced as the baseline: the print side read the defaults.
        Assert.Equal(FontSize.Wide, printServiceInstance.LoadSettings().KitchenItemName.Size);

        var changed = pageInstance.LoadSettings();
        changed.KitchenItemName.Size = FontSize.Normal;
        changed.KitchenItemName.IsBold = true;
        pageInstance.SaveSettings(changed);

        var reloaded = printServiceInstance.LoadSettings();
        Assert.Equal(FontSize.Normal, reloaded.KitchenItemName.Size);
        Assert.True(reloaded.KitchenItemName.IsBold);
    }
}
