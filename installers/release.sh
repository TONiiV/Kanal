#!/usr/bin/env bash
# Builds a release package on this machine: a signed, notarised dmg on a Mac, an msi on Windows.
# Usage: installers/release.sh <version> [--unsigned]      e.g. installers/release.sh 0.1.0-alpha.2
# --unsigned is for the CI smoke build; a macOS package without it is refused by Gatekeeper.
set -euo pipefail

full="${1:?usage: installers/release.sh <version> [--unsigned], e.g. 0.1.0-alpha.2}"
unsigned="${2:-}"
[[ -z "$unsigned" || "$unsigned" == --unsigned ]] || { echo "unknown option: $unsigned" >&2; exit 1; }
full="${full#v}"
[[ "$full" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]] || { echo "not a version: $full" >&2; exit 1; }
# MSI ProductVersion and CFBundleShortVersionString take only the numeric part
version="${full%%-*}"

cd "$(dirname "$0")/.."

[[ -z "$(git status --porcelain)" ]] || { echo "the working tree has changes; commit or stash them first" >&2; exit 1; }

case "$(uname -s)" in
  Darwin)
    rid=osx-arm64 sign=true
    if [[ "$unsigned" == --unsigned ]]; then
      sign=false
    else
      missing=
      for name in MACOS_SIGN_IDENTITY NOTARY_KEY_PATH NOTARY_KEY_ID NOTARY_ISSUER_ID; do
        [[ -n "${!name:-}" ]] || missing="$missing $name"
      done
      [[ -z "$missing" ]] || { echo "unset:$missing (see docs/superpowers/specs/2026-08-01-installers-design.md)" >&2; exit 1; }
    fi
    ;;
  MINGW*|MSYS*|CYGWIN*) rid=win-x64 sign=false ;;
  *) echo "release packages are built on macOS or Windows only" >&2; exit 1 ;;
esac

echo "Kanal $full ($rid) from $(git rev-parse --short HEAD) on $(git rev-parse --abbrev-ref HEAD)"

dotnet build installers/Kanal.Installers.csproj -t:PackInstaller \
  -p:Version="$version" -p:FullVersion="$full" -p:TargetRid="$rid" -p:SignBuild="$sign"

for f in artifacts/Kanal-"$full"-"$rid".dmg artifacts/Kanal-"$full"-"$rid".msi; do
  [[ -e "$f" ]] || continue
  ls -lh "$f"
  if command -v shasum > /dev/null; then shasum -a 256 "$f"; else sha256sum "$f"; fi
  exit 0
done
echo "no package in artifacts/" >&2
exit 1
