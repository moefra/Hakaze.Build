using System.Collections.Immutable;
using Hakaze.Build.Abstractions;

namespace Hakaze.Build.Generator.Sample;

public sealed record Foo(string Value);

[ExportTargets]
public partial class Targets
{
    private static partial string GetTargetSourceId(IBuilding building, CancellationToken cancellationToken) => "sample-target-source";

    [Target]
    public static Task<ExecutionResult> Compile(
        CancellationToken cancellationToken,
        IEvaluatedBuilding building)
    {
        return Task.FromResult<ExecutionResult>(
            new SuccessfulExecutionWithResult<Foo>("compiled", new Foo(building.Profile.Name)));
    }


    [Target]
    public static Task<ExecutionResult> Nop()
    {
        return Task.FromResult<ExecutionResult>(new SuccessfulExecution("nop"));
    }

    [Target]
    [DependOn(nameof(Compile))]
    public static Task<ExecutionResult> PostCompile(
        [Retrieval(nameof(Compile))] Foo foo,
        ImmutableDictionary<TargetId, object?> collectedExecutionResults,
        IEvaluatedBuilding building,
        CancellationToken cancellationToken)
    {
        Foo? collectedFoo = null;
        foreach (var value in collectedExecutionResults.Values)
        {
            if (value is Foo typedValue)
            {
                collectedFoo = typedValue;
                break;
            }
        }

        return Task.FromResult<ExecutionResult>(
            new SuccessfulExecutionWithResult<string>(
                "post-compiled",
                $"{building.Profile.Name}:{foo.Value}:{collectedFoo?.Value}"));
    }

    public static Task<ExecutionResult> CustomPostCompile(
        Foo foo,
        ImmutableDictionary<TargetId, object?> collectedExecutionResults,
        IEvaluatedBuilding building,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<ExecutionResult>(
            new SuccessfulExecutionWithResult<string>(
                "post-compiled-override",
                $"override:{building.Profile.Name}:{foo.Value}:{collectedExecutionResults.Count}"));
    }
}

public static class SampleUsage
{
    public static ImmutableArray<ITarget> LoadTargetsDirectly(IBuilding building)
    {
        return Targets.GetTargets(building);
    }

    public static Task<ImmutableArray<ITarget>> LoadTargetsViaFactoryAsync(IBuilding building, CancellationToken cancellationToken = default)
    {
        return new TargetsFactory().GetTargetsAsync(building, cancellationToken);
    }

    public static Task<ExecutionResult> InvokeCompileDirectly(IEvaluatedBuilding building, CancellationToken cancellationToken = default)
    {
        return Targets.InvokeCompile(building, cancellationToken);
    }

    public static Task<ExecutionResult> InvokePostCompileWithOverride(IEvaluatedBuilding building, CancellationToken cancellationToken = default)
    {
        return Targets.InvokePostCompile(building, cancellationToken, Targets.CustomPostCompile);
    }
}
