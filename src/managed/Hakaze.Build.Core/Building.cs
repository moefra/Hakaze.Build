using System.Collections.Immutable;
using Hakaze.Build.Abstractions;

namespace Hakaze.Build.Core;

public sealed class Building(Profile profile, IConfig config, ImmutableArray<IProject> projects) : IBuilding
{
    public Profile Profile { get; } = profile;

    public IConfig Config { get; } = config;

    public ImmutableArray<IProject> Projects { get; } = projects;
}
