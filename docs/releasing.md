# Releasing PicoInfra

**`main` is the only release branch.** Not "preferably main" — the only one.
Nothing that becomes a `.nupkg` on nuget.org may be packed from anywhere else.

## The only supported way to produce release bits

```bash
git checkout main
git pull --ff-only
bash tools/release-build.sh 2026.12.2   # version is optional: defaults to 0.0.0-dev
```

`tools/release-build.sh` is the sole sanctioned entry point. It refuses to run
unless `HEAD` is `main`, refuses to run on a dirty tree, prints the commit it
built from, and writes every artifact **outside the repository**
(`OUT=${OUT:-$TMPDIR/rel-<short sha>}`), so a stale in-tree `bin/`, `obj/` or
`artifacts/` folder can never be mistaken for what was just built.

| Exit | Meaning |
|------|---------|
| `0` | packages built; path, count and the bundle `sha256` printed |
| `2` | not on `main` (branch name or detached `HEAD` printed) |
| `3` | working tree dirty (first 3 porcelain paths printed) |
| other non-zero | build/pack failed, or the build dirtied the repo — treat as "no release" |

`scripts/release.ps1` (local folder feed + tag) and `.github/workflows/release.yml`
(tag → nuget.org) still exist, but they are *downstream* of this gate: they pack
the tree, so they inherit whatever tree you gave them.

## Three checks before you call it released

1. **You are on main, and main is what you think it is.**
   `git rev-parse --abbrev-ref HEAD` prints `main`, and
   `git log --oneline origin/main..HEAD` is empty — nothing released that is not
   already pushed to `main`.
2. **The tree is clean.** `git status --porcelain` prints nothing. A release must
   correspond to exactly one commit; if `HEAD` is not the code, the artifact has
   no provenance at all.
3. **The build says which commit it built.** `bash tools/release-build.sh <version>`
   exits `0`, and the `HEAD` line it prints equals `git rev-parse HEAD`. Paste that
   sha into the release notes: it is the only trustworthy answer to "what is in
   this package?".

## Why this rule exists

A release was once packed from a side branch. The `.nupkg` was minutes old — the
timestamp said "brand new" — and it was **69 commits behind `main`**. Every
sibling repo (PicoActor, PicoNode, PicoAgent, …) that consumed it built on code
that predated fixes already shipped upstream. Nothing failed loudly: the version
looked current, the build was green, the file was fresh. Recency of the *file* is
not recency of the *code*, and nobody can tell the difference by looking at the
package.

A convention ("please pack from main") does not survive a deadline. A check at
the packing step does — it turns a silent mistake into a two-second refusal
(`exit 2`), with the branch name in the message so the fix is obvious.

## "Side branches only carry CI work" is not a safe assumption either

This repository has had **three product commits stranded on a side branch** — a
dependency upgrade and a YAML sequence-parsing fix among them. They were real
user-facing changes, not infrastructure tinkering, and they sat outside `main`
while releases were cut from `main`. So the usual reassurance — *"don't worry,
the side branch is only CI plumbing"* — is exactly the assumption that fails:
you cannot know what a branch holds without reading it, and by the time you have
read it, you could have merged it.

Two rules follow, and they are cheap:

- Release from `main`, always. If the fix is not in `main`, it is not released.
- Merge to `main` before packing, not after. A branch that carries product code
  is a branch that must land — the release gate exists to make that impossible to
  forget rather than merely advisable to remember.
