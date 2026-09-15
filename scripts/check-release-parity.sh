#!/usr/bin/env bash
# Parity guard: the local release script (scripts/release.ps1) must pack the same
# projects as the release workflow (.github/workflows/release.yml). Drift here is
# silent — e.g. the script once missed PicoSchedule, so the local folder feed
# (used by sibling PicoHex repos) was incomplete while CI published the full set.
#
# Usage: bash scripts/check-release-parity.sh
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
workflow_file="$root/.github/workflows/release.yml"
script_file="$root/scripts/release.ps1"

extract_workflow() {
    grep -oE 'dotnet pack [^ \\]+\.csproj' "$workflow_file" |
        sed -E 's/^dotnet pack //' |
        tr '\\' '/' |
        sort -u
}

extract_script() {
    grep -oE 'Pack "[^"]+\.csproj"' "$script_file" |
        sed -E 's/^Pack "//; s/"$//' |
        tr '\\' '/' |
        sort -u
}

workflow_targets="$(extract_workflow)"
script_targets="$(extract_script)"

if [ -z "$workflow_targets" ]; then
    echo "::error::no 'dotnet pack' targets found in $workflow_file"
    exit 1
fi

if [ "$workflow_targets" != "$script_targets" ]; then
    echo "::error::release.ps1 and release.yml pack different project sets"
    echo "--- release.yml (left) vs release.ps1 (right) ---"
    diff <(printf '%s\n' "$workflow_targets") <(printf '%s\n' "$script_targets") || true
    exit 1
fi

echo "OK: $(printf '%s\n' "$workflow_targets" | wc -l | tr -d ' ') pack targets in parity"
