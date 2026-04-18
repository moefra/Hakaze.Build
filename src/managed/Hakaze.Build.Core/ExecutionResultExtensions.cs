using Hakaze.Build.Abstractions;

namespace Hakaze.Build.Core;

public static class ExecutionResultExtensions
{
    extension(ExecutionResult _)
    {
        public static ExecutionResult Success(string report)
        {
            var re = new SuccessfulExecution(report);
            return re;
        }

        public static ExecutionResult SuccessWith<T>(string report,T result)
        {
            var re = new SuccessfulExecutionWithResult<T>(report, result);
            return re;
        }

        public static ExecutionResult Skipped(string reason)
        {
            var re = new SkippedExecution(reason);
            return re;
        }

        public static ExecutionResult Cached<T>(string reason, T cached)
        {
            var re = new CachedExecution<T>(reason, cached);
            return re;
        }

        public static ExecutionResult Failed(string reason, Exception? exception = null)
        {
            var re = new FailedExecution(reason, exception);
            return re;
        }
    }
}