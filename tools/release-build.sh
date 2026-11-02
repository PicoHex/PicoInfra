#!/usr/bin/env bash
# tools/release-build.sh -- the only sanctioned way to produce PicoInfra release bits.
#
# Why this gate exists
# --------------------
# A fresh timestamp is not evidence of fresh code. PicoInfra packages have been
# packed from a side branch before: the .nupkg was minutes old and still 69
# commits behind main, so sibling repos (PicoActor, PicoNode, PicoAgent, ...)
# consumed a build that predated shipped product fixes. Ordering people to
# "please pack from main" does not survive a deadline; a gate at the packing
# step does. This script refuses to run unless HEAD is a clean checkout of
# `main`, and it writes every artifact outside the repository so a stale
# in-tree bin/ or artifacts/ folder can never be mistaken for what was built.
#
# Usage
#   bash tools/release-build.sh                 # version 0.0.0-dev (local build)
#   bash tools/release-build.sh 2026.12.2       # stamp a concrete version
#   VERSION=2026.12.2 OUT=/d/rel bash tools/release-build.sh
#
# Exit codes
#   0  artifacts written; path, package count and sha256 printed
#   2  HEAD is not `main` (current branch / detached HEAD printed)
#   3  working tree is dirty (first 3 porcelain paths printed)
#   1  anything else -- no dotnet, OUT resolving inside the repo, build/pack
#      failure, no packages produced, or the build dirtied the repository
#
# Contract: docs/releasing.md ("main is the only release branch").
set -euo pipefail

readonly RELEASE_BRANCH="main"

emit() { printf '%s\n' "$*"; }
fail() {
    printf 'release-build: %s\n' "$*" >&2
    exit 1
}

command -v git >/dev/null 2>&1 || fail "REFUSED - git not found on PATH"
command -v dotnet >/dev/null 2>&1 || fail "REFUSED - dotnet not found on PATH"

repo_root="$(git rev-parse --show-toplevel 2>/dev/null)" ||
    fail "REFUSED - not inside a git work tree"
cd "$repo_root"
repo_abs="$(pwd -P)"

# ---------------------------------------------------------------------------
# R1 -- main is the only release branch.
# ---------------------------------------------------------------------------
branch="$(git rev-parse --abbrev-ref HEAD)"
if [ "$branch" != "$RELEASE_BRANCH" ]; then
    if [ "$branch" = "HEAD" ]; then
        current="detached HEAD (no branch at all)"
    else
        current="$branch"
    fi
    {
        printf 'release-build: REFUSED - release artifacts may only be built from %s.\n' "$RELEASE_BRANCH"
        printf '  current checkout : %s\n' "$current"
        printf '  why              : a branch that merely looks new ships code that is not main;\n'
        printf '                     this repo has already been burnt by a nupkg 69 commits behind main.\n'
        printf '  fix              : git checkout %s && git pull --ff-only\n' "$RELEASE_BRANCH"
    } >&2
    exit 2
fi

# ---------------------------------------------------------------------------
# R2 -- a dirty tree must never ship: the artifact would match no commit.
# ---------------------------------------------------------------------------
dirty="$(git status --porcelain)"
if [ -n "$dirty" ]; then
    readarray -t dirty_rows <<<"$dirty"
    {
        printf 'release-build: REFUSED - working tree is not clean; uncommitted code cannot be released.\n'
        printf '  %d change(s); first 3:\n' "${#dirty_rows[@]}"
        for i in 0 1 2; do
            [ "$i" -lt "${#dirty_rows[@]}" ] || break
            printf '    %s\n' "${dirty_rows[$i]}"
        done
        printf '  fix: commit or stash, then re-run.\n'
    } >&2
    exit 3
fi

# ---------------------------------------------------------------------------
# R3 -- provenance of the exact tree being packed.
# ---------------------------------------------------------------------------
head_sha="$(git rev-parse HEAD)"
short_sha="$(git rev-parse --short HEAD)"
built_at="$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
version="${1:-${VERSION:-0.0.0-dev}}"

# ---------------------------------------------------------------------------
# R4 -- artifacts live outside the repository, always.
# ---------------------------------------------------------------------------
tmp_root="${TMPDIR:-${TEMP:-/tmp}}"
out_dir="${OUT:-$tmp_root/rel-$short_sha}"
mkdir -p "$out_dir" || fail "cannot create OUT directory: $out_dir"
out_abs="$(cd "$out_dir" && pwd -P)"
case "$out_abs/" in
    "$repo_abs"/*)
        fail "REFUSED - OUT resolves inside the repository ($out_abs). Release artifacts must stay out of the tree."
        ;;
esac

nupkg_dir="$out_dir/nupkg"
artifacts_dir="$out_dir/artifacts"
build_log="$out_dir/build.log"
# Stale packages from an earlier run must never be reported as this build's output.
rm -rf -- "$nupkg_dir"
mkdir -p "$nupkg_dir" "$artifacts_dir"

emit "release-build: provenance"
emit "  branch      : $branch"
emit "  HEAD        : $head_sha"
emit "  built at    : $built_at (UTC)"
emit "  version     : $version"
emit "  artifacts   : $out_dir"
if [ "$version" = "0.0.0-dev" ]; then
    emit "  note        : no version argument, packages are stamped 0.0.0-dev (local build, not publishable)"
fi

# ---------------------------------------------------------------------------
# R6 -- build every packable project in Release; any failure is fatal.
# The package set is derived from IsPackable in the csproj files, so it cannot
# drift from the repository (CI/scripts parity is guarded by
# scripts/check-release-parity.sh).
# ---------------------------------------------------------------------------
packables=()
while IFS= read -r proj; do
    grep -qE '<IsPackable>[[:space:]]*true[[:space:]]*</IsPackable>' "$proj" || continue
    packables+=("$proj")
done < <(git ls-files '*.csproj' | sort)
[ "${#packables[@]}" -gt 0 ] || fail "no packable projects found (IsPackable=true) - repository layout changed?"

: >"$build_log"
{
    printf 'PicoInfra release build\n'
    printf 'branch=%s\nhead=%s\nbuilt_at=%s\nversion=%s\n' \
        "$branch" "$head_sha" "$built_at" "$version"
    printf 'dotnet=%s\n' "$(dotnet --version)"
} >"$out_dir/release-info.txt"

# One MSBuild node, no node reuse. The pack loop is sequential, so there is no
# parallelism to win back -- and long-lived worker nodes are what die with
# "MSB4166: child node exited prematurely" in the middle of a 20-project run
# (seen once here, not reproducible per project). That crash would abort a
# release for no reason in the code being released.
export MSBUILDDISABLENODEREUSE=1

emit "release-build: packing ${#packables[@]} project(s) in Release (log: $build_log)"
started=$SECONDS
index=0
for proj in "${packables[@]}"; do
    index=$((index + 1))
    project_started=$SECONDS
    printf '  [%2d/%d] %s ... ' "$index" "${#packables[@]}" "$proj"
    if dotnet pack "$proj" \
        --configuration Release \
        --artifacts-path "$artifacts_dir" \
        --output "$nupkg_dir" \
        -p:Version="$version" -maxcpucount:1 >>"$build_log" 2>&1; then
        printf 'ok (%ss)\n' "$((SECONDS - project_started))"
    else
        printf 'FAILED (%ss)\n' "$((SECONDS - project_started))"
        {
            printf 'release-build: BUILD FAILED - dotnet pack %s\n' "$proj"
            printf '  full log: %s\n' "$build_log"
            printf '  last 40 lines:\n'
        } >&2
        tail -n 40 "$build_log" >&2 || true
        exit 1
    fi
done
emit "release-build: build finished in $((SECONDS - started))s"

# ---------------------------------------------------------------------------
# R4 (verified) -- the build itself must not have touched the repository.
# ---------------------------------------------------------------------------
leftovers="$(git status --porcelain)"
if [ -n "$leftovers" ]; then
    {
        printf 'release-build: FAILED - the build dirtied the repository, so the artifact is not reproducible from HEAD.\n'
        printf '%s\n' "$leftovers" | while IFS= read -r row; do printf '  %s\n' "$row"; done
    } >&2
    exit 1
fi

# ---------------------------------------------------------------------------
# R5 -- the primary artifact is a single bundle of the packable set: one path,
# one sha256, plus per-package hashes for auditing.
# ---------------------------------------------------------------------------
shopt -s nullglob
nupkgs=("$nupkg_dir"/*.nupkg)
symbols=("$nupkg_dir"/*.snupkg)
shopt -u nullglob
[ "${#nupkgs[@]}" -gt 0 ] || fail "no .nupkg produced in $nupkg_dir - nothing to release"

sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | cut -d' ' -f1
    elif command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$1" | cut -d' ' -f1
    else
        openssl dgst -sha256 "$1" | tr -s ' ' | rev | cut -d' ' -f1 | rev
    fi
}

bundle="$out_dir/PicoInfra-$version-$short_sha.nupkg.tar.gz"
rm -f -- "$bundle"
tar -czf "$bundle" -C "$nupkg_dir" . || fail "could not bundle the packages into $bundle"
primary_sha="$(sha256_of "$bundle")"

sha_file="$out_dir/SHA256SUMS"
: >"$sha_file"
{
    printf '%s  %s\n' "$primary_sha" "$(basename "$bundle")"
    for pkg in "${nupkgs[@]}"; do
        printf '%s  %s\n' "$(sha256_of "$pkg")" "$(basename "$pkg")"
    done
    for sym in "${symbols[@]}"; do
        printf '%s  %s\n' "$(sha256_of "$sym")" "$(basename "$sym")"
    done
} >>"$sha_file"

emit "release-build: OK"
emit "  primary     : $bundle"
emit "  sha256      : $primary_sha"
emit "  packages    : ${#nupkgs[@]} .nupkg + ${#symbols[@]} .snupkg in $nupkg_dir"
emit "  per-package : $sha_file"
emit "  repo state  : clean (git status --porcelain empty)"
emit "  next        : publish only from this bundle, tagged on $RELEASE_BRANCH"
