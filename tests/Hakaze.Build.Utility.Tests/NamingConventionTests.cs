using Hakaze.Build.Utility.Naming;

namespace Hakaze.Build.Utility.Tests;

public class NamingConventionTests
{
    [Test]
    [Arguments("CMakePath", "cmake-path")]
    [Arguments("SourceFile", "source-file")]
    [Arguments("HTTPRequest", "httprequest")]
    [Arguments("Version2Path", "version2-path")]
    [Arguments("", "")]
    public void ToKebabCase_ConvertsExpectedValues(string input, string expected)
    {
        var actual = NamingConvention.ToKebabCase(input);
        if (actual != expected)
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }
}
