using System.Runtime.CompilerServices;

// The audio internals (decoder, synthesizer, process supervision) are tested directly
// rather than through the public players, which would need real audio output.
[assembly: InternalsVisibleTo("YAPA.Avalonia.Tests")]
