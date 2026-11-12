; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
;
; One row per DiagnosticDescriptor declared in PicoAopDiag.cs, with the Category and Severity read
; from the descriptor. PicoAop.Gen is not packable — it ships as analyzers/dotnet/cs inside the
; PicoAop.Abs package, so these rows are promoted to AnalyzerReleases.Shipped.md on the PicoAop.Abs tag.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
