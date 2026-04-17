using System.Collections.Immutable;

namespace Hakaze.Build.Abstractions;

public static class PropertyExtensions
{
    extension(Property property)
    {
        public string StringValue => (property as StringProperty)!.Value;
        public long IntegerValue => (property as IntegerProperty)!.Value;
        public bool BooleanValue => (property as BooleanProperty)!.Value;
        public ImmutableArray<Property> ListValue => (property as ListProperty)!.Value;
    }
}
