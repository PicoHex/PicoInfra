; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
;
; Rows promoted out of AnalyzerReleases.Unshipped.md at the release that first shipped them:
; PicoDI.Gen carries 5 rule IDs, all first shipped in 2026.1.1, when the generator shipped as the
; standalone PicoDI.Gen package (PicoDI.Gen was IsPackable=true and in the release pack list at that
; tag; PicoDI.Abs did not embed it yet). Method: one git grep per ID per tag, oldest tag first.
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

## Release 2026.1.1

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PICO002 | PicoDI | Error | Circular dependency detected
PICO003 | PicoDI | Error | Abstract type registration
PICO004 | PicoDI | Error | Missing public constructor
PICO005 | PicoDI | Error | Multiple preferred constructors
PICO006 | PicoDI | Error | Factory-based registration cannot be intercepted
