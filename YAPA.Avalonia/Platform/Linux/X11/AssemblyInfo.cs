using System.Runtime.CompilerServices;

// The live X11 tests create their own throwaway windows and read properties
// back through the same bindings the app uses, instead of duplicating them.
[assembly: InternalsVisibleTo("YAPA.Avalonia.Tests")]
