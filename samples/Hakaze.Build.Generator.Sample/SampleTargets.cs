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
        IBuilding building,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<ExecutionResult>(
            new SuccessfulExecutionWithResult<Foo>("compiled", new Foo(building.Profile.Name)));
    }

    [Target]
    [DependOn(nameof(Compile))]
    public static Task<ExecutionResult> PostCompile(
        IBuilding building,
        [Retrieval(nameof(Compile))] Foo foo,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<ExecutionResult>(
            new SuccessfulExecutionWithResult<string>("post-compiled", foo.Value));
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
}
