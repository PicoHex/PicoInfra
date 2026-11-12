; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
;
; One row per DiagnosticDescriptor declared in MediatorGenerator.cs, with the Category and Severity
; read from the descriptor. PicoMediator.Gen is not packable — it ships as analyzers/dotnet/cs inside
; the PicoMediator.Abs package, so these rows are promoted to AnalyzerReleases.Shipped.md on the
; PicoMediator.Abs tag.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PMGEN001 | PicoMediator.Gen | Warning | Base-typed publish cannot reach concrete subscribers
