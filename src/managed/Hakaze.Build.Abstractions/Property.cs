using System.Collections.Immutable;

namespace Hakaze.Build.Abstractions;

public abstract record Property();

public sealed record StringProperty(string Value) : Property;

public sealed record IntegerProperty(long Value): Property;

public sealed record BooleanProperty(bool Value): Property;

public sealed record ListProperty(ImmutableArray<Property> Value): Property;
