# Contributing to PicoInfra

Thanks for your interest in contributing. PicoInfra is a minimal, AOT-first infrastructure library for .NET. This guide covers the basics to get you started.

## Build Commands

Build the entire solution:

```shell
dotnet build PicoInfra.slnx
```

Build with AOT validation:

```shell
dotnet build PicoInfra.slnx -p:PublishAot=true
```

## Test Commands

Run tests for individual modules:

```shell
dotnet test PicoDI/tests/PicoDI.Test/PicoDI.Test.csproj
dotnet test PicoCfg/tests/PicoCfg.Tests/PicoCfg.Tests.csproj
dotnet test PicoCfg/tests/PicoCfg.DI.Tests/PicoCfg.DI.Tests.csproj
dotnet test PicoCfg/tests/PicoCfg.Gen.Tests/PicoCfg.Gen.Tests.csproj
dotnet test PicoLog/tests/PicoLog.Tests/PicoLog.Tests.csproj
```

Or run all tests at once (slower; CI runs per project instead):

```shell
dotnet test PicoInfra.slnx
```

### Testing-Platform 2.x flakiness ("Zero tests ran")

`dotnet test` against Microsoft Testing Platform 2.x can report
`Zero tests ran` (exit code 5/8) or hang. Two verified causes:

1. **Do not pass `--nologo` to `dotnet test`** on .NET 10 SDK (10.0.400 verified):
   the new test experience reports `Zero tests ran` for every project when
   `--nologo` is present. Use `--no-progress` (or `-v q`) instead — CI does not
   pass `--nologo`.
2. Stale `testhost.exe` processes holding file locks on freshly built dlls.

Preferred per-project run (mirrors CI exactly):

```shell
dotnet build PicoDI/tests/PicoDI.Test/PicoDI.Test.csproj -c Release
dotnet test PicoDI/tests/PicoDI.Test/PicoDI.Test.csproj -c Release --no-build --no-progress
```

If the runner still misbehaves:

```shell
# Clear stale test hosts holding file locks (Windows)
taskkill //F //IM testhost.exe

# Run the test project directly through its executable. The repo defaults
# to PublishAot=true, so explicitly disable AOT for a managed test run:
dotnet run --project PicoDI/tests/PicoDI.Test/PicoDI.Test.csproj -c Release -p:PublishAot=false
```

`-p:PublishAot=false` is required because the repo default `PublishAot=true`
turns a managed test run into an AOT publish. The `dotnet run` form is the
reliable path for local development; CI uses `dotnet test` after a clean build.

## PR Workflow

1. Fork the repository on GitHub.
2. Create a feature branch from `main`:

   ```shell
   git checkout -b feat/your-feature-name
   ```

3. Make your changes. Keep commits atomic and well-described.
4. Run the build and all tests:

   ```shell
   dotnet build PicoInfra.slnx && dotnet test PicoInfra.slnx
   ```

5. Push your branch and open a pull request against `main`.
6. In the PR description, explain what the change does and why it is needed.
7. A maintainer will review your PR. Address any feedback by pushing additional commits.

## Coding Conventions

This project uses the following C# conventions:

- **Nullable enabled**: All projects have `<Nullable>enable</Nullable>`. Write null-safe code.
- **File-scoped namespaces**: Use `namespace PicoInfra.Foo;` not block-scoped namespaces.
- **Target-typed new**: Use `new()` instead of repeating the type name when the type is obvious.
- **Sealed types**: Prefer `sealed class` unless the type is designed for inheritance.
- **Primary constructors**: Use primary constructors for simple types that take dependencies.
- **No reflection**: Use source generators or factory delegates instead of `Activator.CreateInstance`, `Expression`, or runtime emit.
- **XML docs**: Public APIs must have XML doc comments. Internal and private APIs should have them where helpful.

Run CSharpier (the repo formatter, pinned in `.config/dotnet-tools.json`) before committing:

```shell
dotnet tool restore
dotnet csharpier check .              # report unformatted files
dotnet csharpier format .             # fix them
```

The pre-commit hook (`scripts/install-hooks.sh`) formats staged `.cs` files automatically; CI enforces `dotnet csharpier check .` in the `Format` job.

## AOT Testing Guide

PicoInfra is AOT-first. Before submitting, verify your changes compile with Native AOT:

```shell
# Publish a test project with AOT enabled
dotnet publish PicoDI/tests/PicoDI.Test/PicoDI.Test.csproj -c Release -p:PublishAot=true

# Or for a specific project
dotnet publish <project>.csproj -c Release -p:PublishAot=true
```

If your change introduces new dependencies, make sure they are AOT-compatible (no runtime code generation, no unsupported reflection).

Key things to watch for:
- No calls to `Activator.CreateInstance` or `RuntimeHelpers.GetUninitializedObject`.
- No `Expression.Compile` or dynamic method generation.
- Generic types used with value types should not trigger unexpected reflection.
- Trimming warnings should be treated as errors.
