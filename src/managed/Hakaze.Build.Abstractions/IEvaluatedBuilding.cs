using System.Collections.Immutable;

namespace Hakaze.Build.Abstractions;

public interface IEvaluatedBuilding : IBuilding
{
    ImmutableDictionary<TargetId, ITarget> Targets { get; }
}