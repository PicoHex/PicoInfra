; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
;
; One row per DiagnosticDescriptor declared in PicoAopDiag.cs, with the Category and Severity read
; from the descriptor. PicoAop.Gen is not packable — it ships as analyzers/dotnet/cs inside the
; PicoAop.Abs package, so these rows are promoted to AnalyzerReleases.Shipped.md on the PicoAop.Abs tag.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PICO101 | PicoAop | Error | Sealed type cannot be intercepted
PICO102 | PicoAop | Error | Value type cannot be intercepted
PICO105 | PicoAop | Error | Type does not implement IInterceptor
PICO110 | PicoAop | Warning | Method with ref/out parameters delegated without interception
PICO111 | PicoAop | Warning | Member cannot be intercepted
PICO112 | PicoAop | Error | Property with an init accessor cannot be intercepted
PICO113 | PicoAop | Warning | Members share one generated invocation name
