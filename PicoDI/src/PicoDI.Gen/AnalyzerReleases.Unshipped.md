; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
;
; One row per DiagnosticDescriptor declared in DiagnosticDescriptors.cs, with the Category and Severity
; read from the descriptor (Category is PicoDiNames.RootNamespace == "PicoDI"). PicoDI.Gen is not
; packable — it ships as analyzers/dotnet/cs inside the PicoDI.Abs package, so these rows are
; promoted to AnalyzerReleases.Shipped.md on the PicoDI.Abs tag.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PICO002 | PicoDI | Error | Circular dependency detected
PICO003 | PicoDI | Error | Abstract type registration
PICO004 | PicoDI | Error | Missing public constructor
PICO005 | PicoDI | Error | Multiple preferred constructors
PICO006 | PicoDI | Error | Factory-based registration cannot be intercepted
