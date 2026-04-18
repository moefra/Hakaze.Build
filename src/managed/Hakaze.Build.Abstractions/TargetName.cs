namespace Hakaze.Build.Abstractions;

/// <summary>
/// Logical target name. Generated targets commonly use a fully qualified
/// name such as <c>Namespace.Type.Method</c>.
/// </summary>
public readonly record struct TargetName(string Name);
