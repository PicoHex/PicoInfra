# Releasing PicoInfra

This file covers **what is specific to this repository**. It is not the release procedure.

## The authority: the `picohex-release` skill

Version, tag and baseline decisions come from `picohex-release`, not from anywhere in this repo:

- **The version rule.** `<year>.<x>.<y>`; `x` (and `y` → 0) when the public API changed, `y` alone
  when it did not. "API changed" includes additions. All packages share one version.
- **`-DryRun` before anything else.** It prints the last release, the per-package delta with the
  exact added and removed lines, and the next version. A removed line is a breaking change. In this
  repository that dry run is the skill's tool under `scripts/api-surface/` — **not**
  `scripts/release.ps1`, which has no `-DryRun` (see the table below).
- **The documentation walk.** The printed delta is the list of every document that is now lying —
  READMEs, the wire description, and the `picohex-*` skills, which live outside this repository.
  `docs/release-notes/` and `AnalyzerReleases.Unshipped.md` are part of that walk.
- **Apply, then push.** Applying refreshes `api/`, commits `chore(release): v<version>` when the
  baselines moved, and creates the annotated tag.

The skill's own tool is the one that answers "what is next". Do not compute a version by hand here
and do not infer it from a date.

## What is specific to this repository

| Piece | What it is |
|---|---|
| `.github/workflows/release.yml` | The packer that publishes. Triggered by a `v*` tag (or `workflow_dispatch`). It runs every test project, then packs in three phases (abstractions with `UseProjectReferences=true`, consumers against the staged feed, then the rest), strips the leading `v` from the tag to get `-p:Version`, uploads the `.nupkg`/`.snupkg` set, and on the `publish` job logs into nuget.org by OIDC, pushes with `--skip-duplicate`, and creates the GitHub release with auto-generated notes. |
| `scripts/release.ps1` | The local packer: `-Version <x.y.z>` (mandatory), optional `-SkipTests`, `-NoPush`. Packs the same three phases into `artifacts/nupkg`, then creates an annotated tag and pushes branch + tag. It refuses a non-numeric version, a tag that already exists, and a dirty tree. It **is not** the skill's `release.ps1`: it has no `-DryRun` and no `-Bootstrap`, and it does not decide a version. |
| `scripts/check-release-parity.sh` | Drift guard: the project sets packed by `release.ps1` and by `release.yml` must be identical, and so must the test-project sets. Runs `dotnet pack`/`dotnet test` target extraction, exits non-zero on any difference. Run it after touching either side — drift here is silent, and once left PicoSchedule out of the local feed. |
| `artifacts/nupkg` | The local folder feed. Restored automatically when the folder exists (`Directory.Build.props` adds it to `RestoreAdditionalProjectSources`). It exists because nuget.org needs minutes to index a new version while sibling repos (PicoActor, PicoNode, PicoAgent, …) restore by `PackageReference`. Same bits as CI, served sooner. |
| `tools/release-build.sh` | Local verification build: refuses a branch that is not `main` (`exit 2`) and a dirty tree (`exit 3`), prints the exact `HEAD` it built from, and writes artifacts **outside** the repository (`OUT`, default `$TMPDIR/rel-<short sha>`). Its job is proof of provenance, not publication. |

## `api/` baselines and `scripts/api-surface/` are generated — never hand-edit

`api/<PackageId>.public.txt` (one file per packable project — 20 of them) is **the last release's
public API surface**, and `scripts/api-surface/` is the zero-dependency extractor that produces and
diffs it. They arrive with the release-prep work, merged to `main` as `24c72c5` — after the commit
this branch was cut from (`5f85fc2`).

- **Do not edit a baseline.** It is generated output. Editing it makes every later comparison start
  from a surface no release ever had, and the tool cannot tell the difference.
- **Do not refresh `api/` before running the dry run.** If the baselines already match the working
  tree, the tool reports "API unchanged" and answers with a `y` bump where an `x` is due. Refresh
  happens as part of applying the release, not before it.
- Adding or removing a packable project changes the baseline set itself — that is a deliberate act,
  not a side effect of a build.
- **Run the dry run and the apply step from a checkout with no live `.worktrees/*`.** The extractor
  recurses the repository root for `*.csproj`, so a nested checkout under `.worktrees/` contributes a
  second copy of every packable project and the tool stops with `duplicate PackageId across packable
  projects`. Remove the worktree rather than working around it by copying the repository elsewhere.

## `main` is the only release branch

Not "preferably main" — the only one. A `.nupkg` carries its **pack** time, not its code's: this
repository has shipped a package that was minutes old and **69 commits behind `main`**, and nothing
failed, because the file was fresh and the version looked current. A convention does not survive a
deadline; `tools/release-build.sh` refuses with `exit 2` (wrong branch) or `exit 3` (dirty tree), and
`scripts/release.ps1` refuses a dirty tree and a tag that already exists — naming the branch or the
first dirty paths so the fix is obvious.

The same rule applies to work that is still on a branch: a side branch in this repository has
carried real product commits (a dependency upgrade, a YAML parsing fix) while releases were cut from
`main`. If a change is not in `main`, it is not released.

## Why this file was rewritten

It used to present the local packaging flow as the whole story: no version rule, no `-DryRun`, no
API baseline, and `tools/release-build.sh` described as "the only supported way to produce release
bits". A reader could follow it top to bottom and never learn that a version is decided from an API
diff. That is the misleading part, and it is what changed — the repo-specific facts above were kept,
the procedure now points at the skill that owns it.
