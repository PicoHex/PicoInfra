#!/usr/bin/env bash
# API-baseline gate: `api/<PackageId>.public.txt` is generated output, and this
# is the check that says so where a contributor will see it.
#
# `api/` holds the public surface of the **last release**, written by the release
# tool from the tagged tree. It deliberately lags `main`: between releases the
# source moves and `api/` does not. Two consequences follow, and they are the
# whole design here:
#
#   1. A commit that changes the public surface must NOT touch `api/`. The next
#      release refreshes it and bumps x. Refreshing it early makes the release
#      tool compare the tree against a baseline that already matches it, so it
#      answers "API unchanged" and bumps y where an x is due
#      (docs/releasing.md, picohex-release skill, step 4).
#   2. Therefore the only legitimate way for `api/` to differ from the newest
#      release tag is a hand-edit - and a hand-edit is invisible until a human
#      runs the release tool.
#
# `verify` is the blocking gate: `api/` must be byte-identical to `api/` at the
# newest release tag reachable from HEAD. That is the drift-detector semantics
# (a). It costs no build, needs no NuGet feed and no sibling repositories, and it
# cannot false-fail on development, because development never writes `api/`.
#
# `report` is advisory: it builds the tree, extracts the surface of every
# packable assembly with scripts/api-surface, and prints the delta against `api/`
# plus the version that delta implies for the next release. It fails only when it
# cannot extract (build or tool failure) - never because the surface moved.
# Blocking on a *removal* here was rejected: `main` would be red for the whole
# interval between an intentional breaking change and its release (4 days around
# v2026.11.0: edf2cfd 2026-11-02 and 5729d95 2026-11-03, tag 2026-11-06), and the
# cheapest way for an author to silence it is the one action that corrupts the
# version decision - editing api/.
#
# Usage: bash scripts/check-api-baselines.sh verify
#        bash scripts/check-api-baselines.sh report
set -uo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
tool_project="$root/scripts/api-surface/ApiSurface.csproj"
tool_dll="$root/scripts/api-surface/bin/Release/net10.0/picohex-api-surface.dll"
baseline_dir="$root/api"
solution="$root/PicoInfra.slnx"
diff_line_cap=40
scratch=""

say() { printf '%s\n' "$*"; }
note() { say "::notice::$*"; }
warn() { say "::warning::$*"; }
err() { say "::error::$*"; }

# Step-summary lines are optional: the gate is also runnable on a laptop.
summary() {
    [ -n "${GITHUB_STEP_SUMMARY:-}" ] || return 0
    printf '%s\n' "$*" >>"$GITHUB_STEP_SUMMARY"
}

# The newest release tag reachable from HEAD. `git tag --sort=-v:refname` would
# pick a release this branch does not contain, and comparing a branch cut before
# a release against that release is a guaranteed false failure.
last_tag() {
    git -C "$root" describe --tags --abbrev=0 --match 'v[0-9]*' 2>/dev/null || true
}

# --------------------------------------------------------------- verify (a) --

# `api/` must be exactly what the newest release tag carries. Not "match the
# current tree": post-release `main` has moved on by design.
cmd_verify() {
    local tag head
    tag="$(last_tag)"
    if [ -z "$tag" ]; then
        note "no release tag (v[0-9]*) reachable from HEAD: nothing to compare api/ against yet"
        return 0
    fi
    if ! git -C "$root" cat-file -e "$tag:api" 2>/dev/null; then
        note "release tag $tag predates api/ (that tree carries no baselines): nothing to compare"
        return 0
    fi

    head="$(git -C "$root" log -1 --format='%h %s' "$tag")"
    say "reference release: $tag ($head)"

    local tracked untracked changes
    tracked="$(git -C "$root" diff --name-status "$tag" -- api/)"
    untracked="$(git -C "$root" status --porcelain --untracked-files=all -- api/ | grep '^??' || true)"
    changes="$tracked"
    if [ -n "$untracked" ]; then
        changes="${changes}${changes:+$'\n'}$(printf '%s\n' "$untracked" | sed 's/^?? /A\t/')"
    fi

    if [ -z "$changes" ]; then
        say "api/ matches $tag: no baseline differs from the release that wrote it."
        say "The baselines still describe the last release; the current tree may have moved on."
        summary "### API baselines"
        summary ""
        summary "\`api/\` is byte-identical to \`api/\` at \`$tag\` - it still describes the last release."
        return 0
    fi

    err "api/ no longer matches $tag, the release it describes"
    printf '%s\n' "$changes" | sed 's/^/  /'
    say ""
    say "api/<PackageId>.public.txt is GENERATED OUTPUT - never hand-edited."
    say "  - It holds the last release's surface on purpose; between releases it lags main."
    say "  - A real surface change does not touch api/: the next release refreshes it and bumps x."
    say "  - Editing it makes the release tool read the delta as 'API unchanged' and answer with a"
    say "    y bump where an x is due (docs/releasing.md)."
    say ""
    say "To restore:  git checkout $tag -- api/"
    summary "### API baselines"
    summary ""
    summary "**FAILED**: \`api/\` differs from \`$tag\`, the release it describes."
    summary ""
    summary '```'
    summary "$changes"
    summary '```'
    summary ""
    summary "\`api/\` is generated output. Restore it with \`git checkout $tag -- api/\`;"
    summary "the next release refreshes it from the tagged tree."
    return 1
}

# ---------------------------------------------------------- report (advisory) --

# Mirrors Get-PackableProjects in the release tool: every *.csproj under the repo
# root (or root/src when it exists) with <IsPackable>true</IsPackable>, excluding
# bin/ and obj/.
packable_projects() {
    local scan_root="$root"
    [ -d "$root/src" ] && scan_root="$root/src"
    find "$scan_root" -type f -name '*.csproj' \
        -not -path '*/bin/*' -not -path '*/obj/*' -not -path '*/.git/*' |
        sort |
        while IFS= read -r project; do
            grep -qiE '<IsPackable>[[:space:]]*true[[:space:]]*</IsPackable>' "$project" &&
                printf '%s\n' "$project"
        done
}

# First non-empty <Name> element, as the release tool reads it.
xml_prop() {
    grep -oE "<$2>[^<]+</$2>" "$1" 2>/dev/null | head -1 | sed -E "s|</?$2>||g"
}

# Same lookup as Get-Surface: the newest bin/Release/**/<AssemblyName>.dll that is
# not a reference assembly.
find_assembly() {
    local dir="$1" name="$2"
    [ -d "$dir/bin/Release" ] || return 1
    find "$dir/bin/Release" -type f -name "$name.dll" \
        -not -path '*/ref/*' -not -path '*/obj/*' -printf '%T@\t%p\n' 2>/dev/null |
        sort -rn | head -1 | cut -f2-
}

cmd_report() {
    local tag
    tag="$(last_tag)"

    say "=== building the surface extractor (net10.0, zero PackageReference) ==="
    if ! dotnet build "$tool_project" -c Release -v quiet --nologo; then
        err "the api-surface tool does not build: the surface cannot be extracted"
        return 1
    fi

    say "=== building the tree (Release) ==="
    # The canonical build command for this repo; it produces every packable
    # assembly under bin/Release/<tfm>/, which is where Get-Surface reads from.
    # One retry: MSB4166 ("child node exited prematurely") and file-lock failures
    # are transport, not code - two of four solution builds on the development box
    # hit it - and a real compile error fails again immediately.
    if ! dotnet build "$solution" -c Release -m:4 --nologo -v quiet; then
        say "the Release build failed; retrying once (node crash / file lock, not necessarily code)"
        if ! dotnet build "$solution" -c Release -m:4 --nologo -v quiet; then
            err "the tree does not build in Release: the public surface cannot be extracted"
            return 1
        fi
    fi

    local tmp
    scratch="$(mktemp -d)"
    tmp="$scratch"
    trap 'rm -rf "${scratch:-}"' EXIT

    local total_added=0 total_removed=0
    local rows="" warned=0 pkg_count=0 seen="" project package_id assembly_name
    local target_dir assembly out added removed diffout diffrc delta

    while IFS= read -r project; do
        package_id="$(xml_prop "$project" PackageId)"
        assembly_name="$(xml_prop "$project" AssemblyName)"
        [ -n "$package_id" ] || package_id="${assembly_name:-$(basename "$project" .csproj)}"
        [ -n "$assembly_name" ] || assembly_name="$package_id"
        target_dir="$(dirname "$project")"
        seen="$seen$package_id"$'\n'
        pkg_count=$((pkg_count + 1))

        if [ ! -f "$baseline_dir/$package_id.public.txt" ]; then
            warn "$package_id is packable but api/$package_id.public.txt does not exist; the release tool creates it when it next runs"
            rows="$rows| \`$package_id\` | - | - | no baseline yet |"$'\n'
            continue
        fi

        assembly="$(find_assembly "$target_dir" "$assembly_name")"
        if [ -z "$assembly" ]; then
            err "no built assembly for $package_id (expected $target_dir/bin/Release/**/$assembly_name.dll)"
            return 1
        fi

        out="$tmp/$package_id.public.txt"
        if ! dotnet "$tool_dll" dump "$assembly" --out "$out"; then
            err "api-surface dump failed for $package_id ($assembly)"
            return 1
        fi

        diffout="$(dotnet "$tool_dll" diff "$baseline_dir/$package_id.public.txt" "$out")"
        diffrc=$?
        if [ "$diffrc" -gt 1 ]; then
            err "api-surface diff failed for $package_id"
            printf '%s\n' "$diffout"
            return 1
        fi
        delta="$(printf '%s\n' "$diffout" | tail -1)"
        added="${delta#added=}"; added="${added%% *}"
        removed="${delta##*removed=}"
        total_added=$((total_added + added))
        total_removed=$((total_removed + removed))
        rows="$rows| \`$package_id\` | $added | $removed | |"$'\n'

        if [ "$diffrc" -ne 0 ]; then
            warn "$package_id: public surface moved since ${tag:-the last release} (+$added/-$removed); api/ is NOT updated by hand - the next release refreshes it and bumps x"
            warned=$((warned + 1))
            say ""
            say "--- $package_id (+$added/-$removed) ---"
            printf '%s\n' "$diffout" | grep -E '^[+-]' | head -n "$diff_line_cap"
            if [ "$(printf '%s\n' "$diffout" | grep -cE '^[+-]')" -gt "$diff_line_cap" ]; then
                say "... (log is capped at $diff_line_cap lines here; the step summary carries the counts)"
            fi
        fi
    done < <(packable_projects)

    # A baseline that names no packable PackageId is a file the release tool will
    # never rewrite (it iterates projects, not files).
    local baseline pkg
    for baseline in "$baseline_dir"/*.public.txt; do
        [ -e "$baseline" ] || continue
        pkg="$(basename "$baseline" .public.txt)"
        if ! printf '%s' "$seen" | grep -qxF "$pkg"; then
            warn "api/$pkg.public.txt names no packable project (renamed or removed?); the release tool only writes baselines for existing projects"
            rows="$rows| \`$pkg\` | - | - | orphan baseline |"$'\n'
        fi
    done

    local api_changed="false" next=""
    [ $((total_added + total_removed)) -ne 0 ] && api_changed="true"
    if [ -n "$tag" ]; then
        next="$(dotnet "$tool_dll" next-version --last "$tag" --api-changed "$api_changed")"
    else
        next="$(dotnet "$tool_dll" next-version --api-changed "$api_changed")"
    fi

    say ""
    say "=== surface delta since ${tag:-the first release}: +$total_added/-$total_removed across $pkg_count packable projects ==="
    if [ "$api_changed" = "true" ]; then
        say "the public API changed, so the next release is an x bump: ${next:-?}"
        say "api/ is generated: leave it alone, the release refreshes it from the tagged tree."
    else
        say "the public API is unchanged, so the next release is a y bump: ${next:-?}"
    fi

    summary "### Public API surface vs \`${tag:-no previous release}\`"
    summary ""
    summary "\`api/\` is generated by the release tool at release time - **never hand-edit it**."
    summary "A surface change between releases is expected: the release refreshes \`api/\` and"
    summary "bumps x. There is nothing to fix in the pull request."
    summary ""
    summary "| package | added | removed | |"
    summary "| --- | ---: | ---: | --- |"
    while IFS= read -r row; do [ -n "$row" ] && summary "$row"; done <<<"$rows"
    summary "| **total** | **$total_added** | **$total_removed** | $pkg_count packable projects |"
    summary ""
    if [ "$api_changed" = "true" ]; then
        summary "The API changed, so the next release is an **x bump** (\`${next:-?}\`)."
    else
        summary "The API is unchanged, so the next release is a **y bump** (\`${next:-?}\`)."
    fi
    if [ "$total_removed" -gt 0 ]; then
        summary ""
        summary "**$total_removed line(s) the last release published are gone.** A removal is a breaking"
        summary "change (source, and for a method signature also binary). It ships only with an x bump"
        summary "and a release-note entry; it is not a reason to edit \`api/\`."
    fi

    if [ "$warned" -eq 0 ] && [ "$total_added" -eq 0 ] && [ "$total_removed" -eq 0 ]; then
        say "no surface drift: every baseline matches what the current tree extracts."
    fi
    return 0
}

# ---------------------------------------------------------------------- entry --

case "${1:-}" in
verify) cmd_verify ;;
report) cmd_report ;;
*)
    say "usage: bash scripts/check-api-baselines.sh <verify|report>" >&2
    exit 2
    ;;
esac
