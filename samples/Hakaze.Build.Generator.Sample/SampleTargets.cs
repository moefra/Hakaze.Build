using System.Collections.Immutable;
using Hakaze.Build.Abstractions;
using Hakaze.Build.Abstractions.Generator;

namespace Hakaze.Build.Generator.Sample;

public sealed record Foo(string Value);

[ExportTargets]
public partial class Targets
{
    [Target]
    [TargetSource(nameof(GetCompileSource))]
    public static Task<ExecutionResult> Compile(
        CancellationToken cancellationToken,
        IEvaluatedBuilding building)
    {
        return Task.FromResult<ExecutionResult>(
            new SuccessfulExecutionWithResult<Foo>("compiled", new Foo(building.Profile.Name)));
    }

    private static string GetCompileSource(IBuilding building, CancellationToken cancellationToken) => $"compile:{building.Profile.Name}:{cancellationToken.CanBeCanceled}";


    [Target]
    public static Task<ExecutionResult> Nop()
    {
        return Task.FromResult<ExecutionResult>(new SuccessfulExecution("nop"));
    }

    [TargetFactory("FactoryTarget")]
    public static Task<ImmutableArray<ITarget>> CreateFactoryTargetsAsync(IBuilding building, CancellationToken cancellationToken)
    {
        var targetId = new TargetId(
            null,
            null,
            FactoryTargetId,
            new TargetSource($"factory:{building.Profile.Name}:{cancellationToken.CanBeCanceled}"));

        return Task.FromResult<ImmutableArray<ITarget>>([new FactoryProvidedTarget(targetId, $"{building.Profile.Name}:{cancellationToken.CanBeCanceled}")]);
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

public sealed class FactoryProvidedTarget(TargetId id, string value) : ITarget
{
    public TargetId Id { get; } = id;

    public ImmutableArray<TargetId> RequiredPreparation { get; } = [];

    public Task<ExecutionResult> ExecuteAsync(
        IEvaluatedBuilding building,
        ImmutableDictionary<TargetId, object?> collectedExecutionResults,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ExecutionResult>(
            new SuccessfulExecutionWithResult<string>("factory-target", value));
    }
}

public static class SampleUsage
{
    public static Task<ImmutableArray<ITarget>> LoadTargetsGenericallyAsync<T>(IBuilding building, CancellationToken cancellationToken = default)
        where T : IExportTargets
    {
        return T.GetTargetsAsync(building, cancellationToken);
    }

    public static Task<ImmutableArray<ITarget>> LoadTargetsDirectlyAsync(IBuilding building, CancellationToken cancellationToken = default)
    {
        return Targets.GetTargetsAsync(building, cancellationToken);
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
