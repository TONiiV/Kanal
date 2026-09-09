using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;

namespace Kanal.Core.UnitTests;

/// <summary>
/// The macOS bundle is assembled by MSBuild, and most of what can go wrong in it is invisible until
/// the app is signed and launched on a real Mac: a missing usage-description string makes the system
/// deny the microphone silently, and a missing entitlement crashes the JIT only under the hardened
/// runtime. Neither reproduces under <c>dotnet run</c>, which inherits the terminal's permissions and
/// no runtime hardening.
///
/// So the staging step is split. <c>StageMacAppLayout</c> writes the directory structure, Info.plist
/// and entitlements — pure file operations, no Apple tooling, no publish output — which makes it
/// runnable and assertable on any OS including the Linux CI job. <c>StageMacApp</c> then adds the
/// self-contained binaries on top. Only the layout half is covered here; the signing half depends on
/// a certificate and Apple's notary service and is exercised by the tag-triggered CI run.
/// </summary>
public class InstallerLayoutTests
{
    const string ExpectedVersion = "1.2.3";

    static readonly Lazy<StagedApp> Staged = new(StageOnce, isThreadSafe: true);

    // Staging shells out to MSBuild, so it is done once and shared. xUnit gives each test class one
    // instance per test by default, which would otherwise re-run the build for every assertion.
    static StagedApp StageOnce() => StageInto(Path.Combine(
        Path.GetTempPath(), "kanal-installer-tests", Guid.NewGuid().ToString("n")));

    static StagedApp StageInto(string stageDir)
    {
        var repoRoot = FindRepoRoot();

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(Path.Combine("installers", "Kanal.Installers.csproj"));
        psi.ArgumentList.Add("-t:StageMacAppLayout");
        psi.ArgumentList.Add($"-p:MacAppStageDir={stageDir}");
        psi.ArgumentList.Add($"-p:Version={ExpectedVersion}");
        psi.ArgumentList.Add("-nologo");
        psi.ArgumentList.Add("-v:quiet");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start dotnet");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"StageMacAppLayout failed (exit {proc.ExitCode}):\n{stdout}\n{stderr}");

        return new StagedApp(Path.Combine(stageDir, "Kanal.app"), repoRoot);
    }

    static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Kanal.slnx")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new InvalidOperationException("Kanal.slnx not found above the test assembly");
    }

    sealed record StagedApp(string AppDir, string RepoRoot)
    {
        public string Contents => Path.Combine(AppDir, "Contents");
        public string InfoPlist => Path.Combine(Contents, "Info.plist");
        public string Entitlements => Path.Combine(RepoRoot, "installers", "macos", "Kanal.entitlements");
    }

    /// <summary>
    /// Reads a plist's top-level dict into a dictionary. A plist dict is a flat sequence of key
    /// elements each followed by its value element, so pairing is positional rather than nested.
    /// Bare &lt;true/&gt;/&lt;false/&gt; values become "true"/"false".
    /// </summary>
    static Dictionary<string, string> ReadPlist(string path)
    {
        // A real plist carries a DOCTYPE pointing at Apple's DTD. XDocument.Load prohibits DTDs by
        // default and would throw before parsing a single key, so read through a reader that skips
        // it — without fetching anything over the network.
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });

        var dict = XDocument.Load(reader).Root?.Element("dict")
            ?? throw new InvalidOperationException($"no top-level <dict> in {path}");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var children = dict.Elements().ToList();
        for (var i = 0; i + 1 < children.Count; i += 2)
        {
            if (children[i].Name != "key") continue;
            var value = children[i + 1];
            result[children[i].Value] = value.Name == "true" || value.Name == "false"
                ? value.Name.LocalName
                : value.Value;
        }

        return result;
    }

    [Fact]
    public void BundleHasTheThreeDirectoriesLaunchServicesLooksFor()
    {
        var app = Staged.Value;

        Assert.True(Directory.Exists(Path.Combine(app.Contents, "MacOS")), "Contents/MacOS missing");
        Assert.True(Directory.Exists(Path.Combine(app.Contents, "Resources")), "Contents/Resources missing");
        Assert.True(File.Exists(app.InfoPlist), "Contents/Info.plist missing");
    }

    [Fact]
    public void IconIsStagedUnderResources()
    {
        // CFBundleIconFile names it without the extension, so the file itself must be kanal.icns.
        Assert.True(
            File.Exists(Path.Combine(Staged.Value.Contents, "Resources", "kanal.icns")),
            "Resources/kanal.icns missing — Finder and the Dock read the bundle icon from here, "
            + "not from the window icon set in MainWindow.axaml");
    }

    [Fact]
    public void CFBundleExecutableMatchesTheApphostThatWillBeStaged()
    {
        // A mismatch here is a bundle that cannot launch at all: LaunchServices resolves the
        // executable by this exact name under Contents/MacOS.
        Assert.Equal("Kanal.Host", ReadPlist(Staged.Value.InfoPlist)["CFBundleExecutable"]);
    }

    [Fact]
    public void BundleIdentifierIsStableAcrossVersions()
    {
        Assert.Equal("io.github.toniiv.kanal", ReadPlist(Staged.Value.InfoPlist)["CFBundleIdentifier"]);
    }

    [Fact]
    public void VersionFlowsFromTheBuildIntoBothVersionKeys()
    {
        var plist = ReadPlist(Staged.Value.InfoPlist);

        Assert.Equal(ExpectedVersion, plist["CFBundleShortVersionString"]);
        Assert.Equal(ExpectedVersion, plist["CFBundleVersion"]);
    }

    [Fact]
    public void MicrophoneUsageDescriptionIsPresentAndNotEmpty()
    {
        // Without this key macOS denies microphone access with no prompt and no error — the app
        // simply captures silence. It cannot be caught by `dotnet run`, only by a real bundle.
        var plist = ReadPlist(Staged.Value.InfoPlist);

        Assert.True(
            plist.TryGetValue("NSMicrophoneUsageDescription", out var reason),
            "NSMicrophoneUsageDescription missing — the host would capture silence with no prompt");
        Assert.False(string.IsNullOrWhiteSpace(reason), "NSMicrophoneUsageDescription is empty");
    }

    [Fact]
    public void BundleDeclaresRetinaSupportAndAMinimumSystemVersion()
    {
        var plist = ReadPlist(Staged.Value.InfoPlist);

        Assert.Equal("true", plist["NSHighResolutionCapable"]);
        Assert.Equal("12.0", plist["LSMinimumSystemVersion"]);
    }

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("de")]
    [InlineData("pl")]
    public void MicrophonePromptIsLocalisedForEveryLanguageTheAppSpeaks(string language)
    {
        // The prompt is the one piece of Kanal's text macOS renders rather than the app, and it is
        // asked in a room whose premise is that nobody shares a language. InfoPlist.strings is the
        // only filename the system reads it from — a file staged under any other name is silently
        // ignored and the operator gets the English fallback.
        var strings = Path.Combine(
            Staged.Value.Contents, "Resources", $"{language}.lproj", "InfoPlist.strings");

        Assert.True(File.Exists(strings), $"{language}.lproj/InfoPlist.strings missing");
        Assert.Contains("NSMicrophoneUsageDescription", File.ReadAllText(strings));
    }

    [Fact]
    public void StagingClearsWhatAnEarlierBuildLeftBehind()
    {
        // Staging copies into the bundle, it does not sync it. Without an explicit wipe a file from
        // a previous build survives into the next dmg — including binaries this run never signed.
        // A fresh CI runner never sees it: it is the maintainer packaging twice who ships it.
        var stageDir = Path.Combine(
            Path.GetTempPath(), "kanal-installer-tests", Guid.NewGuid().ToString("n"));

        var staged = StageInto(stageDir);
        var leftover = Path.Combine(staged.Contents, "MacOS", "libghost.dylib");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        File.WriteAllText(leftover, "from the build before");

        StageInto(stageDir);

        Assert.False(File.Exists(leftover), "a file from the previous build survived staging");
    }

    [Fact]
    public void EntitlementsAllowJitAndAudioInput()
    {
        // The hardened runtime is mandatory for notarisation and breaks both of these by default:
        // without allow-jit the .NET JIT cannot map executable pages and the app crashes at startup;
        // without device.audio-input the microphone is refused even with the Info.plist string
        // present. Neither failure reproduces in an unsigned build.
        var entitlements = ReadPlist(Staged.Value.Entitlements);

        Assert.Equal("true", entitlements["com.apple.security.cs.allow-jit"]);
        Assert.Equal("true", entitlements["com.apple.security.device.audio-input"]);
    }
}
