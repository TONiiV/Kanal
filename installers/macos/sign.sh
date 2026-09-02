#!/usr/bin/env bash
#
# Sign a staged Kanal.app for Developer ID distribution.
#
# Signing is inside-out: every nested Mach-O binary first, the bundle itself last. Apple deprecated
# --deep for exactly this reason - it signs nested code with the bundle's own entitlements and gets
# the order wrong often enough to produce bundles that pass codesign and fail notarisation.
#
# LLamaSharp ships libllama.dylib and several libggml*.dylib inside the publish output, the .NET
# runtime adds its own native libraries plus helper executables such as createdump, and a
# self-contained publish puts ~200 managed .dll assemblies beside them. Everything in that directory
# counts as nested code to codesign, so everything in it is signed - see the note below.
#
# Usage: sign.sh <path-to-Kanal.app> <path-to-entitlements> <signing-identity>

set -euo pipefail

APP="${1:?usage: sign.sh <app> <entitlements> <identity>}"
ENTITLEMENTS="${2:?missing entitlements path}"
IDENTITY="${3:?missing signing identity}"

if [[ ! -d "$APP" ]]; then
    echo "sign.sh: no bundle at $APP" >&2
    exit 1
fi

# An 'Apple Development' certificate signs fine and is then rejected by the notary service, which is
# a slow and confusing way to find out. Fail here instead.
if [[ "$IDENTITY" != Developer\ ID\ Application* ]]; then
    echo "sign.sh: '$IDENTITY' is not a Developer ID Application identity." >&2
    echo "         Notarisation requires one. 'Apple Development' and 'Apple Distribution'" >&2
    echo "         certificates cannot be used for Developer ID distribution." >&2
    exit 1
fi

echo "sign.sh: signing nested binaries in $APP"

# Everything under Contents/MacOS except the apphost, and deliberately not just the Mach-O files.
# codesign treats that whole directory as nested code, so a self-contained publish's managed .dll
# assemblies count too: leave one unsigned and sealing the bundle fails with "code object is not
# signed at all" naming a .dll. Resources/ is not code and is sealed by the bundle signature.
#
# The apphost is excluded because it is the bundle's *main* executable. Signing it on its own makes
# codesign seal the whole bundle around it -- before the assemblies beside it have been signed.
#
# Batched through xargs rather than one codesign per file: there are ~240 of them, each otherwise
# paying a process start and its own round-trip to Apple's timestamp server.
nested_count=$(find "$APP/Contents/MacOS" -type f ! -name Kanal.Host -print | wc -l | tr -d ' ')

find "$APP/Contents/MacOS" -type f ! -name Kanal.Host -print0 \
    | xargs -0 -n 40 codesign --force --timestamp --options runtime --sign "$IDENTITY"

echo "sign.sh: signed $nested_count nested binaries"

# The bundle last, and only this one carries the entitlements.
codesign --force --timestamp --options runtime \
    --entitlements "$ENTITLEMENTS" \
    --sign "$IDENTITY" \
    "$APP"

# Verifying with the notary service's own strictness catches rejections now rather than after a
# round-trip to Apple.
codesign --verify --deep --strict --verbose=2 "$APP"

# --verify above passes on an ad-hoc signature too, and ad-hoc is exactly the signature Gatekeeper
# refuses. What separates them is the Authority line, so assert on that rather than on an exit code.
details="$(codesign -dv --verbose=2 "$APP" 2>&1)"
echo "$details"

for expected in "Authority=Developer ID Application" "Timestamp=" "flags=.*runtime"; do
    if ! grep -qE "$expected" <<< "$details"; then
        echo "sign.sh: signature is missing '$expected' — notarisation would reject this" >&2
        exit 1
    fi
done

echo "sign.sh: $APP signed"
