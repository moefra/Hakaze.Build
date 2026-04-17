using System.Collections.Immutable;

namespace Hakaze.Build.Abstractions;

public interface ITargetFactory
{
    /// <summary>
    /// Extracts targets for the building. For reduplicative invoking
    /// the method should return the array with the same items or same array
    /// if the <paramref name="building"/> is the same.
    /// </summary>
    /// <returns></returns>
    Task<ImmutableArray<ITarget>> GetTargetsAsync(IBuilding building, CancellationToken cancellationToken = default);
}
