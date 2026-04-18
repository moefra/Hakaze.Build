namespace Hakaze.Build.Utility.Naming;

public static class NamingConvention
{
    public static string ToKebabCase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var builder = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsUpper(current))
            {
                var hasPrevious = index > 0;
                var previous = hasPrevious ? value[index - 1] : '\0';
                if (hasPrevious &&
                    (char.IsLower(previous) || char.IsDigit(previous)))
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(current));
                continue;
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }
}
