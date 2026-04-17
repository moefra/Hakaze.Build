using System.Collections.Immutable;

namespace Hakaze.Build.Abstractions;

public interface IBuilding
{
    Profile Profile { get; }

    IConfig Config { get; }

    ImmutableArray<IProject> Projects { get; }
}
