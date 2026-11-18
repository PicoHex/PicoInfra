# Releasing PicoInfra

This file covers **what is specific to this repository**. It is not the release procedure.

## The authority: the `picohex-release` skill

Version, tag and baseline decisions come from `picohex-release`, not from anywhere in this repo:

- **The version rule.** `<year>.<x>.<y>`; `x` (and `y` → 0) when the public API changed, `y` alone
  when it did not. "API changed" includes additions. All packages share one version.
- **`-DryRun` before anything else.** It prints the last release, the per-package delta with the
  exact added and removed lines, and the next version. A removed line is a breaking change.
  `-DryRun` is a verb of the **skill script** (`picohex-release`'s own `scripts/release.ps1`) — not of
  anything in this repository. `scripts/api-surface/` is a bare extractor with three verbs, `dump`
  (one assembly → surface lines), `diff` (baseline vs current) and `next-version` (the version rule);
  it has no dry run and walks nothing. This repo's `scripts/release.ps1` is the packer, and it has no
  `-DryRun` either (see the table below).
- **The documentation walk.** The printed delta is the list of every document that is now lying —
  READMEs, the wire description, and the `picohex-*` skills, which live outside this repository.
  `docs/release-notes/` and the `AnalyzerReleases.*` pair in every `*.Gen` that ships rules are
  part of that walk: the `Unshipped` rows are promoted into `Shipped` on the tag that publishes
  them (see "Analyzer release tracking" below).
- **Apply, then push.** Applying refreshes `api/`, commits `chore(release): v<version>` when the
  baselines moved, and creates the annotated tag.

The skill's own tool is the one that answers "what is next". Do not compute a version by hand here
and do not infer it from a date.

## What is specific to this repository

| Piece | What it is |
|---|---|
| `.github/workflows/release.yml` | The packer that publishes. Triggered by a `v*` tag (or `workflow_dispatch`, whose `backfill_run_id` input runs the symbol backfill job instead of the release chain — see *Symbols* below). It runs every test project, then packs in three phases (abstractions with `UseProjectReferences=true`, consumers against the staged feed, then the rest), strips the leading `v` from the tag to get `-p:Version`, uploads the `.nupkg`/`.snupkg` set, and on the `publish` job logs into nuget.org by OIDC, pushes the `.nupkg` set with `--skip-duplicate`, then the `.snupkg` set the same way, and creates the GitHub release whose body is `docs/release-notes/<tag>.md` followed by the auto-generated compare link (the step fails when that file is missing, so a release page can never ship without notes). |
| `scripts/release.ps1` | The local packer: `-Version <x.y.z>` (mandatory), optional `-SkipTests`, `-NoPush`. Packs the same three phases into `artifacts/nupkg`, then creates an annotated tag and pushes branch + tag. It refuses a non-numeric version, a tag that already exists, and a dirty tree. It **is not** the skill's `release.ps1`: it has no `-DryRun` and no `-Bootstrap`, and it does not decide a version. |
| `scripts/check-release-parity.sh` | Drift guard: the project sets packed by `release.ps1` and by `release.yml` must be identical, and so must the test-project sets. Runs `dotnet pack`/`dotnet test` target extraction, exits non-zero on any difference. Run it after touching either side — drift here is silent, and once left PicoSchedule out of the local feed. |
| `artifacts/nupkg` | The local folder feed. Restored automatically when the folder exists (`Directory.Build.props` adds it to `RestoreAdditionalProjectSources`). It exists because nuget.org needs minutes to index a new version while sibling repos (PicoActor, PicoNode, PicoAgent, …) restore by `PackageReference`. Same bits as CI, served sooner. |
| `tools/release-build.sh` | Local verification build: refuses a branch that is not `main` (`exit 2`) and a dirty tree (`exit 3`), prints the exact `HEAD` it built from, and writes artifacts **outside** the repository (`OUT`, default `$TMPDIR/rel-<short sha>`). Its job is proof of provenance, not publication. |

## Symbols — `pack` emits a `.snupkg` per package, and a published version can never gain them

`IncludeSymbols` / `SymbolPackageFormat=snupkg` are declared in `Directory.Build.targets:14-18`, not
in `Directory.Build.props`, and which of the two files they live in *is* the point: the block's
condition is `'$(IsPackable)' == 'true'`, properties evaluate in document order, and while
`Directory.Build.props` is being read `IsPackable` is still the `false` that the same file sets at
`:6` — the csproj body that sets it to `true` is read *afterwards*. In `.props` the condition saw
`false` for every project, the block silently never ran, and a plain `dotnet pack` emitted no
`.snupkg` at all: that is the real reason `v2026.11.0` shipped 20 packages and zero symbol packages,
and no publish loop could have pushed symbols that were never produced. Item groups were never
affected — MSBuild evaluates items in a later pass with the final property values — which is why the
SourceLink/icon `ItemGroup` under the same condition stayed in `.props`, and why
`AotOptimizationLevel` in the same `.targets` file already followed this rule.

Two probes answer "did the block fire". `dotnet msbuild PicoDI/src/PicoDI.Abs/PicoDI.Abs.csproj
-getProperty:IncludeSymbols -getProperty:SymbolPackageFormat` reports `true` / `snupkg` for a
packable project and empty / `symbols.nupkg` (the NuGet default) for `PicoLog.Tests`; and a
`dotnet pack` with no symbol property on the command line now leaves a `.snupkg` beside each
`.nupkg`.

### Symbols cannot be retro-added to a published version

A `.nupkg` embeds the identity of the PDB it was built with: the CodeView record of the shipped DLL
pins that PDB's id, and **PDB ID = CodeView GUID is the only authoritative, checkable link** — a
`.snupkg` carries no hash of its `.nupkg`, and the DLL's `PDB_CHECKSUM` is not the PDB file's
SHA-256 (the control below settles both). A rebuild from the same tag never reaches that id: the two
assemblies carry byte-identical IL and differ in **71 bytes over 5 ranges** — PE `TimeDateStamp`,
module MVID, CodeView timestamp, CodeView GUID, `PDB_CHECKSUM`. That is build identity, stamped per
toolchain instance. Two consecutive packs of one tag are byte-identical *to each other* (reproducible
pack; `CI=true`, SourceLink at `107f54c` for the probe below) and still are not the published bits.

Measured on `v2026.11.0` — published 2026-11-06 with 20 `.nupkg` and **0 `.snupkg`** — by dispatching
the old re-packing backfill (`release.yml`, run `44340046281`):

| version | PDB id the published DLL wants | PDB id a rebuild of the tag yields |
|---|---|---|
| `2026.11.0` | `ef12ed63f9051a4ab52fafd95e519ba6` | `430acd12439bf04a9b064fafdc7f8714` — **no match** |
| `2026.11.1` (control) | `1177b170…` | matches |
| `2026.11.2` (control, symbols shipped with the release) | `554250cb0d5ad84fa57468530b2dad17` | snupkg `#Pdb` `554250cb0d5ad84fa57468530b2dad17c09a84ad` — matches |

The push does not fail, which is the trap. All 20 symbol pushes returned **`201 Created`** and the
job reported `20 symbol packages pushed, 0 skipped`. Nothing ever became retrievable: **21 polling
rounds over 94 minutes, `0/20` at HTTP 200**, body `<Code>BlobNotFound</Code>`, while the `2026.11.2`
control was **`20/20` at HTTP 200** in the same rounds. nuget.org accepts a symbol package for a
version it already has and then cannot serve it, because no published binary is bound to it. A green
backfill therefore proves nothing, which is why the job now refuses instead of packing.

### What to do instead, in this order

1. **The failed release's own pack artifacts still exist** (the `nupkg` artifact of *that* run) → push
   **those** `.snupkg` files. They were built in the same run that published the `.nupkg`s, so their
   PDBs *are* the published bits'. Never a rebuild. This is the one case the job handles.
2. **Otherwise the only public remedy is a new version.** That is `v2026.11.0`'s situation: its
   release run emitted no `.snupkg` at all, so no artifact of it can carry a matching PDB, and no
   amount of work on that version can produce one.
3. **A private symbol server** can hold the rebuilt `.snupkg` files for internal debugging, labelled
   as *not* matching the published binaries. Optional, and never a substitute for 1 or 2.

### The `backfill-symbols` job replays a release run's artifact — it does not pack

It lives **inside `release.yml`** and cannot be its own workflow file: nuget.org's trusted-publishing
policy for this repository names `release.yml`, and `NuGet/login` enforces the name — a standalone
`backfill-symbols.yml` packed all 40 files correctly in run 44332758225 and then died with
`Token exchange failed (HTTP 401) … Workflow mismatch for policy 'PicoInfra': expected
'release.yml', actual 'backfill-symbols.yml'`. The policy names one file: do not move it back out.

The job is gated on the `backfill_run_id` dispatch input, which names **the run whose artifact must
be replayed** — not a tag, because a tag is exactly what cannot be rebuilt. Empty is the pre-existing
behaviour: `pack` → `publish`, version parsed from the ref. Set, and `pack` stands down and `publish`
with it through `needs`, so the run is the backfill alone. It downloads that run's `nupkg` artifact
(the one `pack` uploads: 20 `.nupkg` + 20 `.snupkg`), keeps only the `.snupkg` files, and pushes them
with `--skip-duplicate`. It refuses loudly, naming the new-version remedy, when the run has no
`nupkg` artifact at all or when that artifact holds no `.snupkg` — the `v2026.11.0` shape — and it
refuses a `.nupkg` that would reach the push loop and a symbol whose version does not match. The push
step's `working-directory` is the directory the download lands in; globbing the workspace root
instead is what once made this job stand ready to announce "the pack produced no symbols" about a
directory it had never listed.

```shell
gh workflow run release.yml --ref main -f backfill_run_id=<run id> -f tag=v2026.11.2
```

`tag` is optional and only labels the log and the job summary; the version that is pushed comes from
the symbol file names and is cross-checked against it. Find the id with `gh run list --workflow
release.yml`.

**Nothing gates it.** `release.yml` only *reports* the symbol count — `Publishing N package(s) and M
symbol package(s)` (`:324`) and the same line after the pushes (`:363`) — so a release can ship 20
packages and 0 `.snupkg` and stay green. The fix is in the pack, not in a gate. And once the release
run is gone, the symbols are gone with it: `v2026.11.0` has no matching `.snupkg` anywhere, so a
consumer of that version gets source-stepping from a new version or not at all.

## Analyzer release tracking — the `Shipped` half is per release, the `Unshipped` half is per commit

Four generators ship rule IDs a consumer build can hit, and all four now declare an
`AnalyzerReleases.Unshipped.md` + `AnalyzerReleases.Shipped.md` pair as `AdditionalFiles`:
`PicoAop.Gen` (7 rules), `PicoCfg.Gen` (10), `PicoDI.Gen` (5), `PicoMediator.Gen` (1) — **23 rules**
(`PicoLog.Gen` ships none and has neither file). A `_RequireAnalyzerReleasesFile` target in each of
those four csprojs fails the build when either file is missing. That target is the **only**
machine-enforced half: no RS diagnostic fires here, because `Microsoft.CodeAnalysis.Analyzers`
reaches these projects with `build` assets only, never an `analyzers` asset, so the
release-tracking analyzer never loads (measured: deleting both `AdditionalFiles` items from
`PicoAop.Gen.csproj` and rebuilding leaves the warning count unchanged, and rewording a
descriptor produces no diagnostic either). The files are a record first, a gate second.

- **The split.** `Unshipped.md` holds the rows added, reworded or retired since the last release.
  `Shipped.md` holds one `## Release <version>` section per tag, with the rows that first shipped in
  it. Same columns in both: `Rule ID | Category | Severity | Notes`.
- **Promote at the release**, in the same commit that writes `docs/release-notes/<tag>.md`: move
  every `### New Rules` row out of `Unshipped.md` into a `## Release <version>` section of
  `Shipped.md`, leave `Unshipped.md`'s tables empty, and put any reworded or retired rule under
  `### Changed Rules` / `### Removed Rules` in `Unshipped.md` for the *next* release to promote.
- **`<version>` is the release tag without the `v` — never a project's version.** One tag is one
  `<year>.<x>.<y>` for every package, so there is no separate generator version to read. The `.Gen`
  projects cannot supply one anyway: all five are `IsPackable=false`, and the generator DLL reaches
  consumers only as `analyzers/dotnet/cs` inside `PicoX.Abs` (`IsPackable=true`, which packs the
  generator DLL into it with `PackagePath="analyzers/dotnet/cs"`). **A rule ships when the `.Abs`
  package carrying it is tagged**, and that is the same tag either way.
- **Why at the release and not later.** `Shipped.md` is what makes a rule *old*. A rule parked in
  `Unshipped.md` after it has reached nuget.org has no baseline, so the next reword or removal of a
  consumer-facing ID is no longer visible as such — the precise rot the tracking exists to prevent.
- **The first entries are archaeology, and each file says so.** No `AnalyzerReleases.*` file existed
  at any tag up to and including `v2026.11.0` (the `.Abs` packages packed the generator, nothing
  tracked the IDs); the tracking landed in `b5caf5b` (2026-11-12) and the missing-file guard in
  `0a52b39`, six days after the `v2026.11.0` tag (2026-11-06). The `Shipped.md` headers therefore
  record how each `## Release` heading was obtained — one `git grep` per rule ID per tag, oldest tag
  first, plus a check that the tag published a package carrying the generator — instead of implying a
  promotion someone performed. Every rule is dated to its **real** first tag: 14 in `v2026.1.1` (as
  the standalone `PicoCfg.Gen`/`PicoDI.Gen` packages, which were `IsPackable=true` then and in the
  release pack list), 4 in `v2026.1.2`, 1 in `v2026.8.1`, 1 in `v2026.8.6`, 3 in `v2026.11.0`
  (`PICO111`, `PICO112`, `PICO113`). Dating all 23 to `v2026.11.0` because the *tracking* started
  there would have been wrong for 20 of them — by 42–43 releases for the 18 that shipped in
  `v2026.1.1` / `v2026.1.2`, and by 11–16 for the two that shipped in `v2026.8.1` / `v2026.8.6`.
  From here on the promotion is the ordinary case: whatever `Unshipped.md` holds when the tag is
  cut.

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
- **CI enforces this** — `.github/workflows/ci.yml`, job `api-baselines`, which runs
  `scripts/check-api-baselines.sh` on every PR and push. The two steps are the design:
  - `verify` **fails** when `api/` differs from `api/` at the newest release tag reachable from
    `HEAD`. A release is the only thing that writes these files, so *any* difference is a
    hand-edit — and a hand-edit used to be invisible until a human ran the release tool, which
    then reads the delta as "API unchanged" and answers with a `y` bump where an `x` is due. It
    costs no build and cannot fail on ordinary development: `api/` lagging `main` is the point.
  - `report` **does not fail** on a surface change, on purpose. It builds the tree, extracts every
    packable surface with `scripts/api-surface`, posts the per-package delta against `api/` and the
    version that delta implies to the job summary, and warns when the surface moved. Failing here
    would mean `main` is red from a breaking commit until its release — four days around
    `v2026.11.0` — and the cheapest way to silence it is the early refresh this section forbids.
    It fails only when it cannot extract at all (build or tool failure), because a gate that skips
    silently is indistinguishable from one that checked.
- **Run the dry run and the apply step from a checkout with no live `.worktrees/*`.** It is the skill
  script's project discovery that recurses the repository root for `*.csproj`
  (`Get-PackableProjects`), so a nested checkout under `.worktrees/` contributes a second copy of
  every packable project and the script stops with `duplicate PackageId across packable projects`.
  The extractor is not the walker — `dump` takes one assembly path, `diff` two files — so the caveat
  belongs to the script, not to the tool. Remove the worktree rather than working around it by
  copying the repository elsewhere.

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
