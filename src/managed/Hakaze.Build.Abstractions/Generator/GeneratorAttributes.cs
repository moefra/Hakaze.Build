namespace Hakaze.Build.Abstractions.Generator;

[global::System.AttributeUsage(global::System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ExportTargetsAttribute : global::System.Attribute
{
}

[global::System.AttributeUsage(global::System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PerProjectAttribute : global::System.Attribute
{
}

[global::System.AttributeUsage(global::System.AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class TargetAttribute : global::System.Attribute
{
}

[global::System.AttributeUsage(global::System.AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class TargetFactoryAttribute : global::System.Attribute
{
    public TargetFactoryAttribute()
    {
    }

    public TargetFactoryAttribute(string targetName)
    {
        TargetName = targetName;
    }

    public string? TargetName { get; }
}

[global::System.AttributeUsage(global::System.AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class TargetSourceAttribute : global::System.Attribute
{
    public TargetSourceAttribute(string methodName)
    {
        MethodName = methodName;
    }

    public string MethodName { get; }
}

[global::System.AttributeUsage(global::System.AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
public sealed class DependOnAttribute : global::System.Attribute
{
    public DependOnAttribute(params string[] targetNames)
    {
        TargetNames = targetNames ?? global::System.Array.Empty<string>();
    }

    public string[] TargetNames { get; }
}

[global::System.AttributeUsage(global::System.AttributeTargets.Parameter, Inherited = false, AllowMultiple = true)]
public sealed class RetrievalAttribute : global::System.Attribute
{
    public RetrievalAttribute(string targetName)
    {
        TargetName = targetName;
    }

    public string TargetName { get; }
}
