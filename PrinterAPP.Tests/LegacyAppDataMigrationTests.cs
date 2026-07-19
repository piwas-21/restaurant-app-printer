using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// <see cref="LegacyAppDataMigration.TryCopyLegacyFile"/> is the copy-only path migration used
/// for both config.json and print_style_settings.json. The invariants pinned here protect the
/// live client machine: the legacy file is never modified, an existing new file always wins,
/// and failures degrade to "no copy" instead of crashing startup.
/// </summary>
public sealed class LegacyAppDataMigrationTests : IDisposable
{
    private readonly FakeAppDataPathProvider _paths = new();

    public void Dispose() => _paths.Dispose();

    private string LegacyFile => Path.Combine(_paths.LegacyAppDataDirectory, "settings.json");

    private string NewFile => Path.Combine(_paths.AppDataDirectory, "settings.json");

    [Fact]
    public void Copies_when_only_legacy_exists_and_keeps_the_original()
    {
        File.WriteAllText(LegacyFile, "{\"a\":1}");

        var copied = LegacyAppDataMigration.TryCopyLegacyFile(LegacyFile, NewFile);

        Assert.True(copied);
        Assert.Equal("{\"a\":1}", File.ReadAllText(NewFile));
        Assert.Equal("{\"a\":1}", File.ReadAllText(LegacyFile)); // original left in place
    }

    [Fact]
    public void Does_not_overwrite_an_existing_new_file()
    {
        File.WriteAllText(LegacyFile, "{\"a\":\"old\"}");
        File.WriteAllText(NewFile, "{\"a\":\"new\"}");

        var copied = LegacyAppDataMigration.TryCopyLegacyFile(LegacyFile, NewFile);

        Assert.False(copied);
        Assert.Equal("{\"a\":\"new\"}", File.ReadAllText(NewFile));
        Assert.Equal("{\"a\":\"old\"}", File.ReadAllText(LegacyFile));
    }

    [Fact]
    public void Returns_false_when_legacy_file_is_missing()
    {
        var copied = LegacyAppDataMigration.TryCopyLegacyFile(LegacyFile, NewFile);

        Assert.False(copied);
        Assert.False(File.Exists(NewFile));
    }

    [Fact]
    public void Creates_the_destination_directory_when_missing()
    {
        File.WriteAllText(LegacyFile, "{}");
        var nested = Path.Combine(_paths.AppDataDirectory, "nested", "dir", "settings.json");

        var copied = LegacyAppDataMigration.TryCopyLegacyFile(LegacyFile, nested);

        Assert.True(copied);
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Returns_false_instead_of_throwing_when_destination_is_unusable()
    {
        File.WriteAllText(LegacyFile, "{}");
        Directory.CreateDirectory(NewFile); // a directory squats on the destination path

        var copied = LegacyAppDataMigration.TryCopyLegacyFile(LegacyFile, NewFile);

        Assert.False(copied);
        Assert.Equal("{}", File.ReadAllText(LegacyFile));
    }
}
