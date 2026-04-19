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
    /// Path to the CMake executable.
    /// </summary>
    [Option]
    public required string CMakePath { get; init; }

    /// <summary>
    /// Explicit version selector for CMake.
    /// </summary>
    [Option("cmake-ver")]
    public int CMakeVersion { get; init; }

    /// <summary>
    /// Source files that should be processed.
    /// </summary>
    [Option]
    public ImmutableArray<string> SourceFile { get; init; }

    /// <summary>
    /// Raw CMake configuration properties.
    /// </summary>
    [Option]
    public required ImmutableArray<Property> CMakeConfig { get; init; }

    /// <summary>
    /// Validates the bound option values.
    /// </summary>
    [OptionValidator]
    public void Validate()
    {
        throw new NotImplementedException();
    }
}

public static class OptionSampleUsage
{
    public static T LoadOptions<T>(IConfig config)
        where T : IExportOptions<T>
    {
        return T.BindAsync(config);
    }

    public static ImmutableDictionary<string, string> LoadOptionDocuments<T>()
        where T : IExportOptions<T>
    {
        return T.GetOptionDocuments();
    }
}
