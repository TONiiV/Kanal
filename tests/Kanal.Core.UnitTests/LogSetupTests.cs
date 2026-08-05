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

    /// <summary>
    /// The doc comment invites hand-editing, and "Warn" is what a hand types for Warning. Anything
    /// but the four exact names threw, <see cref="SettingsStore.Load"/> caught it and started
    /// fresh, and the next Save wrote those defaults over the file — so one typo in a level cost
    /// the operator their stored API key, folders and language, with nothing on screen.
    /// </summary>
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

    /// <summary>
    /// The level converter fixed one field; every other field can still make the whole file
    /// unreadable, and starting from defaults means the next Save writes over a stored API key.
    /// The file is copied aside first, so the key is recoverable rather than gone.
    /// </summary>
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

    /// <summary>
    /// And it does not stay there. The copy is the stored API key in plaintext, beside the file it
    /// was copied from, and nothing ever removed it — one typo in a level left a second copy of the
    /// key on disk for the life of the install. The next Save that succeeds is the moment it has
    /// done its job: whatever was in it has been re-entered by then or is not coming back.
    /// </summary>
    [Fact]
    public void TheSalvagedCopyGoesOnTheNextSuccessfulSave()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsStore.SettingsPath)!);
        var original = File.Exists(SettingsStore.SettingsPath)
            ? File.ReadAllText(SettingsStore.SettingsPath)
            : null;
        File.WriteAllText(SettingsStore.SalvagedPath, """{"ApiKeys":[,,,"broken""");
        try
        {
            SettingsStore.Save(new AppSettings());

            Assert.False(
                File.Exists(SettingsStore.SalvagedPath),
                "the salvaged copy of the key outlived the settings it was salvaged from");
        }
        finally
        {
            if (File.Exists(SettingsStore.SalvagedPath))
                File.Delete(SettingsStore.SalvagedPath);
            if (original is null)
                File.Delete(SettingsStore.SettingsPath);
            else
                File.WriteAllText(SettingsStore.SettingsPath, original);
        }
    }

    private sealed class DelegateSink(Action<LogLevel, string, string, Exception?> write) : ILogSink
    {
        public void Write(LogLevel level, string category, string message, Exception? error) =>
            write(level, category, message, error);
    }

    /// <summary>The four names it does understand still round-trip, in any casing.</summary>
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

        Assert.Equal(LogSetup.RetentionDays, target.MaxArchiveDays);
    }

    /// <summary>
    /// NLog's file-count cap counts every file matching the pattern, across dates — not per day, as
    /// its name suggests. Set alongside a day-based retention it wins, and a busy meeting at the
    /// smallest rollover size silently cuts the two weeks the settings panel promises down to hours.
    /// The promise on screen is the one that has to hold.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(SettingsStore.MaxLogMaxFileSizeMb)]
    public void TheRetentionPromiseIsNotUndercutByAFileCount(int megabytes)
    {
        var target = Target(new AppSettings { LogMaxFileSizeMb = megabytes }, "/logs");

        // Bounded — dropping the count entirely let one loud day write 65 MB with nothing yet
        // old enough to delete…
        Assert.True(target.MaxArchiveFiles > 0);

        // …bounded by the budget wherever the budget and the floor can both be met, and by the
        // floor where they cannot. Past ~102 MB a file the two are in conflict and retention wins:
        // a count below the floor is the bug this whole guard exists to catch, so the budget is
        // documented as a target rather than quietly cutting the promised fortnight to two files.
        var bound = Math.Max(LogSetup.DiskBudgetMb, (long)LogSetup.MinArchives * megabytes);
        Assert.True(
            (long)target.MaxArchiveFiles * megabytes <= bound,
            $"{megabytes} MB × {target.MaxArchiveFiles} files exceeds {bound} MB");

        // The floor itself: far enough above a fortnight of ordinary meetings that age, not count,
        // is what does the deleting. A handful of rollovers a day for 14 days is the shape to clear.
        Assert.True(
            target.MaxArchiveFiles >= LogSetup.MinArchives,
            $"{target.MaxArchiveFiles} archives would undercut the promised {LogSetup.RetentionDays} days");
    }

    /// <summary>
    /// <c>Program.Main</c> applies the defaults and then the stored settings, so the second apply
    /// is always the in-place one — the operator's configured size reaches the target through that
    /// branch on every launch, not only on a mid-meeting change. A branch that updated the
    /// rollover size but left the count derived from the default cut the folder to 204 archives at
    /// 1 MB where 2048 were intended: hours of retention against the fortnight on screen.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(SettingsStore.MaxLogMaxFileSizeMb)]
    public void AnInPlaceApplyCarriesBothTheSizeAndTheCountItImplies(int megabytes)
    {
        var directory = TempDirectory();
        try
        {
            var settings = new AppSettings { LogMaxFileSizeMb = megabytes };
            using var scope = LogSetup.InstallFor(directory, new AppSettings());

            LogSetup.ApplyTo(directory, settings);

            var live = Assert.IsType<FileTarget>(
                NLog.LogManager.Configuration!.FindTargetByName(LogSetup.TargetName));
            // the freshly built configuration is the oracle: the two branches must not disagree
            var fresh = Target(settings, directory);
            Assert.Equal(fresh.ArchiveAboveSize, live.ArchiveAboveSize);
            Assert.Equal(fresh.MaxArchiveFiles, live.MaxArchiveFiles);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    /// <summary>
    /// `{#}` is NLog 5's archive placeholder. NLog 6 strips it instead of substituting it, so a
    /// name written with one describes files that never appear.
    /// </summary>
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

    /// <summary>
    /// Changing the level mid-meeting must not cost the lines already written. Handing NLog a
    /// second <c>FileTarget</c> over the same open file did exactly that: the new target opened at
    /// a stale offset and overwrote a buffer's worth of what the old one had flushed — thousands of
    /// lines, at the one moment the operator turned Debug on because something was going wrong.
    /// </summary>
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

            // the same file, still open by the same target — not a second handle at a stale offset
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

    /// <summary>
    /// Flushing is not closing. The host flushes on its way out, but a capture loop or a session
    /// still unwinding writes after that — and those lines are the ones explaining why it is on its
    /// way out. Shutting NLog down there swallowed them silently.
    /// </summary>
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

    /// <summary>
    /// Providers and the gateway put whole HTTP response bodies into exception messages — a
    /// captive portal's error page, verbatim, per failure — and the snapshot retry runs every 15
    /// seconds. One repeated failure filled the file on its own; a line has a ceiling now.
    /// </summary>
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

    /// <summary>
    /// A folder that cannot be opened leaves NLog silently dropping every line — it defers file
    /// creation and swallows the failure — while the panel goes on promising a file a day.
    /// </summary>
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
            // and the facade still works — a broken folder must not leave the process sinkless
            Log.Info("test", "nowhere to go, but nothing throws");
        }
        finally
        {
            File.Delete(occupied);
            Log.Install(previousSink);
            NLog.LogManager.Configuration = previousConfig ?? new NLog.Config.LoggingConfiguration();
            // put the flag back so the next test does not inherit a failed state
            var fresh = TempDirectory();
            LogSetup.ApplyTo(fresh, new AppSettings());
            Cleanup(fresh);
        }
    }

    /// <summary>
    /// The folder that worked on the second try leaves no reason behind. A reason kept beside
    /// <see cref="LogSetup.Writable"/> being true is a contradiction waiting for the next reader of
    /// either one.
    /// </summary>
    [Fact]
    public void AFolderThatOpensClearsTheEarlierFailure()
    {
        var occupied = Path.Combine(Path.GetTempPath(), "kanal-log-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(occupied, "a file where the folder needs to be");
        var directory = TempDirectory();
        try
        {
            LogSetup.ApplyTo(occupied, new AppSettings());
            Assert.False(LogSetup.Writable);

            LogSetup.ApplyTo(directory, new AppSettings());

            Assert.True(LogSetup.Writable);
            Assert.Null(LogSetup.FailureReason);
        }
        finally
        {
            File.Delete(occupied);
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
