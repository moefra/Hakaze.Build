using System.Collections.Immutable;

namespace Hakaze.Build.Abstractions.Generator;

public interface IExportTargets
{
    static abstract Task<ImmutableArray<ITarget>> GetTargetsAsync(
        IBuilding building,
        CancellationToken cancellationToken = default);
}
