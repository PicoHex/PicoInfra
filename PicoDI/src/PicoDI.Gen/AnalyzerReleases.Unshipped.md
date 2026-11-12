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
