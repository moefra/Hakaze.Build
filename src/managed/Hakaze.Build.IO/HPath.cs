namespace Hakaze.Build.IO;

public sealed class HPath : IEquatable<HPath>
{
    public HPath(string path)
        : this(path, normalizeToFullPath: true)
    {
    }

    public HPath(FileSystemInfo info)
        : this(info?.FullName ?? throw new ArgumentNullException(nameof(info)))
    {
    }

    private HPath(string path, bool normalizeToFullPath)
    {
        ArgumentNullException.ThrowIfNull(path);
        Value = normalizeToFullPath
            ? NormalizeFullPath(path)
            : NormalizePath(path);
    }

    public string Value { get; }

    public HPath Parent
    {
        get
        {
            var root = Path.GetPathRoot(Value);
            if (!string.IsNullOrEmpty(root) && string.Equals(Value, root, StringComparison.Ordinal))
            {
                return this;
            }

            var parent = Path.GetDirectoryName(Value);
            if (string.IsNullOrEmpty(parent))
            {
                return this;
            }

            return new HPath(parent, normalizeToFullPath: Path.IsPathRooted(parent));
        }
    }

    public string FileName => Path.GetFileName(Value);

    public string FileNameWithoutExtension => Path.GetFileNameWithoutExtension(Value);

    public string Extension => Path.GetExtension(Value);

    public HPath Root
    {
        get
        {
            var root = Path.GetPathRoot(Value);
            return string.IsNullOrEmpty(root)
                ? this
                : new HPath(root, normalizeToFullPath: true);
        }
    }

    public bool IsPathRooted => Path.IsPathRooted(Value);

    public bool HasExtension => Path.HasExtension(Value);

    public bool Exists => File.Exists(Value) || Directory.Exists(Value);

    public bool IsFile => File.Exists(Value);

    public bool IsDirectory => Directory.Exists(Value);

    public static implicit operator HPath(string path) => new(path);

    public static implicit operator string(HPath path) => path.Value;

    public static HPath operator /(HPath left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.Combine(right);
    }

    public static HPath operator /(HPath left, HPath right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.Combine(right);
    }

    public static bool operator ==(HPath? left, HPath? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return left.Equals(right);
    }

    public static bool operator !=(HPath? left, HPath? right) => !(left == right);

    public HPath ChangeExtension(string? extension) => new(Path.ChangeExtension(Value, extension) ?? Value);

    public HPath GetRelativePathTo(HPath destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        // Preserve the relative result instead of resolving it against the current working directory.
        return new HPath(Path.GetRelativePath(Value, destination.Value), normalizeToFullPath: false);
    }

    public HPath Combine(string right)
    {
        ArgumentNullException.ThrowIfNull(right);
        return new HPath(Path.Combine(Value, right));
    }

    public HPath Combine(HPath right)
    {
        ArgumentNullException.ThrowIfNull(right);
        return Combine(right.Value);
    }

    public bool Equals(HPath? other)
        => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is HPath other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;

    private static string NormalizeFullPath(string path) => NormalizePath(Path.GetFullPath(path));

    private static string NormalizePath(string path)
    {
        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(root) && string.Equals(path, root, StringComparison.Ordinal))
        {
            return root;
        }

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
