; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
;
; One row per DiagnosticDescriptor declared in PicoCfgBindGenerator.Diagnostics.cs, with the Category
; and Severity read from the descriptor. PicoCfg.Gen is not packable — it ships as analyzers/dotnet/cs
; inside the PicoCfg.Abs package, so these rows are promoted to AnalyzerReleases.Shipped.md on the
; PicoCfg.Abs tag.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PCFGGEN001 | PicoCfg.Gen | Error | Binding target must be a closed named type
PCFGGEN002 | PicoCfg.Gen | Error | Binding target must have a usable constructor
PCFGGEN003 | PicoCfg.Gen | Error | Complex properties are not supported
PCFGGEN004 | PicoCfg.Gen | Error | Collection properties are not supported
PCFGGEN005 | PicoCfg.Gen | Error | Property type is not supported
PCFGGEN006 | PicoCfg.Gen | Error | Property shape is not supported
PCFGGEN007 | PicoCfg.Gen | Error | Binding target must be a concrete class
PCFGGEN008 | PicoCfg.Gen | Error | Circular dependency detected in nested binding types
PCFGGEN009 | PicoCfg.Gen | Warning | Nesting depth limit reached
PCFGGEN010 | PicoCfg.Gen | Error | Collection element type is not supported
