namespace Hakaze.Build.Abstractions;

public sealed record Profile(string Name, bool OptimizeCode = false, bool GenerateDebugInfo = false);
