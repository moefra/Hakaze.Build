namespace Hakaze.Build.Abstractions;

/// <summary>
/// the source of the target.For example, if we have a `Foo.cs`, then
/// we have a task to compile it,the target source should be `/path/to/Foo.cs`.
/// </summary>
public readonly record struct TargetSource(string SourceId);
