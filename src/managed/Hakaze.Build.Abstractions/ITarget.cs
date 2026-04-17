using System.Collections.Immutable;

namespace Hakaze.Build.Abstractions;

public interface ITarget
{
    TargetId Id { get; }

    ImmutableArray<TargetId> RequiredPreparation { get; }

    Task<ExecutionResult> ExecuteAsync(CancellationToken cancellationToken = default);
}
