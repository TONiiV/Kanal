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
| `StageMacApp` | any — **pure file operations** | no |
| `SignMacApp` | macOS | yes |
| `NotarizeMacApp` | macOS | yes |
| `PackDmg` | macOS | no |
| `PackMsi` | Windows | no (stub for later) |

The `StageMacApp` / `SignMacApp` split is deliberate: assembling a `.app` is copying files into a
directory layout and writing two XML files, which works on any OS and is therefore **testable in the
Linux CI job**. Only the steps that shell out to Apple tooling are platform-bound and untestable.

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

WiX (`.wixproj`, an MSBuild SDK project) over the `win-x64` self-contained publish. Per-user install
by default so no UAC prompt is needed. Start-menu shortcut, Add/Remove Programs entry, clean
uninstall.

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

`tests/Kanal.Tests/InstallerLayoutTests.cs` — runs `StageMacApp` into a temp directory and asserts:

- `Contents/MacOS`, `Contents/Resources`, `Contents/Info.plist` exist
- the apphost is present at `Contents/MacOS/Kanal.Host` and is executable
- `kanal.icns` landed in `Contents/Resources`
- `Info.plist` parses and contains every key in the table above, with `CFBundleExecutable` matching
  the actual apphost filename
- the entitlements file contains `allow-jit` and `device.audio-input`

These are file assertions with no Apple tooling involved, so they run in the existing ubuntu CI job.

Signing and notarisation are **not unit-testable** — they depend on a certificate and Apple's
service. Coverage for them is the tag-triggered CI run plus one local signed build.

## Known unverified

Stated plainly rather than discovered at release time:

1. **The certificate may be the wrong type.** The only identity in the local keychain is
   `Apple Development: Yandong Wang (LM9F46L6Q8)`. Developer ID distribution needs a
   `Developer ID Application:` certificate, which a free Apple ID cannot issue. If the account is a
   paid Developer Program membership this is a five-minute fix on developer.apple.com; if not, the
   Homebrew decision above must be revisited.
2. **The signing/notarisation chain is unrun code** until someone executes it. Apple only reports
   failures after submission, and getting a clean pass typically takes two or three rounds. Planned
   mitigation: a local `-p:SignBuild=true` run, which needs no GitHub secrets.
3. **This machine has only the .NET 10 SDK** (10.0.101), no 9.0. Self-contained `net9.0` publish
   pulls `Microsoft.NETCore.App.Runtime.osx-arm64` 9.0.x from NuGet; expected to work, unconfirmed.
4. **WiX major version** is pinned at implementation time against what actually restores.
