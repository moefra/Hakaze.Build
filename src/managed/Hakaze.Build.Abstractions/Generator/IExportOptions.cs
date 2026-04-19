using System.Collections.Immutable;

namespace Hakaze.Build.Abstractions.Generator;

public interface IExportOptions
{
}

public interface IExportOptions<TSelf> : IExportOptions
    where TSelf : IExportOptions<TSelf>
{
    static abstract TSelf BindAsync(IConfig config);

    static abstract ImmutableDictionary<string, string> GetOptionDocuments();

    static abstract ImmutableDictionary<string, string> GetValidatorDocuments();

    static abstract ImmutableDictionary<string, string> GetOptionType();
}
