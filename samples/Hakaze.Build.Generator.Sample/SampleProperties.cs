using System.Collections.Immutable;
using Hakaze.Build.Abstractions;
using Hakaze.Build.Abstractions.Generator;

namespace Hakaze.Build.Generator.Sample;

/// <summary>
/// Generate options binder
/// </summary>
[ExportOptions]
public partial class SampleProperties
{
    /// <summary>
    /// map `cmake-path`
    /// </summary>
    [Option]
    public string CMakePath { get; init; }

    /// <summary>
    /// map to `cmake-ver`
    /// </summary>
    [Option("cmake-ver")]
    public int CMakeVersion { get; init; }

    /// <summary>
    /// map to `source-file`
    /// </summary>
    [Option]
    public ImmutableArray<string> SourceFile { get; init; }

    /// <summary>
    /// map to `cmake-config`
    /// </summary>
    [Option]
    public required ImmutableArray<Property> CMakeConfig { get; init; }

    [OptionValidator]
    public void Validate()
    {
        throw new NotImplementedException();
    }
}
