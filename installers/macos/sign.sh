#!/usr/bin/env bash
#
# Sign a staged Kanal.app for Developer ID distribution.
#
# Signing is inside-out: every nested Mach-O binary first, the bundle itself last. Apple deprecated
# --deep for exactly this reason - it signs nested code with the bundle's own entitlements and gets
# the order wrong often enough to produce bundles that pass codesign and fail notarisation.
#
# LLamaSharp ships libllama.dylib and several libggml*.dylib inside the publish output, and the .NET
# runtime adds its own native libraries plus helper executables such as createdump. Missing any one
# of them makes the notary service reject the entire submission, so binaries are discovered with
# file(1) rather than by extension.
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

nested_count=0
while IFS= read -r -d '' candidate; do
    if file --brief "$candidate" | grep -q 'Mach-O'; then
        codesign --force --timestamp --options runtime --sign "$IDENTITY" "$candidate"
        nested_count=$((nested_count + 1))
    fi
done < <(find "$APP" -type f -print0)

echo "sign.sh: signed $nested_count nested binaries"

# The bundle last, and only this one carries the entitlements.
codesign --force --timestamp --options runtime \
    --entitlements "$ENTITLEMENTS" \
    --sign "$IDENTITY" \
    "$APP"

# Verifying with the notary service's own strictness catches rejections now rather than after a
# round-trip to Apple.
codesign --verify --deep --strict --verbose=2 "$APP"

echo "sign.sh: $APP signed"
