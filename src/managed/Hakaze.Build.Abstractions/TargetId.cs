namespace Hakaze.Build.Abstractions;

/// <summary>
/// Global unique target identity.
/// </summary>
public readonly record struct TargetId(ProjectId ProjectId,
                                       ConfigId ConfigId,
                                       TargetName Name,
                                       TargetSource Source)
{

}
