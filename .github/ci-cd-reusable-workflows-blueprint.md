# PicoHex Reusable Workflow Blueprint

This blueprint is written for a shared workflow repository such as `PicoHex/.github` and tuned for PicoHex projects that standardize on `.NET 10`.

## Detected Shape For This Repo

- Tier: `Tier 3`
- Reason: this repo contains many packable libraries, a source generator, tests, and AOT-oriented samples
- Packaging note: Tier 3 rules should include Tier 2 source-generator pack ordering for `Pico.DI.Abs`, `Pico.DI.SourceGen`, and `Pico.DI`

## Recommended Shared Repo Layout

Put the final reusable workflows here:

```text
PicoHex/.github/
└── .github/
    └── workflows/
        ├── picohex-dotnet-ci.yml
        ├── picohex-dotnet-release.yml
        └── picohex-dotnet-aot.yml
```

Keep thin consumer workflows inside each product repository.

## Design Goals

- standardize on `.NET 10`
- centralize checkout, setup-dotnet, caching, permissions, concurrency, artifact handling, and NuGet publish logic
- keep repository-specific build, pack, and AOT commands in thin consumer wrappers
- support Tier 1, Tier 2, and Tier 3 without forcing every repo into the same pack graph

## Blueprint Files In This Repo

- `.github/blueprints/picohex-dotnet-ci.reusable.yml`
- `.github/blueprints/picohex-dotnet-release.reusable.yml`
- `.github/blueprints/picohex-dotnet-aot.reusable.yml`
- `.github/blueprints/examples/tier1-library.example.yml`
- `.github/blueprints/examples/tier2-sourcegen.example.yml`
- `.github/blueprints/examples/tier3-framework.example.yml`

These are blueprint files, not active workflows.

## Contract Strategy

The reusable workflows accept repo-specific shell commands as inputs.

Why this shape:

- build and test commands differ across PicoHex repositories
- source-generator pack order is repo-specific
- AOT validation targets differ by repo and sample path
- the shared workflow should own platform plumbing, not guess each repo's internal topology

## Recommended Adoption For This Repo

Use these consumer wrappers after moving the reusable workflows into `PicoHex/.github`:

- CI wrapper should call `picohex-dotnet-ci.yml`
- Release wrapper should call `picohex-dotnet-release.yml`
- Optional tag-only AOT wrapper should call `picohex-dotnet-aot.yml`

Suggested repo-specific commands for `PicoInfra`:

```bash
dotnet restore PicoInfra.slnx
dotnet build PicoInfra.slnx --configuration Release --no-restore
dotnet test PicoInfra.slnx --configuration Release --no-build --verbosity normal
```

Suggested release pack order for the DI chain:

```bash
dotnet pack src/DependencyInjection/Pico.DI.Abs/Pico.DI.Abs.csproj --configuration Release --output ./artifacts/nupkg -p:Version=$VERSION -p:UseProjectReferences=false
dotnet pack src/DependencyInjection/Pico.DI.SourceGen/Pico.DI.SourceGen.csproj --configuration Release --output ./artifacts/nupkg -p:Version=$VERSION -p:UseProjectReferences=false -p:RestoreAdditionalProjectSources=./artifacts/nupkg
dotnet pack src/DependencyInjection/Pico.DI/Pico.DI.csproj --configuration Release --output ./artifacts/nupkg -p:Version=$VERSION -p:UseProjectReferences=false -p:RestoreSources=./artifacts/nupkg
```

Suggested first AOT validation target:

```bash
dotnet publish samples/Pico.DI.Aot.Sample/Pico.DI.Aot.Sample.csproj -c Release -r linux-x64 -p:PublishAot=true --self-contained -o ./artifacts/aot/linux-x64
```

## Implementation Notes

- keep `.NET 10` as `10.0.x` in reusable workflows unless a repo intentionally pins a preview or exact SDK in `global.json`
- prefer `fetch-depth: 0` in reusable workflows because SourceLink and release metadata both benefit from full history
- default permissions to `contents: read`; raise to `contents: write` only in publish or release jobs
- use `secrets: inherit` only when repo policy allows it; otherwise pass `NUGET_API_KEY` explicitly
- keep release separate from CI even for small repos

## Next Move

If you want to operationalize this blueprint, copy the reusable YAML files into `PicoHex/.github/.github/workflows/` and then add one thin consumer workflow per repository.
