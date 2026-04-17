using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Hakaze.Build.Abstractions;

/// <summary>
/// Global unique configuration identity.
/// </summary>
public readonly record struct ConfigId(UInt128 Id1,UInt128 Id2)
{
    public static ConfigId FromProperties(IEnumerable<KeyValuePair<string, Property>> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        var canonical = new StringBuilder();
        foreach (var (key, value) in properties.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(value);

            AppendString(canonical, key);
            AppendProperty(canonical, value);
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())).AsSpan();
        var id1 = BinaryPrimitives.ReadUInt128BigEndian(bytes[0..16]);
        var id2 = BinaryPrimitives.ReadUInt128BigEndian(bytes[16..32]);
        return new ConfigId(id1,id2);
    }

    public static ConfigId FromProperties(ImmutableDictionary<string, Property> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return FromProperties(properties.AsEnumerable());
    }

    private static void AppendProperty(StringBuilder builder, Property property)
    {
        switch (property)
        {
            case StringProperty stringProperty:
                builder.Append('S');
                AppendString(builder, stringProperty.Value);
                break;
            case IntegerProperty integerProperty:
                builder.Append('I');
                builder.Append(integerProperty.Value);
                builder.Append(';');
                break;
            case BooleanProperty booleanProperty:
                builder.Append('B');
                builder.Append(booleanProperty.Value ? '1' : '0');
                break;
            case ListProperty listProperty:
                builder.Append('L');
                builder.Append(listProperty.Value.Length);
                builder.Append(':');
                foreach (var item in listProperty.Value)
                {
                    AppendProperty(builder, item);
                }
                builder.Append(';');
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(property), property, "Unsupported property type.");
        }
    }

    private static void AppendString(StringBuilder builder, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        builder.Append(value.Length);
        builder.Append(':');
        builder.Append(value);
        builder.Append(';');
    }
}
