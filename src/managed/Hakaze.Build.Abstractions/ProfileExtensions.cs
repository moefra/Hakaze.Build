namespace Hakaze.Build.Abstractions;

public static class ProfileExtensions
{
    extension(Profile profile)
    {
        public bool IsReleaseBuild => profile is { OptimizeCode: true, GenerateDebugInfo: false };
    }
}
