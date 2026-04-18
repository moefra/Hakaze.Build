using Hakaze.Build.Utility.Text;

namespace Hakaze.Build.Utility.Tests;

public class CSharpStringLiteralTests
{
    [Test]
    [Arguments("plain-text", "plain-text")]
    [Arguments("a\\b", "a\\\\b")]
    [Arguments("\"quoted\"", "\\\"quoted\\\"")]
    [Arguments("c:\\path\\\"file\"", "c:\\\\path\\\\\\\"file\\\"")]
    public void Escape_ProducesExpectedCSharpLiteralContent(string input, string expected)
    {
        var actual = CSharpStringLiteral.Escape(input);
        if (actual != expected)
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    [Test]
    public void Escape_ThrowsForNull()
    {
        try
        {
            _ = CSharpStringLiteral.Escape(null!);
        }
        catch (ArgumentNullException)
        {
            return;
        }

        throw new InvalidOperationException("Expected ArgumentNullException for null input.");
    }
}
