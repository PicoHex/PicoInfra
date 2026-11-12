; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
;
; Rows promoted out of AnalyzerReleases.Unshipped.md at the release that first shipped them:
; PicoMediator.Gen carries 1 rule ID, first shipped in 2026.8.1: the oldest tag whose PicoMediator.Abs
; packs PicoMediator.Gen.dll under analyzers/dotnet/cs. Method: one git grep per ID per tag, oldest
; tag first, plus a check that the tag's PicoMediator.Abs packs the generator DLL.
;
; The heading is the release TAG, never a project version: one tag is one <year>.<x>.<y> for every
; package (docs/releasing.md), so the .Abs package and the .Gen project cannot carry different
; numbers. Which PACKAGE carried the rule has changed, though - standalone *.Gen packages up to
; 2026.1.1, the generator embedded in *.Abs after that - which is why the tags below differ per rule.
;
; Recorded LATE: no AnalyzerReleases.* file existed at any of these tags. The tracking landed in
; b5caf5b (2026-11-12) and the missing-file guard in 0a52b39, so this file is archaeology, not a
; promotion performed at the time. The alternative - promoting everything under the NEXT tag - was
; rejected: it would claim a first-shipped version that is wrong for all 23 rules - 20 of them had
; already shipped before v2026.11.0, 18 of those at v2026.1.1 or v2026.1.2 (42-43 tags earlier; 44
; tags exist) and 2 at v2026.8.1 / v2026.8.6 (16 and 11 tags earlier). Rule:
; docs/releasing.md, "Analyzer release tracking".

## Release 2026.8.1

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PMGEN001 | PicoMediator.Gen | Warning | Base-typed publish cannot reach concrete subscribers
