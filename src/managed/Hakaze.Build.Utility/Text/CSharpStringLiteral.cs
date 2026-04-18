namespace Hakaze.Build.Utility.Text;

public static class CSharpStringLiteral
{
    public static string Escape(string value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
