namespace Hakaze.Build.Abstractions;

/// <summary>
/// the name of the target.For example, if we have a `Foo.cs`, then
/// we have a task to compile it,the target name should be `CompileCSharpSource`.
/// </summary>
public readonly record struct TargetName(string Name);
