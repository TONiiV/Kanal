using System.Text.Json;
using Kanal.Core.Diagnostics;
using Kanal.Host.Diagnostics;
using Kanal.Host.Services;
using NLog.Targets;

namespace Kanal.Core.UnitTests;

/// <summary>
/// What the operator's two log choices — how much detail, how big a file may get — actually do to
/// the files on disk, and what a settings file written before any of this existed falls back to.
/// </summary>
public class LogSettingsTests
{
    [Fact]
    public void LogSettingsRoundTripAsReadableText()
    {
        var settings = new AppSettings
        {
            LogLevel = LogLevel.Debug,
            LogMaxFileSizeMb = 25,
        };

        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json)!;

        // the level is a word in the file, not an ordinal — the settings file is edited by hand
        // often enough that "3" for Error would be a trap
        Assert.Contains("\"Debug\"", json);
        Assert.Equal(LogLevel.Debug, loaded.LogLevel);
        Assert.Equal(25, loaded.LogMaxFileSizeMb);
    }

    [Fact]
    public void ASettingsFileFromBeforeLoggingGetsTheDefaults()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>(
            """{"ApiKeys":[],"ActiveGladiaKeyName":null}""")!;

        Assert.Equal(LogLevel.Info, loaded.LogLevel);
        Assert.Equal(10, loaded.LogMaxFileSizeMb);
    }

    [Fact]
    public void LogsLiveUnderTheKanalAppDataDirectory()
    {
        Assert.EndsWith(Path.Combine("Kanal", "logs"), SettingsStore.LogsPath);
    }

    /// <summary>
    /// The box takes any number of megabytes, so it also takes 0 and -1. A rollover threshold of
    /// zero archives on every line; the clamp is what stands between a typo and ten thousand files.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(25, 25)]
    [InlineData(4096, 1024)]
    public void TheSizeLimitIsClampedToSomethingAFileSystemSurvives(int configured, int expected)
    {
        var settings = new AppSettings { LogMaxFileSizeMb = configured };

        Assert.Equal(expected, SettingsStore.ResolveLogMaxFileSizeMb(settings));
    }
}

/// <summary>
/// The NLog configuration is built in code rather than from an XML file: it has to be rebuilt when
/// the operator changes the level mid-meeting, and a config file next to the executable is one
/// more thing that can go missing from a published build.
/// </summary>
[Collection(LoggingCollection.Name)]
public class LogSetupTests
{
    private static FileTarget Target(AppSettings settings, string directory) =>
        Assert.IsType<FileTarget>(
            LogSetup.BuildConfiguration(directory, settings).FindTargetByName(LogSetup.TargetName));

    [Fact]
    public void OneFileADayCarriesTheDateInItsName()
    {
        var target = Target(new AppSettings(), "/logs");

        var fileName = target.FileName.ToString();
        Assert.Contains("${shortdate}", fileName);
        Assert.Contains("/logs", fileName);
    }

    [Fact]
    public void TheFileRollsOverAtTheConfiguredNumberOfMegabytes()
    {
        var target = Target(new AppSettings { LogMaxFileSizeMb = 7 }, "/logs");

        Assert.Equal(7L * 1024 * 1024, target.ArchiveAboveSize);
    }

    /// <summary>Old files go on their own, or a laptop left running becomes the disk problem.</summary>
    [Fact]
    public void OldFilesAreNotKeptForever()
    {
        var target = Target(new AppSettings(), "/logs");

        Assert.True(target.MaxArchiveFiles > 0);
        Assert.True(target.MaxArchiveDays > 0);
    }

    [Theory]
    [InlineData(LogLevel.Debug, "Debug")]
    [InlineData(LogLevel.Info, "Info")]
    [InlineData(LogLevel.Warning, "Warn")]
    [InlineData(LogLevel.Error, "Error")]
    public void TheChosenLevelIsTheFloorForWhatIsWritten(LogLevel level, string expected)
    {
        var config = LogSetup.BuildConfiguration("/logs", new AppSettings { LogLevel = level });

        var rule = Assert.Single(config.LoggingRules);
        Assert.Equal(expected, NLog.LogLevel.FromOrdinal(rule.Levels.Min(l => l.Ordinal)).Name);
    }

    /// <summary>
    /// The point of the size box: past the threshold the day's file is rolled over and a fresh one
    /// carries on under the same name, so "today's log" stays one findable file.
    /// </summary>
    [Fact]
    public void PastTheSizeLimitTheDayGetsASecondFile()
    {
        var directory = TempDirectory();
        try
        {
            using var scope = LogSetup.InstallFor(directory, new AppSettings { LogMaxFileSizeMb = 1 });

            var padding = new string('x', 200);
            for (var i = 0; i < 8000; i++)
                Log.Info("test", $"line {i} {padding}");
            scope.Flush();

            var files = Directory.GetFiles(directory);
            Assert.True(files.Length > 1, $"expected a rollover, found {files.Length} file(s)");
            // the live file keeps the plain dated name; the rollovers are the ones with a suffix
            Assert.Contains(files, f => Path.GetFileName(f) == $"kanal-{DateTime.Now:yyyy-MM-dd}.log");
        }
        finally
        {
            Cleanup(directory);
        }
    }

    /// <summary>
    /// Every level the facade offers has to survive the trip through NLog, including the one the
    /// operator only turns on to reproduce a fault.
    /// </summary>
    [Fact]
    public void WritingThroughTheSinkLandsInTheFile()
    {
        var directory = TempDirectory();
        try
        {
            using var scope = LogSetup.InstallFor(directory, new AppSettings { LogLevel = LogLevel.Debug });

            Log.Debug("test", "a debug line");
            Log.Info("test", "an info line");
            Log.Warning("test", "a warning line");
            Log.Error("test", "an error line", new InvalidOperationException("the cause"));
            scope.Flush();

            var written = string.Join("\n", Directory.GetFiles(directory).Select(File.ReadAllText));
            Assert.Contains("a debug line", written);
            Assert.Contains("an info line", written);
            Assert.Contains("a warning line", written);
            Assert.Contains("an error line", written);
            // the exception is the half of an error line worth having
            Assert.Contains("the cause", written);
            // and the category, so a line can be traced back to what produced it
            Assert.Contains("test", written);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    /// <summary>Below the floor is silence, not a smaller line.</summary>
    [Fact]
    public void ALineBelowTheChosenLevelIsNotWritten()
    {
        var directory = TempDirectory();
        try
        {
            using var scope = LogSetup.InstallFor(directory, new AppSettings { LogLevel = LogLevel.Warning });

            Log.Info("test", "chatter nobody asked for");
            Log.Warning("test", "worth keeping");
            scope.Flush();

            var written = string.Join("\n", Directory.GetFiles(directory).Select(File.ReadAllText));
            Assert.DoesNotContain("chatter nobody asked for", written);
            Assert.Contains("worth keeping", written);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "kanal-log-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
