using System.Collections.Immutable;
using Hakaze.Build.Abstractions;

namespace Hakaze.Build.Core;

public sealed class Target(
    TargetId id,
    ImmutableArray<TargetId> requiredPreparation,
    Func<IEvaluatedBuilding, ImmutableDictionary<TargetId, object?>, CancellationToken, Task<ExecutionResult>> execute) : ITarget
{
    private readonly Func<IEvaluatedBuilding, ImmutableDictionary<TargetId, object?>, CancellationToken, Task<ExecutionResult>> _execute =
        execute ?? throw new ArgumentNullException(nameof(execute));

    public TargetId Id { get; } = id;

    public ImmutableArray<TargetId> RequiredPreparation { get; } = requiredPreparation.IsDefault ? [] : requiredPreparation;

    public Task<ExecutionResult> ExecuteAsync(
        IEvaluatedBuilding building,
        ImmutableDictionary<TargetId, object?> collectedExecutionResults,
        CancellationToken cancellationToken = default)
    {
        return _execute(building, collectedExecutionResults, cancellationToken);
    }
}
