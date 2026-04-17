using System.Collections.Immutable;
using Hakaze.Build.Abstractions;

namespace Hakaze.Build.Core;

public sealed class EvaluatedBuilding(
    Profile profile,
    IConfig config,
    ImmutableArray<IProject> projects,
    ImmutableDictionary<TargetId, ITarget> targets) : IEvaluatedBuilding
{
    public Profile Profile { get; } = profile;

    public IConfig Config { get; } = config;

    public ImmutableArray<IProject> Projects { get; } = projects;

    public ImmutableDictionary<TargetId, ITarget> Targets { get; } = targets;
}
