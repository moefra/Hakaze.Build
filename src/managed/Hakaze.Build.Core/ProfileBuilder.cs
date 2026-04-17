using Hakaze.Build.Abstractions;

namespace Hakaze.Build.Core;

public sealed class ProfileBuilder
{
    private string? _name;
    private bool _optimizeCode;
    private bool _generateDebugInfo;

    public ProfileBuilder WithName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
        return this;
    }

    public ProfileBuilder SetOptimizeCode(bool value = true)
    {
        _optimizeCode = value;
        return this;
    }

    public ProfileBuilder SetGenerateDebugInfo(bool value = true)
    {
        _generateDebugInfo = value;
        return this;
    }

    public Profile Build()
    {
        return new Profile(RequireName(), _optimizeCode, _generateDebugInfo);
    }

    private string RequireName()
    {
        if (_name is not { Length: > 0 } name)
        {
            throw new InvalidOperationException("Profile name is required.");
        }

        return name;
    }
}
