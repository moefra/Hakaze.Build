namespace Hakaze.Build.Abstractions;

public abstract record ExecutionResult();

public record SuccessfulExecution(string ExecutionReport) : ExecutionResult;

public record SuccessfulExecutionWithResult<T>(string ExecutionReport,T ExecutionResult)
    : SuccessfulExecution(ExecutionReport);

public record SkippedExecution(string Reason) : ExecutionResult;

public record CachedExecution<T>(string Reason, T CachedResult) : SkippedExecution(Reason);

public record FailedExecution(string Reason, Exception? Exception) : ExecutionResult;
