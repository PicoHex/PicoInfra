; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
;
; Rows promoted out of AnalyzerReleases.Unshipped.md at the release that first shipped them:
; PicoAop.Gen carries 7 rule IDs, first shipped at two different tags: PICO101/102/105/110 in
; 2026.1.2 (the first tag whose PicoAop.Abs packs PicoAop.Gen.dll under analyzers/dotnet/cs) and
; PICO111/112/113 in 2026.11.0. Method: one git grep per ID per tag, oldest tag first, plus a
; check that the tag's PicoAop.Abs actually packs the generator DLL.
;
; The heading is the release TAG, never a project version: one tag is one <year>.<x>.<y> for every
; package (docs/releasing.md), so the .Abs package and the .Gen project cannot carry different
; numbers. Which PACKAGE carried the rule has changed, though - standalone *.Gen packages up to
; 2026.1.1, the generator embedded in *.Abs after that - which is why the tags below differ per rule.
;
; Recorded LATE: no AnalyzerReleases.* file existed at any of these tags. The tracking landed in
; b5caf5b (2026-11-12) and the missing-file guard in 0a52b39, so this file is archaeology, not a
; promotion performed at the time. The alternative - promoting everything under the NEXT tag - was
; rejected: it would claim a first-shipped version that is wrong for all 23 rules - 20 of them first
; shipped at v2026.1.1 or v2026.1.2, which is 42-43 tags earlier (44 tags exist). Rule:
; docs/releasing.md, "Analyzer release tracking".

## Release 2026.1.2

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PICO101 | PicoAop | Error | Sealed type cannot be intercepted
PICO102 | PicoAop | Error | Value type cannot be intercepted
PICO105 | PicoAop | Error | Type does not implement IInterceptor
PICO110 | PicoAop | Warning | Method with ref/out parameters delegated without interception

## Release 2026.11.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PICO111 | PicoAop | Warning | Member cannot be intercepted
PICO112 | PicoAop | Error | Property with an init accessor cannot be intercepted
PICO113 | PicoAop | Warning | Members share one generated invocation name
