#!/usr/bin/env bash
# Canonical copy: picohex-skills/skills/picohex-dependency-policy/scripts/check-dependency-policy.sh (keep in sync)
# check-dependency-policy.sh — assert the PicoHex dependency policy (R1-R7, R7b, R10).
#
#   bash scripts/check-dependency-policy.sh [REPO_ROOT ...]
#
# shellcheck disable=SC2016  # the single-quoted $ below are literal message text
#   REPO_ROOT   one or more repository roots (default: the current directory)
#
# Exit: 0 = every assertion passed, 1 = at least one assertion failed,
#       2 = usage / environment error.
#
# Runs on Linux CI and on Windows under Git Bash. Needs only bash, awk, sed,
# grep, find and git — no dotnet, no python, no network.
#
# The rule numbers match SKILL.md. Every failure prints why the rule exists and
# the exact edit that clears it.

set -uo pipefail

readonly SELF_RULES='R1 R2 R3 R4 R5 R6 R7 R7b R10'

# ── R1 classification (explicit whitelists — see SKILL.md R1) ───────────────
#
# R1 floats the LAST segment only. Which last segment is legal depends on the
# tier, and the tier is a property of the PACKAGE, not of its name: the prefix
# `System.` is shared by Microsoft's BCL packages and by community packages
# (`System.IO.Abstractions` is Testably's, a third-party package). Classifying
# by prefix would file the wrong rule against the wrong tier, so the tier comes
# from these lists, consulted in this order:
#
#   1. THIRD_PARTY_IDS  (exact id)      -> x.y.*   (semver: patch promises no break)
#   2. OFFICIAL_IDS     (exact id)      -> x.y.*   (same, Microsoft-maintained)
#   3. `Microsoft.*`                    -> official (semver, Microsoft-published)
#   4. `Pico*`                          -> first-party -> <year>.<x>.*
#   5. anything else                    -> UNCLASSIFIED, fails closed
#
# Fail-closed on purpose: a checker that guesses a tier is a checker whose
# verdicts nobody can audit. Adding a package means adding it to a list here.
readonly OFFICIAL_IDS='System.Buffers System.IO.Pipelines System.Threading.Channels System.Threading.Tasks.Extensions'
readonly THIRD_PARTY_IDS='TUnit YamlDotNet MessagePack Tomlyn Tommy ini-parser-netstandard System.IO.Abstractions'

# The one legal float shape per tier: wildcard on the last segment, nothing less.
#   first-party  <year>.<x>.*   (picohex-release: x+1 = public API changed)
#   semver       <x>.<y>.*     (semver:   y+1 = feature, z+1 = fix)
readonly R1_FIRST_RE='^[0-9]{4}\.[0-9]+\.\*$'
readonly R1_SEMVER_RE='^[0-9]+\.[0-9]+\.\*$'

# Directory names never scanned. .worktrees holds full repo copies (a git
# worktree per lane) and would report the same defect N times; .superpowers
# holds throwaway probe harnesses; obj/bin/artifacts are build output.
readonly PRUNE_NAMES=(obj bin node_modules artifacts TestResults .git .worktrees .superpowers .review-2026-11 publish logs results)

fail_count=0
pass_count=0
root_name=""

# ── output helpers ───────────────────────────────────────────────────────────

c_pass() { pass_count=$((pass_count + 1)); printf '  [PASS] %-4s %s\n' "$1" "$2"; }

c_fail() { # rule, headline, why, fix
    fail_count=$((fail_count + 1))
    printf '  [FAIL] %-4s %s\n' "$1" "$2"
    printf '         why : %s\n' "$3"
    shift 3
    while [ $# -gt 0 ]; do
        printf '         fix : %s\n' "$1"
        shift
    done
}

c_note() { printf '         note: %s\n' "$*"; }

# ── filesystem helpers ──────────────────────────────────────────────────────

PRUNE_EXPR=( -false )
for n in "${PRUNE_NAMES[@]}"; do PRUNE_EXPR=( "${PRUNE_EXPR[@]}" -o -name "$n" ); done

# find_files ROOT NAME — absolute paths, pruned.
find_files() {
    local root=$1 name=$2
    find "$root" \( "${PRUNE_EXPR[@]}" \) -prune -o -type f -name "$name" -print 2>/dev/null | sort
}

# xml_comments_with_dashes FILE — prints "<line>: <comment body>" for every XML
# comment whose body contains "--" (illegal XML, and MSBuild drops the whole
# imported file without a word).
xml_comments_with_dashes() {
    awk '
        {
            s = $0
            while (1) {
                if (!incomment) {
                    p = index(s, "<!--")
                    if (p == 0) break
                    s = substr(s, p + 4); incomment = 1; body = ""
                } else {
                    p = index(s, "-->")
                    if (p == 0) { body = body s " "; s = ""; break }
                    body = body substr(s, 1, p - 1)
                    s = substr(s, p + 3)
                    incomment = 0
                    gsub(/[[:space:]]+/, " ", body)
                    if (index(body, "--") > 0) printf "%d: %s\n", NR, body
                    body = ""
                }
            }
        }
    ' "$1"
}

# run_commands FILE — one logical shell command per output line, as
# "<line>\t<text>". Handles `run: <inline>`, `run: |` and `run: >` block
# scalars (joining continuation lines), and drops shell-comment-only lines so
# prose like "do not use --force-evaluate here" is not read as a flag.
run_commands() {
    awk '
        function clean(str) {
            sub(/^[[:space:]]+/, "", str); sub(/[[:space:]]+$/, "", str); return str
        }
        function flush() {
            if (buf != "") printf "%d\t%s\n", start, buf
            buf = ""
        }
        {
            line = $0
            if (match(line, /^[[:space:]]*run:[[:space:]]*/)) {
                rest = substr(line, RLENGTH + 1)          # RLENGTH is clobbered by the next match()
                m = match(line, /[^[:space:]]/)
                runind = (m > 0 ? m - 1 : 0)
                if (rest ~ /^[|>]/) { inblock = 1; buf = ""; start = NR; next }
                inblock = 0
                buf = rest; start = NR
                flush()
                next
            }
            if (inblock) {
                if (line ~ /^[[:space:]]*$/) next
                if (clean(line) ~ /^#/) next
                m = match(line, /[^[:space:]]/)
                ind = (m > 0 ? m - 1 : 0)
                if (ind > runind) { buf = (buf == "" ? clean(line) : buf " " clean(line)) }
                else { inblock = 0; flush() }
            }
        }
        END { flush() }
    ' "$1"
}

has_rid_flag() {
    # -r <rid> | --runtime <rid> | --runtime=<rid> | -p:RuntimeIdentifier=<rid>
    printf '%s' "$1" | grep -Eq '(^|[[:space:]])(-r|--runtime)([[:space:]=]|$)' ||
        printf '%s' "$1" | grep -Eq '(^|[[:space:]])-p:RuntimeIdentifier'
}

has_flag() { printf '%s' "$1" | grep -Eq "$2"; }

# ── the rules ───────────────────────────────────────────────────────────────

# r1_tier ID — prints first | official | third | unknown (see the lists above).
r1_tier() {
    local id=$1 p
    for p in $THIRD_PARTY_IDS; do [ "$id" = "$p" ] && { printf 'third'; return; }; done
    for p in $OFFICIAL_IDS; do [ "$id" = "$p" ] && { printf 'official'; return; }; done
    case $id in Microsoft.*) printf 'official'; return ;; esac
    case $id in Pico*) printf 'first'; return ;; esac
    printf 'unknown'
}

# r1_nums VERSION — the leading all-numeric segments, space separated.
# `2026.13.*` -> "2026 13" ; `18.*` -> "18" ; `*` -> "" ; `1.83.8` -> "1 83 8".
r1_nums() {
    printf '%s' "$1" | awk -F. '{ for (i = 1; i <= NF; i++) { if ($i !~ /^[0-9]+$/) break; printf "%s%s", (i > 1 ? " " : ""), $i } }'
}

# r1_suggest TIER VERSION — the float that tier wants, derived from VERSION so the
# printed fix is copy-pasteable. With too little information it emits a placeholder.
r1_suggest() {
    local tier=$1 nums s1 s2
    nums=$(r1_nums "$2")
    # shellcheck disable=SC2086  # splitting into the leading numeric segments is the point
    set -- $nums
    s1=${1:-}; s2=${2:-}
    case $tier in
        first)
            if [ -n "$s1" ] && [ -n "$s2" ]; then printf '%s.%s.*' "$s1" "$s2"
            elif [ -n "$s1" ]; then printf '%s.<x>.*' "$s1"
            else printf '<year>.<x>.*'; fi ;;
        *)
            if [ -n "$s1" ] && [ -n "$s2" ]; then printf '%s.%s.*' "$s1" "$s2"
            else printf '<major>.<minor>.*'; fi ;;
    esac
}

check_r1() { # ROOT PKGPROPS
    local root=$1 props=$2 hits=0 line where num inc ver tier want fix nseg
    local n_first=0 n_official=0 n_third=0 n_exempt=0

    while IFS= read -r line; do
        case $line in *PackageVersion*) ;; *) continue ;; esac
        num=${line%%:*}
        where=${line#*:}
        inc=$(printf '%s' "$where" | sed -n 's/.*Include="\([^"]*\)".*/\1/p')
        ver=$(printf '%s' "$where" | sed -n 's/.*Version="\([^"]*\)".*/\1/p')
        [ -n "$inc" ] || continue
        [ -n "$ver" ] || { c_note "PackageVersion Include=\"$inc\" has no Version attribute — skipped"; continue; }
        case $ver in
            '$('*)
                # Own repo / release-injected version: a property reference, not a
                # dependency. Exempt by rule (but see the SKILL: a property whose
                # default is a literal reintroduces the pin through the back door).
                n_exempt=$((n_exempt + 1)); continue ;;
        esac
        case $ver in *-*) # prerelease (or a "*-*" float)
            hits=$((hits + 1)); c_fail R1 "prerelease version: $inc $ver  ($props:$num)" \
                "floating resolves to the highest STABLE only; a prerelease float (*-*) lets an unversioned preview land in a release build" \
                "<PackageVersion Include=\"$inc\" Version=\"$(r1_suggest "$(r1_tier "$inc")" "$ver")\" />"
            continue ;;
        esac
        tier=$(r1_tier "$inc")
        case $tier in
            first) n_first=$((n_first + 1)) ;;
            official) n_official=$((n_official + 1)) ;;
            third) n_third=$((n_third + 1)) ;;
        esac
        want=$(r1_suggest "$tier" "$ver")
        fix='<PackageVersion Include="'"$inc"'" Version="'"$want"'" />'
        nseg=$(r1_nums "$ver" | awk '{ print NF }'); nseg=${nseg:-0}

        # ── the conforming case: wildcard on the last segment, per tier ──────
        if [ "$tier" = first ]; then
            printf '%s' "$ver" | grep -Eq "$R1_FIRST_RE" && continue
        elif [ "$tier" != unknown ]; then
            printf '%s' "$ver" | grep -Eq "$R1_SEMVER_RE" && continue
        fi

        hits=$((hits + 1))
        case $tier in
            unknown)
                c_fail R1 "unclassified package: $inc $ver  ($props:$num)" \
                    "R1 cannot pick a float shape for a package no list names; guessing from the prefix is how 'System.IO.Abstractions' (Testably, third-party) got held to Microsoft's tier" \
                    "classify $inc in scripts/check-dependency-policy.sh: OFFICIAL_IDS='...' or THIRD_PARTY_IDS='...'" \
                    "then re-run; the expected shape is <x>.<y>.* for either semver tier, <year>.<x>.* for first-party"
                continue ;;
        esac

        # ── floats that are too wide: the possibly-breaking segment rides along ─
        if [ "$ver" = '*' ]; then
            c_fail R1 "bare wildcard: $inc $ver  ($props:$num)" \
                "'*' floats the MAJOR segment, i.e. it auto-lands the one release that promises to break us: a major move arrives as an unrelated restore, code untouched, and the review sees only a moved lock line" \
                "$fix"
            c_note "take major.minor from the version resolved today (packages.lock.json) or the latest stable on the feed"
            continue
        fi
        if printf '%s' "$ver" | grep -Eq '^[0-9]+\.\*$'; then
            if [ "$tier" = first ]; then
                c_fail R1 "cross-x float: $inc $ver  ($props:$num)" \
                    "<year>.* floats the x segment, and picohex-release defines x+1 as 'public API changed' — the one segment that must be migrated by a human. Floating it means a breaking upstream release lands with zero code change, and the only visible trace is a version number nobody chose" \
                    "$fix"
            else
                c_fail R1 "cross-minor float: $inc $ver  ($props:$num)" \
                    "<major>.* floats the minor segment, which semver uses for features — the segment that may add overloads, change defaults or deprecate. Only the patch segment promises no break, so only the patch segment may float" \
                    "$fix"
            fi
            continue
        fi

        # ── four-segment versions (NuGet legacy major.minor.build.revision) ────
        if [ "$nseg" -ge 4 ]; then
            c_fail R1 "four-segment version: $inc $ver  ($props:$num)" \
                "a four-segment NuGet version carries major.minor.build.revision, so the semver patch band is segments 3-4 together; the wildcard therefore starts at the PENULTIMATE segment (1.2.3.4 -> 1.2.*), which keeps major+minor fixed and floats the whole patch band" \
                "$fix"
            c_note "if the 3rd/4th segments are really independent build metadata, use the date/CI version instead of a hand-typed quad"
            continue
        fi

        # ── exact pins and other non-floating shapes ──────────────────────────
        if [ "$nseg" -eq 0 ]; then
            c_fail R1 "not a version at all: $inc $ver  ($props:$num)" \
                "R1 expects a float of the form <year>.<x>.* (first-party) or <x>.<y>.* (official / third-party)" \
                "$fix"
            continue
        fi
        if [ "$ver" = "${ver#*\*}" ]; then
            if [ "$tier" = first ]; then
                c_fail R1 "exact first-party version: $inc $ver  ($props:$num)" \
                    "a pin is a decision nobody re-evaluates: it keeps the build still by making the upstream move invisible, while the declaration's last-segment float already carries every patch forward on its own and R10 reports the band" \
                    "$fix"
            else
                c_fail R1 "exact $( [ "$tier" = third ] && printf 'third-party' || printf 'official' ) version: $inc $ver  ($props:$num)" \
                    "the patch segment is the one that promises no break, so it is the one we may float; pinning it buys nothing a plain restore does not already give, and it puts a version move in the hands of whoever runs the pin update instead of the feed" \
                    "$fix"
            fi
            continue
        fi
        c_fail R1 "malformed float: $inc $ver  ($props:$num)" \
            "the wildcard must be the LAST segment and nothing else: <year>.<x>.* (first-party) or <x>.<y>.* (official / third-party)" \
            "$fix"
    done < <(grep -n '<PackageVersion' "$props" 2>/dev/null)

    c_note "classified: first $n_first, official $n_official, third $n_third, exempt(\$(property)) $n_exempt"
    if [ "$hits" -eq 0 ]; then
        c_pass R1 'every dependency floats its last segment only — <year>.<x>.* first-party, <x>.<y>.* official/third-party'
    fi
}

check_r2() { # ROOT PKGPROPS
    local root=$1 props=$2
    if grep -q '<CentralPackageTransitivePinningEnabled>true<' "$props" 2>/dev/null; then
        c_pass R2 'CentralPackageTransitivePinningEnabled=true'
    else
        c_fail R2 'CentralPackageTransitivePinningEnabled is not set to true' \
            "without it the central float only governs projects that reference the package DIRECTLY; every other project resolves that package at some upstream nuspec lower bound, so one build graph can carry two copies of it" \
            "add to $props:  <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>"
    fi
}

check_r3() { # ROOT PKGPROPS
    local root=$1 props=$2
    if grep -q '<CentralPackageFloatingVersionsEnabled>true<' "$props" 2>/dev/null; then
        c_pass R3 'CentralPackageFloatingVersionsEnabled=true'
    else
        c_fail R3 'CentralPackageFloatingVersionsEnabled is not set to true' \
            "CPM rejects a floating PackageVersion with NU1011 unless the opt-in is on, so R1's floats cannot even be declared" \
            "add to $props:  <CentralPackageFloatingVersionsEnabled>true</CentralPackageFloatingVersionsEnabled>"
    fi
}

check_r4() { # ROOT PKGPROPS BUILDPROPS
    local root=$1 props=$2 build=$3 name total in_props in_build hits=0
    for name in ManagePackageVersionsCentrally CentralPackageTransitivePinningEnabled CentralPackageFloatingVersionsEnabled; do
        in_props=$(grep -c "<$name" "$props" 2>/dev/null || true)
        in_build=$(grep -c "<$name" "$build" 2>/dev/null || true)
        in_props=${in_props:-0}; in_build=${in_build:-0}
        total=$((in_props + in_build))
        [ "$total" -eq 1 ] && continue
        if [ "$total" -eq 0 ]; then
            # Absence is already reported by R2 / R3 for the two pinning
            # switches; only ManagePackageVersionsCentrally has no other owner,
            # so an ABSENT switch is reported once, never twice.
            case $name in
                ManagePackageVersionsCentrally)
                    hits=$((hits + 1))
                    c_fail R4 "$name is declared 0 times (needs exactly 1, in $props)" \
                        "a CPM switch that is never declared does not exist" \
                        "add <$name>true</$name> to $props" ;;
                *) c_note "$name is absent — reported under R2/R3, not counted twice here" ;;
            esac
        else
            hits=$((hits + 1))
            c_fail R4 "$name is declared $total times (pkgs=$in_props, build=$in_build)" \
                "two declarations of the same switch make 'which one wins' a question nobody can answer from the code" \
                "keep exactly one declaration, in $props; delete the others from $build"
        fi
    done
    [ "$hits" -eq 0 ] && c_pass R4 'the three CPM switches are each declared exactly once'
}

# (Removed, R5 cutover: slnx_projects.) R5b used to enumerate the .slnx projects
# and demand a git-tracked packages.lock.json beside each one. Nothing is tracked
# any more, so the assertion inverts into two questions git answers directly:
# what is tracked (must be nothing), and what is ignored (must be all of it).

check_r5() { # ROOT BUILDPROPS
    # R5 is a contract of ABSENCE: the record is written, never tracked, never
    # replayed. See SKILL.md "R5 detail" and "What was removed and why".
    local root=$1 build=$2 hits=0 tracked rel f probe bad r5c_hits=0 nprobe ci miss wf lines

    # ── R5a: the switch that WRITES the record stays on ─────────────────────
    if grep -q '<RestorePackagesWithLockFile>true<' "$build" 2>/dev/null; then
        c_pass R5a 'RestorePackagesWithLockFile=true — restore still writes the record'
    else
        hits=$((hits + 1))
        c_fail R5a 'RestorePackagesWithLockFile is not set to true' \
            "with it off NuGet neither writes nor reads a lock file: a restore resolves silently and leaves nothing to inspect. The record is what CI ships as the resolved-versions artifact — untracked is not off" \
            "add to $build:  <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>"
    fi

    # ── R5b: nothing tracks the record, and .gitignore covers it ────────────
    if ! git -C "$root" rev-parse --git-dir >/dev/null 2>&1; then
        c_note 'not a git work tree — R5b (lock tracking / .gitignore) skipped'
    else
        tracked=$(git -C "$root" ls-files -- '*packages.lock.json' 2>/dev/null)
        if [ -n "$tracked" ]; then
            hits=$((hits + 1))
            c_fail R5b "git tracks $(printf '%s\n' "$tracked" | wc -l | tr -d ' ') packages.lock.json file(s) (e.g. $(printf '%s\n' "$tracked" | head -1))" \
                "the lock is a local RECORD of what one restore resolved, not a pin: tracked, it dirties the tree on every publish (a publish writes RID entries such as Microsoft.DotNet.ILCompiler) and reads like an invariant nobody may move. The estate keeps the record, drops the tracking, and ships it as the resolved-versions artifact" \
                "git -C \"$root\" rm --cached -- '*packages.lock.json' 'packages.lock.json'   # the files stay on disk" \
                "then: git -C \"$root\" ls-files -- '*packages.lock.json'    # must print nothing"
        fi

        bad=''; nprobe=0
        while IFS= read -r f; do
            [ -n "$f" ] || continue
            rel=${f#"$root"/}
            # A tracked lock is reported above; ignore-checking it again would
            # report one defect twice (git never calls a tracked path ignored).
            printf '%s\n' "$tracked" | grep -qxF -- "$rel" && continue
            nprobe=$((nprobe + 1))
            git -C "$root" check-ignore -q -- "$rel" 2>/dev/null || bad="$bad $rel"
        done < <(find_files "$root" 'packages.lock.json')
        if [ "$nprobe" -eq 0 ]; then
            # Nothing restored on this clone yet: probe the path itself, at the
            # root and one level down — the pattern must match at every level.
            for probe in packages.lock.json src/probe/packages.lock.json; do
                git -C "$root" check-ignore -q -- "$probe" 2>/dev/null || bad="$bad $probe"
            done
        fi
        if [ -n "$bad" ]; then
            hits=$((hits + 1))
            c_fail R5b ".gitignore does not cover the lock record:$bad" \
                "an ignored record is the whole design: restore writes one beside every project, git never sees it, and a publish that rewrites it cannot dirty the tree. Without the ignore line the next restore stages dozens of lock files nobody meant to add" \
                "add to $root/.gitignore:  packages.lock.json    # a bare name matches at every level" \
                "then: git -C \"$root\" check-ignore -v packages.lock.json    # must name that line"
        fi
        if [ -z "$tracked" ] && [ -z "$bad" ]; then
            c_pass R5b 'no lock file is git-tracked, and .gitignore covers packages.lock.json'
        fi
    fi

    # ── R5c: no locked mode anywhere in the pipeline ────────────────────────
    while IFS= read -r wf; do
        [ -n "$wf" ] || continue
        case $wf in */.github/workflows/*) ;; *) continue ;; esac
        lines=$(grep -nE 'RestoreLockedMode|--locked-mode' "$wf" 2>/dev/null | grep -vE '^[0-9]+:[[:space:]]*#')
        [ -n "$lines" ] || continue
        r5c_hits=$((r5c_hits + 1)); hits=$((hits + 1))
        c_fail R5c "locked mode still configured: ${wf#"$root"/}:$(printf '%s' "$lines" | head -1 | cut -d: -f1)" \
            "RestoreLockedMode / --locked-mode means 'restore only from the committed record' — and nothing is committed, so it has no subject. Left in place it can only red the leg the first time a patch moves inside its band, i.e. punish exactly the behaviour the policy wants" \
            "remove the flag / property from ${wf#"$root"/} (lines: $(printf '%s' "$lines" | cut -d: -f1 | tr '\n' ' '))" \
            "the record is still written (R5a) and shipped (R5e); nothing needs to lock against it"
    done < <(find_files "$root" '*.yml'; find_files "$root" '*.yaml')
    [ "$r5c_hits" -eq 0 ] && c_pass R5c 'no RestoreLockedMode / --locked-mode anywhere under .github/workflows/'

    # ── R5d: the refresh job must not exist ─────────────────────────────────
    if [ -f "$root/.github/workflows/deps-refresh.yml" ]; then
        hits=$((hits + 1))
        c_fail R5d 'deps-refresh.yml exists — it was deleted on purpose (cutover 5d1ea54c) and must not come back' \
            "its only product was a PR whose diff was lock files; the record is gitignored now, so that diff is always empty and the job could only report 'no change'. Rewritten to bump Directory.Packages.props it would automate the band move R10 reserves for a person — and a plain restore already floats to the newest version inside each band by itself" \
            "git -C \"$root\" rm -f .github/workflows/deps-refresh.yml"
    else
        c_pass R5d 'no deps-refresh.yml — a restore absorbs patches by itself; R10 reports the band'
    fi

    # ── R5e: the record leaves the runner as an artifact ────────────────────
    ci="$root/.github/workflows/ci.yml"
    if [ ! -f "$ci" ]; then
        hits=$((hits + 1))
        c_fail R5e 'no .github/workflows/ci.yml' \
            "the resolved versions leave the pipeline as a build artifact; with no ci.yml nothing records what a build resolved" \
            'add ci.yml with a restore, a build and the resolved-versions upload (R5e)'
    else
        miss=''
        grep -q 'upload-artifact' "$ci" || miss="$miss upload-artifact"
        grep -q 'resolved-versions' "$ci" || miss="$miss resolved-versions"
        grep -q 'packages\.lock\.json' "$ci" || miss="$miss '**/packages.lock.json'"
        grep -qE 'if:[[:space:]]*always\(\)' "$ci" || miss="$miss 'if: always()'"
        if [ -n "$miss" ]; then
            hits=$((hits + 1))
            c_fail R5e "ci.yml does not ship the record — missing:$miss" \
                "the tracked lock used to answer 'which exact versions did that build resolve'. With it gone the answer has to leave the runner as data, one artifact per leg, and it must survive a red leg: a run that failed after restore is exactly the run somebody needs to inspect" \
                'add to ci.yml, after the build step (the name carries the leg):' \
                '- name: Upload resolved versions (restore record)' \
                '  if: always()' \
                '  uses: actions/upload-artifact@v4' \
                '  with:' \
                '    name: resolved-versions-${{ matrix.rid }}' \
                '    path: |' \
                '      **/packages.lock.json' \
                '    if-no-files-found: warn' \
                '    retention-days: 30'
        else
            c_pass R5e 'ci.yml uploads **/packages.lock.json as the resolved-versions artifact (if: always())'
        fi
    fi

    # ── R10: the upstream watch exists, is on-demand, and only reports ──────
    local watch="$root/.github/workflows/upstream-watch.yml"
    if [ ! -f "$watch" ]; then
        hits=$((hits + 1))
        c_fail R10 'no .github/workflows/upstream-watch.yml' \
            "nothing else names the segment a human must migrate: a restore re-resolves inside the declared range and can never leave it, and with no tracked lock there is not even a diff that hints at the band. It is the only mechanical detector of the move that needs a person" \
            'copy upstream-watch.yml from a sibling PicoHex repo (workflow_dispatch only, report-only)'
    elif grep -qE '^[[:space:]]*(schedule:|- cron:)' "$watch"; then
        hits=$((hits + 1))
        c_fail R10 'upstream-watch.yml runs on a timer' \
            'a dependency version moves BEFORE A RELEASE, not on a calendar: a report nobody asked for is a report nobody reads' \
            "remove the schedule:/cron: block from $watch and dispatch it by hand in the release pre-flight"
    elif grep -q 'workflow_dispatch' "$watch"; then
        c_pass R10 'upstream-watch.yml: on-demand, report-only — the only band detector'
    else
        hits=$((hits + 1))
        c_fail R10 'upstream-watch.yml has no workflow_dispatch' \
            'it must be triggered by a human at release time' \
            'add "on: workflow_dispatch:" to $watch'
    fi
}

check_r6() { # ROOT BUILDPROPS
    # R6 is RID neutrality: a record must describe one graph, and a publish must
    # not lean on an implicit host RID. See SKILL.md "R6 detail".
    local root=$1 build=$2 hits=0 wf cmd text at
    if grep -q '<UseDefaultPublishRuntimeIdentifier>false<' "$build" 2>/dev/null; then
        c_pass R6a 'UseDefaultPublishRuntimeIdentifier=false — restore stays RID-neutral'
    else
        hits=$((hits + 1))
        c_fail R6a 'UseDefaultPublishRuntimeIdentifier is not false' \
            "PublishAot=true makes the SDK infer the HOST RID at restore time and append it to RuntimeIdentifiers, so one machine resolves two different graphs (plain restore vs publish) and whatever gets written carries net10.0/<host-rid>: host-specific for no reason, and useless as a statement about the build" \
            "add to $build:  <UseDefaultPublishRuntimeIdentifier>false</UseDefaultPublishRuntimeIdentifier>"
    fi

    # Every `dotnet publish` in CI must state its RID and must not skip restore.
    while IFS= read -r wf; do
        [ -n "$wf" ] || continue
        case $wf in */.github/workflows/*) ;; *) continue ;; esac
        while IFS= read -r cmd; do
            [ -n "$cmd" ] || continue
            case $cmd in *'dotnet publish'*) ;; *) continue ;; esac
            text=${cmd#*$'\t'}
            at=${cmd%%$'\t'*}
            if ! has_rid_flag "$text"; then
                hits=$((hits + 1))
                c_fail R6b "publish without an explicit RID (-r): ${wf#"$root"/}:$at" \
                    "the shared restore is RID-neutral (UseDefaultPublishRuntimeIdentifier=false), so the net10.0/<rid> assets do not exist until a RID-aware restore runs — and an implicit host RID is what makes the resolution host-specific" \
                    'add -r <rid> (or -p:RuntimeIdentifier=<rid>) to the publish'
            fi
            if has_flag "$text" '(^|[[:space:]])--no-restore([[:space:]]|$)'; then
                hits=$((hits + 1))
                c_fail R6c "publish with --no-restore: ${wf#"$root"/}:$at" \
                    "the AOT publish needs net10.0/<rid> assets a RID-less restore never produced; the step fails with NETSDK1047 ('assets file does not have a target for net10.0/<rid>')" \
                    'drop --no-restore from the publish (or run a RID-scoped restore immediately before it)'
            fi
        done < <(run_commands "$wf")
    done < <(find_files "$root" '*.yml' | grep '/.github/workflows/')
}

check_r7() { # ROOT
    local root=$1 f hits=0 out
    while IFS= read -r f; do
        [ -n "$f" ] || continue
        out=$(xml_comments_with_dashes "$f")
        [ -z "$out" ] && continue
        hits=$((hits + 1))
        c_fail R7 "XML comment contains '--': ${f#"$root"/}" \
            "a '--' inside an XML comment is illegal XML, and MSBuild drops the whole imported file SILENTLY — the symptom is not a syntax error, it is NU1015 (a package with no version) or a switch that quietly stopped existing" \
            "rewrite the comment without a double hyphen (use ' - ' or '—'), then verify: grep -n '<PackageVersion' ${f#"$root"/}"
        printf '%s\n' "$out" | head -5 | while IFS= read -r l; do printf '         at  : %s\n' "$l"; done
    done < <(find_files "$root" '*.props'; find_files "$root" '*.targets')
    [ "$hits" -eq 0 ] && c_pass R7 "no '--' inside any XML comment (.props/.targets)"
}

check_r7b() { # ROOT — extra assertion (see SKILL.md R7 trap 2)
    local root=$1 f hits=0
    # The two flags live in variables on purpose: this file is itself a *.sh
    # inside the scanned root, so the same rule written with the literals on
    # one line reports the checker against the checker.
    local f_eval='--force-evaluate'
    local f_locked='--locked-mode'
    while IFS= read -r f; do
        [ -n "$f" ] || continue
        case $f in *.yml|*.yaml|*.sh|*.ps1) ;; *) continue ;; esac
        if grep -q -- "$f_eval" "$f" 2>/dev/null && grep -q -- "$f_locked" "$f" 2>/dev/null; then
            # Only a defect when both appear in one command, so check per line.
            while IFS= read -r l; do
                case $l in *"$f_eval"*"$f_locked"*|*"$f_locked"*"$f_eval"*)
                    hits=$((hits + 1))
                    c_fail R7b "$f_locked and $f_eval in one command: ${f#"$root"/}" \
                        "force-evaluate silently wins and rewrites the lock — the step looks locked and is not" \
                        "split them: drop $f_locked entirely (nothing is committed to lock against, R5c); $f_eval is the LOCAL command that rewrites the record" ;;
                esac
            done < <(grep -n -- '--force-evaluate' "$f")
        fi
    done < <(find_files "$root" '*.yml'; find_files "$root" '*.yaml'; find_files "$root" '*.sh'; find_files "$root" '*.ps1')
    [ "$hits" -eq 0 ] && c_pass R7b "no command combines $f_locked with $f_eval"
}

# ── driver ──────────────────────────────────────────────────────────────────

check_repo() {
    local root=$1 props build
    root=${root%/}
    root_name=$(basename "$root")
    props="$root/Directory.Packages.props"
    build="$root/Directory.Build.props"

    printf '\n=== %s\n' "$root_name"
    printf '    %s\n' "$root"

    if [ ! -f "$props" ]; then
        c_fail R0 "no Directory.Packages.props in $root" \
            "central package management is the only place a version may be declared; without the file every csproj owns its own pin" \
            'add Directory.Packages.props with ManagePackageVersionsCentrally=true'
        return
    fi
    if [ ! -f "$build" ]; then
        c_fail R5a "no Directory.Build.props in $root" \
            "the shared restore/AOT switches live there" \
            'add Directory.Build.props'
        build=/dev/null
    fi

    check_r1 "$root" "$props"
    check_r2 "$root" "$props"
    check_r3 "$root" "$props"
    check_r4 "$root" "$props" "$build"
    check_r5 "$root" "$build"
    check_r6 "$root" "$build"
    check_r7 "$root"
    check_r7b "$root"
}

main() {
    local roots=("$@")
    [ ${#roots[@]} -eq 0 ] && roots=('.')

    local r
    for r in "${roots[@]}"; do
        if [ ! -d "$r" ]; then
            printf 'check-dependency-policy: not a directory: %s\n' "$r" >&2
            exit 2
        fi
        check_repo "$r"
    done

    printf '\n──────────────────────────────────────────────────────────────\n'
    printf 'rules: %s\n' "$SELF_RULES"
    printf 'passed %d, failed %d\n' "$pass_count" "$fail_count"
    if [ "$fail_count" -gt 0 ]; then
        printf 'RESULT: FAIL — %d assertion(s) violated\n' "$fail_count"
        exit 1
    fi
    printf 'RESULT: PASS\n'
    exit 0
}

main "$@"
