
using Hakaze.Build.Abstractions;

namespace Hakaze.Build.Core;

public static class TargetExtensions
{
    extension(TargetId id)
    {
        public TargetBuilder BeginBuild()
        {
            var builder = new TargetBuilder();
            builder.WithId(id);
            return builder;
        }
    }
}
