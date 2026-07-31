# Installers — design

**Date:** 2026-08-01 · **Branch:** `feat/installers` · **Status:** approved, not yet implemented

Ship Kanal to an operator's laptop as a double-clickable install, on macOS and Windows, without
asking them to install a .NET runtime or open a terminal.

## Goals

- One `installers/Kanal.Installers.csproj` that produces a signed, notarised `.dmg` on macOS and an
  `.msi` on Windows.
- Self-contained publish — the operator installs nothing else.
- Signing is an opt-in switch, not a branch. An unsigned build must always succeed, so that forks,
  fresh clones and PR CI can exercise the same pipeline.

## Non-goals

- **Homebrew.** See the decision record below.
- In-app auto-update. Separate concern, separate PR.
- Linux packaging. No operator runs Linux; the CI test job does, and it needs no package.
- Windows code signing. No certificate exists yet; the hook is stubbed, not wired.

## Decision: `.dmg`, not Homebrew

A Homebrew cask is not an alternative to a `.dmg` — a cask's body is "download this `.dmg`, copy the
`.app` to /Applications". It is a layer on top, so it can only ever be additive.

The one substantive thing a cask adds is that `brew install --cask` strips the quarantine attribute,
letting an **unsigned** app bypass "cannot verify the developer". With a Developer ID certificate and
notarisation that advantage is worth nothing — a stapled `.dmg` opens on double-click.

Against it: the audience is meeting operators, not developers. Requiring Homebrew (Xcode Command Line
Tools plus a `curl` pasted into a terminal) as a prerequisite for a meeting tool is more friction than
dragging one icon. homebrew-core does not accept internal tools, so it would mean maintaining a
private tap and hand-updating `version` + `sha256` on every release, turning `brew install kanal` into
`brew tap <owner>/kanal && brew install --cask kanal` — longer than a download link.

**Revisit if the Developer ID assumption fails.** If only a free Apple ID is available, notarisation
is impossible, the unsigned-app friction returns, and a cask becomes genuinely useful.

## Artefact matrix

| Host OS | RID | Artefact |
|---|---|---|
| `macos-latest` (arm64) | `osx-arm64` | `Kanal-<version>-osx-arm64.dmg` |
| `windows-latest` | `win-x64` | `Kanal-<version>-win-x64.msi` |

**A single machine cannot produce both.** `codesign`/`notarytool` exist only on macOS; MSI authoring
needs the Windows toolchain. The csproj therefore builds *for the host OS it runs on*; CI fans out
over a two-entry matrix and a third job collects the artefacts into one GitHub Release.

Intel Macs and Windows-on-ARM are out of scope (confirmed with the operator).

## Project structure

`installers/Kanal.Installers.csproj` compiles no code. It is an MSBuild orchestration project
(`<TargetFramework>` present only to keep the SDK happy; no `Compile` items). Targets:

| Target | Platform | Signing needed |
|---|---|---|
| `PublishHost` | any | no |
| `StageMacAppLayout` | any — **pure file operations** | no |
| `StageMacApp` | any | no |
| `SignMacApp` | macOS | yes |
| `NotarizeMacApp` | macOS | yes |
| `PackDmg` | macOS | no |
| `PackMsi` | Windows only | no (unsigned until a certificate exists) |
| `PackInstaller` | dispatches on host OS | — |

Staging is split in two, and the seam is what makes any of this testable. `StageMacAppLayout` writes
the directory structure, the icon and Info.plist — pure file manipulation, no Apple tooling, and
crucially **no dependency on the publish output**, so it runs in seconds on any OS. `StageMacApp`
then copies the self-contained binaries on top. Tests drive the layout target alone; if they drove
the full one, every test run would trigger a multi-minute self-contained publish and nobody would
run them.

`PublishHost` shells out to `dotnet publish` rather than invoking the `Publish` target through the
`MSBuild` task. Publishing for a RID the host project was not restored for fails with `NETSDK1047`,
and restore does not compose reliably inside an existing build.

Signing is gated on `-p:SignBuild=true`. Without it, `SignMacApp` and `NotarizeMacApp` no-op and the
build still yields a working (unsigned) `.app` and `.dmg`.

## macOS bundle layout

```
Kanal.app/Contents/
├── Info.plist
├── MacOS/Kanal.Host            ← self-contained apphost, +x
├── Resources/kanal.icns        ← from design/kanal.icns
└── _CodeSignature/             ← written by codesign
```

`Info.plist` keys that are load-bearing:

| Key | Value | Why |
|---|---|---|
| `CFBundleIdentifier` | `io.github.toniiv.kanal` | stable identity across versions |
| `CFBundleExecutable` | `Kanal.Host` | must match the apphost filename exactly |
| `CFBundleIconFile` | `kanal` | `.icns` extension omitted, by convention |
| `CFBundleShortVersionString` / `CFBundleVersion` | from the tag | shown in Finder / About |
| `NSMicrophoneUsageDescription` | operator-facing sentence | **without it macOS silently denies mic access** |
| `LSMinimumSystemVersion` | `12.0` | matches the .NET 9 macOS floor |
| `NSHighResolutionCapable` | `true` | otherwise the window renders blurry on Retina |

`NSMicrophoneUsageDescription` cannot be caught by `dotnet run` — an unbundled binary inherits the
terminal's permissions. It only fails once the app is a bundle, which is exactly what this PR
introduces, so it is asserted in a test rather than left to manual discovery.

### Entitlements

Notarisation requires the hardened runtime, which breaks two things Kanal needs:

```xml
<key>com.apple.security.cs.allow-jit</key><true/>
<key>com.apple.security.device.audio-input</key><true/>
```

Without `allow-jit` the .NET JIT cannot map executable pages and the app crashes at startup under the
hardened runtime — it will not reproduce in an unsigned build. `device.audio-input` is the
hardened-runtime counterpart to the Info.plist string; both are required.

### Signing and notarisation order

LLamaSharp ships native libraries (`libllama.dylib`, `libggml*.dylib`) inside the publish output.
Every Mach-O binary must be signed or notarisation rejects the whole submission, so nested binaries
are signed first, the bundle last.

```
codesign (inside-out, --options runtime, --timestamp)
  → zip the .app → notarytool submit --wait → stapler staple THE .APP
  → build the .dmg from the stapled .app
  → notarytool submit the .dmg --wait → stapler staple THE .DMG
```

**Both layers get stapled, and that matters here specifically.** Stapling only the `.dmg` leaves the
`.app` without its own ticket, so the first launch after dragging it to /Applications needs a network
round-trip to Apple. Kanal's whole point is running a meeting on local models with no connectivity —
an app that will not open offline is a broken app. Two tickets means fully offline launch.

Credentials come from an App Store Connect API key (`.p8`), not an Apple ID with an app-specific
password: it is revocable on its own, tied to no individual's account, and unaffected by 2FA.

## Windows MSI

WiX **5.0.2**, pinned in `.config/dotnet-tools.json` and invoked as a local tool from the `PackMsi`
target, over the `win-x64` self-contained publish. A local tool rather than a separate `.wixproj`
keeps the whole chain inside one project file. Per-user install under `LocalAppDataFolder` so no UAC
prompt is needed — an operator setting up a laptop before a meeting should not need admin rights.
Start-menu shortcut, Add/Remove Programs entry, clean uninstall. The several hundred files of a
self-contained publish are harvested by `Files/@Include` rather than enumerated.

**Pinned to 5.0.2 for licensing, not compatibility.** `dotnet tool install wix` takes the newest
version, which is 7.0.0, and that fails the build outright:

```
error WIX7015: You must accept the Open Source Maintenance Fee (OSMF) EULA to use WiX Toolset v7
```

WiX introduced the Open Source Maintenance Fee in v6 — commercial use, which an internal tool is,
requires accepting a paid EULA. 5.0.2 is the last release before it and is unaffected. The schema is
the same (`.../schemas/v4/wxs`), so nothing in `Kanal.wxs` changed for the downgrade. **Do not let a
dependency update bump this past 5.x** without deciding to pay.

**WiX runs on Windows only.** It says so itself on any other host ("All behavior after this point is
undefined"), so `Kanal.wxs` cannot be fully validated on the development Mac — the `windows-latest`
CI job is its only real exercise. Schema validation does run cross-platform though, and it earned
its keep: it caught a `ComponentGroup` nested inside `StandardDirectory`, which is not a legal child.

Exactly one diagnostic does not survive the port, and it is worth knowing about before it wastes
someone's afternoon: `WIX0389: The Directory/@Name attribute's value, 'Kanal', is not a relative
path`, raised against the most ordinary construct in the language. Confirmed to be a platform
artefact — the `windows-latest` run does not raise it. **Off Windows, treat `WIX0389` as noise and
read the errors around it.**

Every path reaches the `.wxs` as an absolute value passed with `-d`. Relative paths there resolve
against the wix process's working directory rather than against the `.wxs` file, which is how the
icon reference initially pointed outside the repository — a failure that only appears on the runner,
since off-Windows the build never gets far enough to look for the file.

**`UpgradeCode` is fixed for the lifetime of the product:** `49907851-1726-470B-A773-9F62E492913F`.
Changing or regenerating it makes new versions install *alongside* old ones instead of upgrading them.

The MSI is unsigned. First run shows a SmartScreen warning that the operator must click through
("More info" → "Run anyway"). `SignBuild` on Windows is a documented no-op until a certificate exists.

## Version numbers

The tag is the source of truth: `v0.3.1` → `0.3.1`. `workflow_dispatch` and PR builds use `0.0.0`.

MSI `ProductVersion` constrains the tag scheme: **major ≤ 255, minor ≤ 255, build ≤ 65535**, and a
fourth component is silently ignored for upgrade comparisons. Three-component semver tags stay well
inside this. Pre-release suffixes (`v0.3.1-rc1`) are stripped for the MSI and kept for the release
name.

## CI

`ci.yml` is untouched — it is the fast ubuntu feedback loop (tests + the `web/` ↔ `docs/`
byte-identity invariant) and packaging must not slow it down.

New `.github/workflows/release.yml`, two gears:

| Trigger | Signing | Publishes |
|---|---|---|
| `push: tags: ['v*']` | yes | GitHub Release |
| `pull_request` (paths-filtered) | no | artefacts only |
| `workflow_dispatch` | no | artefacts only |

The PR gear is filtered to `installers/**`, `.github/workflows/release.yml` and `src/**/*.csproj`, so
ordinary PRs are unaffected while changes that can break packaging still get exercised. This also
handles fork PRs, which cannot read secrets and therefore *must* be able to build unsigned.

Secrets (macOS only): `MACOS_CERT_P12`, `MACOS_CERT_PWD`, `MACOS_SIGN_IDENTITY`, `NOTARY_KEY_P8`,
`NOTARY_KEY_ID`, `NOTARY_ISSUER_ID`.

Importing the certificate on a runner must end with `security set-key-partition-list`. Omitting it
makes `codesign` raise a GUI authorisation prompt, which on a headless runner is a silent hang until
the job times out, with nothing useful in the log.

## Testing

Per CLAUDE.md, behaviour lands with tests. What is genuinely testable is the staging layer, and the
target split exists to maximise that surface.

`tests/Kanal.Tests/InstallerLayoutTests.cs` — runs `StageMacAppLayout` into a temp directory and
asserts:

- `Contents/MacOS`, `Contents/Resources`, `Contents/Info.plist` exist
- the apphost is present at `Contents/MacOS/Kanal.Host` and is executable
- `kanal.icns` landed in `Contents/Resources`
- `Info.plist` parses and contains every key in the table above, with `CFBundleExecutable` matching
  the actual apphost filename
- the entitlements file contains `allow-jit` and `device.audio-input`

These are file assertions with no Apple tooling involved, so they run in the existing ubuntu CI job.

Signing and notarisation are **not unit-testable** — they depend on a certificate and Apple's
service. Coverage for them is the tag-triggered CI run plus one local signed build.

## Verified

Actually executed on the development Mac, not assumed:

- `PackDmg` unsigned end to end in ~25 s, producing an 88 MB `Kanal-0.0.0-osx-arm64.dmg`.
- The mounted image contains `Kanal.app` plus the `/Applications` symlink that makes the window a
  drag-and-drop target.
- `Contents/MacOS/Kanal.Host` is a `Mach-O 64-bit executable arm64`, and `__VERSION__` and the
  microphone string are substituted correctly in the staged Info.plist.
- The bundle carries **36 dylibs**, which is the concrete justification for `sign.sh` discovering
  Mach-O binaries with `file(1)` rather than trusting a list of extensions.
- Building with only the .NET 10 SDK works — the `net9.0` runtime pack restores from NuGet.
- All 161 tests pass, and `dotnet build Kanal.slnx` stays clean with the new project in the solution.

## Known unverified

Stated plainly rather than discovered at release time:

1. **The certificate may be the wrong type.** The only identity in the local keychain is
   `Apple Development: Yandong Wang (LM9F46L6Q8)`. Developer ID distribution needs a
   `Developer ID Application:` certificate, which a free Apple ID cannot issue. If the account is a
   paid Developer Program membership this is a five-minute fix on developer.apple.com; if not, the
   Homebrew decision above must be revisited. `sign.sh` refuses to start on a non-Developer-ID
   identity, because signing succeeds and notarisation then rejects it — a slow way to find out.
2. **The signing/notarisation chain is unrun code** until someone executes it. Apple only reports
   failures after submission, and getting a clean pass typically takes two or three rounds. Planned
   mitigation: a local `-p:SignBuild=true` run, which needs no GitHub secrets.
3. **The MSI has never been produced.** Schema validation passes off-Windows, but the actual build —
   the `Files/@Include` harvest over several hundred files, `Scope="perUser"`, the shortcut
   component, and whether `WIX0389` really was a platform artefact — is only ever exercised by the
   `windows-latest` job.
4. **The release job is unrun.** It only fires on a tag, so artefact collection and `gh release
   create` stay untested until the first real version tag.
