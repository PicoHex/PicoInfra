// Binary compatibility: SourceGeneratorRequiredException moved from the PicoDI
// runtime assembly to the PicoDI.Abs contract assembly. Pre-compiled consumers
// that referenced it through PicoDI.dll keep resolving it via this forwarder.
[assembly: TypeForwardedTo(typeof(PicoDI.Abs.SourceGeneratorRequiredException))]
