namespace Hakaze.Build.Abstractions;

/// <summary>
/// Logical target identity.
/// <para><see cref="ProjectId"/> is <see langword="null"/> when the target is global or project-agnostic.</para>
/// <para><see cref="ConfigId"/> is <see langword="null"/> when the target is config-agnostic and its identity does not carry a configuration value.</para>
/// <para><see cref="Name"/> identifies the logical target name.</para>
/// <para><see cref="Source"/> is <see langword="null"/> when source is not part of the target identity.</para>
/// These nullable dimensions omit that part of the identity; they do not act as wildcard matching during lookup.
/// </summary>
public readonly record struct TargetId(ProjectId? ProjectId,
                                       ConfigId? ConfigId,
                                       TargetName Name,
                                       TargetSource? Source)
{

}
