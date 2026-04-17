namespace Hakaze.Build.Abstractions.Exceptions;

public sealed class TaskFailedException : Exception
{
    public TaskFailedException()
    {

    }

    public TaskFailedException(string? msg) : base(msg)
    {

    }

    public TaskFailedException(string? msg, Exception? inner) : base(msg, inner)
    {

    }
}
