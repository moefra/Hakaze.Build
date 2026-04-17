using System.Collections.Immutable;
using Hakaze.Build.Abstractions.Exceptions;

namespace Hakaze.Build.Abstractions;

public static class TargetExtensions
{
    extension<T>(ITarget target)
    {
        public async Task<T> ExpectAsync(
            IEvaluatedBuilding building,
            ImmutableDictionary<TargetId, object?> collectedExecutionResults,
            CancellationToken cancellationToken = default)
        {
            return (await target.ExecuteAsync(building, collectedExecutionResults, cancellationToken)) switch
            {
                SuccessfulExecutionWithResult<T> result => result.ExecutionResult,
                CachedExecution<T> cached => cached.CachedResult,
                _ => throw new NotImplementedException()
            };
        }
    }

    extension(Task<ExecutionResult> result)
    {
        public Task<ExecutionResult> Checked()
        {
            result.ContinueWith(static (task) =>
                                    task is { IsCompletedSuccessfully: false, IsCanceled: false } ?
                                    throw new TaskFailedException("Task<ExecutionResult> failed", task.Exception) :
                                    task);
            return result;
        }
    }
}
