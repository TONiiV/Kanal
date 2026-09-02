using System.Text.Json;
using Kanal.Core.Diagnostics;
using Kanal.Host.Diagnostics;
using Kanal.Host.Services;
using NLog.Targets;

namespace Kanal.Core.UnitTests;

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

    [Theory]
    [InlineData("\"Warn\"")]
    [InlineData("\"verbose\"")]
    [InlineData("null")]
    [InlineData("42")]
    public void AnUnreadableLevelCostsTheLevelAndNothingElse(string written)
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>(
            $$"""{"ApiKeys":[{"Name":"prod","Provider":"gladia","Key":"secret"}],"LogLevel":{{written}}}""")!;

        Assert.Equal(LogLevel.Info, loaded.LogLevel);
        Assert.Equal("secret", Assert.Single(loaded.ApiKeys).Key);
    }

    [Fact]
    public void AnUnreadableSettingsFileIsPutAsideBeforeTheDefaultsReplaceIt()
    {
        var previous = Log.Sink;
        var recorded = new List<string>();
        Log.Install(new DelegateSink((_, _, message, _) => recorded.Add(message)));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsStore.SettingsPath)!);
            var original = File.Exists(SettingsStore.SettingsPath)
                ? File.ReadAllText(SettingsStore.SettingsPath)
                : null;
            File.WriteAllText(SettingsStore.SettingsPath, """{"ApiKeys":[,,,"broken""");
            try
            {
                var loaded = SettingsStore.Load();

                Assert.Empty(loaded.ApiKeys); // defaults, as before
                Assert.True(File.Exists(SettingsStore.SalvagedPath));
                Assert.Contains("broken", File.ReadAllText(SettingsStore.SalvagedPath));
                Assert.Contains(recorded, m => m.Contains(SettingsStore.SalvagedPath));
            }
            finally
            {
                File.Delete(SettingsStore.SalvagedPath);
                if (original is null)
                    File.Delete(SettingsStore.SettingsPath);
                else
                    File.WriteAllText(SettingsStore.SettingsPath, original);
            }
        }
        finally
        {
            Log.Install(previous);
        }
    }

    private sealed class DelegateSink(Action<LogLevel, string, string, Exception?> write) : ILogSink
    {
        public void Write(LogLevel level, string category, string message, Exception? error) =>
            write(level, category, message, error);
    }

    [Theory]
    [InlineData("\"Debug\"", LogLevel.Debug)]
    [InlineData("\"warning\"", LogLevel.Warning)]
    [InlineData("\"ERROR\"", LogLevel.Error)]
    public void TheFourNamesAreRead(string written, LogLevel expected)
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>($$"""{"LogLevel":{{written}}}""")!;

        Assert.Equal(expected, loaded.LogLevel);
    }
}

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

    [Fact]
    public void OldFilesAreNotKeptForever()
    {
        var target = Target(new AppSettings(), "/logs");

        Assert.Equal(LogSetup.RetentionDays, target.MaxArchiveDays);
    }

    [Fact]
    public void TheRetentionPromiseIsNotUndercutByAFileCount()
    {
        foreach (var megabytes in new[] { 1, 10, 100 })
        {
            var target = Target(new AppSettings { LogMaxFileSizeMb = megabytes }, "/logs");

            Assert.True(target.MaxArchiveFiles > 0);
            Assert.True(
                (long)target.MaxArchiveFiles * megabytes <= LogSetup.DiskBudgetMb,
                $"{megabytes} MB × {target.MaxArchiveFiles} files exceeds the disk budget");

            Assert.True(
                target.MaxArchiveFiles >= 20,
                $"{target.MaxArchiveFiles} archives would undercut the promised {LogSetup.RetentionDays} days");
        }
    }

    [Fact]
    public void TheArchiveNameCarriesNoPlaceholderTheLoggerWillNotSubstitute()
    {
        var target = Target(new AppSettings(), "/logs");

        Assert.DoesNotContain("{#}", target.ArchiveFileName.ToString());
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
            Assert.Contains(files, f => Path.GetFileName(f) == $"kanal-{DateTime.Now:yyyy-MM-dd}.log");
        }
        finally
        {
            Cleanup(directory);
        }
    }

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
            Assert.Contains("the cause", written);
            Assert.Contains("test", written);
        }
        finally
        {
            Cleanup(directory);
        }
    }

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

    [Fact]
    public void ChangingTheLevelKeepsTheLinesAlreadyWritten()
    {
        var directory = TempDirectory();
        try
        {
            using var scope = LogSetup.InstallFor(directory, new AppSettings { LogLevel = LogLevel.Info });
            var before = NLog.LogManager.Configuration!.FindTargetByName(LogSetup.TargetName);

            for (var i = 0; i < 500; i++)
                Log.Info("test", $"before {i:D4}");

            LogSetup.ApplyTo(directory, new AppSettings { LogLevel = LogLevel.Debug, LogMaxFileSizeMb = 7 });
            Log.Debug("test", "after the change");
            scope.Flush();

            Assert.Same(before, NLog.LogManager.Configuration!.FindTargetByName(LogSetup.TargetName));

            var written = string.Join("\n", Directory.GetFiles(directory).Select(File.ReadAllText));
            for (var i = 0; i < 500; i++)
                Assert.Contains($"before {i:D4}", written);
            Assert.Contains("after the change", written);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void LinesWrittenAfterTheFinalFlushStillLand()
    {
        var directory = TempDirectory();
        try
        {
            using var scope = LogSetup.InstallFor(directory, new AppSettings());

            Log.Info("test", "on the way out");
            LogSetup.Flush();
            Log.Error("test", "and the reason why", new InvalidOperationException("teardown"));
            scope.Flush();

            var written = string.Join("\n", Directory.GetFiles(directory).Select(File.ReadAllText));
            Assert.Contains("and the reason why", written);
            Assert.Contains("teardown", written);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void AnEnormousExceptionIsCappedRatherThanWrittenWhole()
    {
        var directory = TempDirectory();
        try
        {
            using var scope = LogSetup.InstallFor(directory, new AppSettings());
            var body = new string('x', 200_000);

            Log.Error("test", "the gateway refused", new InvalidOperationException(body));
            scope.Flush();

            var written = string.Join("\n", Directory.GetFiles(directory).Select(File.ReadAllText));
            Assert.Contains("the gateway refused", written);
            Assert.Contains("more characters]", written); // said out loud, not silently dropped
            Assert.True(written.Length < 20_000, $"one line wrote {written.Length} characters");
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void AFolderThatCannotBeWrittenIsReportedRatherThanSilent()
    {
        var occupied = Path.Combine(Path.GetTempPath(), "kanal-log-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(occupied, "a file where the folder needs to be");
        var previousSink = Log.Sink;
        var previousConfig = NLog.LogManager.Configuration;
        try
        {
            LogSetup.ApplyTo(occupied, new AppSettings());

            Assert.False(LogSetup.Writable);
            Assert.False(string.IsNullOrWhiteSpace(LogSetup.FailureReason));
            Log.Info("test", "nowhere to go, but nothing throws");
        }
        finally
        {
            File.Delete(occupied);
            Log.Install(previousSink);
            NLog.LogManager.Configuration = previousConfig ?? new NLog.Config.LoggingConfiguration();
            var fresh = TempDirectory();
            LogSetup.ApplyTo(fresh, new AppSettings());
            Cleanup(fresh);
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
