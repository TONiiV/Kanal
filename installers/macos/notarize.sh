#!/usr/bin/env bash
#
# Submit an artefact to Apple's notary service and staple the resulting ticket to it.
#
# Called twice per release, once for each layer. Stapling only the dmg would leave the .app without
# its own ticket, so the first launch after dragging it to /Applications needs a network round-trip
# to Apple. Kanal exists to run a meeting on local models with no connectivity, so an app that will
# not open offline is a broken app.
#
# Credentials come from an App Store Connect API key rather than an Apple ID with an app-specific
# password: revocable on its own, tied to no individual's account, unaffected by 2FA.
#
#   NOTARY_KEY_PATH    path to the .p8 private key
#   NOTARY_KEY_ID      key id from App Store Connect
#   NOTARY_ISSUER_ID   issuer id from App Store Connect
#
# Usage: notarize.sh <path> <app|dmg>

set -euo pipefail

TARGET="${1:?usage: notarize.sh <path> <app|dmg>}"
KIND="${2:?missing kind (app or dmg)}"

: "${NOTARY_KEY_PATH:?NOTARY_KEY_PATH is not set}"
: "${NOTARY_KEY_ID:?NOTARY_KEY_ID is not set}"
: "${NOTARY_ISSUER_ID:?NOTARY_ISSUER_ID is not set}"

notary_args=(--key "$NOTARY_KEY_PATH" --key-id "$NOTARY_KEY_ID" --issuer "$NOTARY_ISSUER_ID")

case "$KIND" in
    app)
        # A bundle is a directory. The notary service takes archives, and ditto is the only zip that
        # preserves the symlinks and extended attributes a signed bundle depends on - a plain `zip`
        # produces an archive that notarises and then fails to launch.
        submission="$(dirname "$TARGET")/$(basename "$TARGET").zip"
        rm -f "$submission"
        ditto -c -k --keepParent "$TARGET" "$submission"
        ;;
    dmg)
        submission="$TARGET"
        ;;
    *)
        echo "notarize.sh: kind must be 'app' or 'dmg', got '$KIND'" >&2
        exit 1
        ;;
esac

echo "notarize.sh: submitting $submission"

# --wait blocks until Apple reaches a verdict and exits non-zero on rejection. Capture the id so the
# log can be fetched, because the failure message on its own never says which binary was at fault.
set +e
output="$(xcrun notarytool submit "$submission" "${notary_args[@]}" --wait 2>&1)"
status=$?
set -e

echo "$output"

if [[ $status -ne 0 ]]; then
    submission_id="$(echo "$output" | awk '/id: /{print $2; exit}')"
    if [[ -n "$submission_id" ]]; then
        echo "notarize.sh: rejected. Fetching the log for $submission_id" >&2
        xcrun notarytool log "$submission_id" "${notary_args[@]}" >&2 || true
    fi
    exit $status
fi

# Staple the original, not the zip - the ticket belongs on the bundle or the dmg.
xcrun stapler staple "$TARGET"
xcrun stapler validate "$TARGET"

[[ "$KIND" == "app" ]] && rm -f "$submission"

echo "notarize.sh: $TARGET notarised and stapled"
