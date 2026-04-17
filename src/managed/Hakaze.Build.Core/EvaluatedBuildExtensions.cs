using System.Collections.Immutable;
using Hakaze.Build.Abstractions;

namespace Hakaze.Build.Core;

public static class EvaluatedBuildExtensions
{
    extension(IEvaluatedBuilding building)
    {
        public async Task<ExecutionResult> Execute(TargetId id)
        {
            ArgumentNullException.ThrowIfNull(building);

            var executionResults = new Dictionary<TargetId, ExecutionResult>();
            var publishedResults = new Dictionary<TargetId, ImmutableDictionary<TargetId, object?>>();
            return await ExecuteCore(id, []);

            async Task<ExecutionResult> ExecuteCore(TargetId currentId, HashSet<TargetId> stack)
            {
                if (executionResults.TryGetValue(currentId, out var cached))
                {
                    return cached;
                }

                if (!building.Targets.TryGetValue(currentId, out var target))
                {
                    throw new InvalidOperationException($"Target '{currentId}' was not found.");
                }

                if (!stack.Add(currentId))
                {
                    throw new InvalidOperationException($"Cyclic target dependency detected for '{currentId}'.");
                }

                try
                {
                    var collectedExecutionResults = ImmutableDictionary.CreateBuilder<TargetId, object?>();

                    foreach (var dependencyId in target.RequiredPreparation)
                    {
                        var dependencyResult = await ExecuteCore(dependencyId, stack);
                        if (dependencyResult is FailedExecution)
                        {
                            executionResults[currentId] = dependencyResult;
                            return dependencyResult;
                        }

                        if (publishedResults.TryGetValue(dependencyId, out var dependencyValues))
                        {
                            foreach (var (resultId, resultValue) in dependencyValues)
                            {
                                collectedExecutionResults[resultId] = resultValue;
                            }
                        }
                    }

                    var collectedResults = collectedExecutionResults.ToImmutable();
                    var result = await target.ExecuteAsync(
                        building,
                        collectedResults);
                    executionResults[currentId] = result;
                    publishedResults[currentId] = PublishResult(
                        currentId,
                        result,
                        collectedResults);
                    return result;
                }
                finally
                {
                    stack.Remove(currentId);
                }
            }

            static ImmutableDictionary<TargetId, object?> PublishResult(
                TargetId targetId,
                ExecutionResult result,
                ImmutableDictionary<TargetId, object?> dependencyResults)
            {
                var builder = dependencyResults.ToBuilder();

                if (TryExtractResultValue(result, out var value))
                {
                    builder[targetId] = value;
                }

                return builder.ToImmutable();
            }

            static bool TryExtractResultValue(ExecutionResult result, out object? value)
            {
                var resultType = result.GetType();

                if (resultType.IsGenericType &&
                    resultType.GetGenericTypeDefinition() == typeof(SuccessfulExecutionWithResult<>))
                {
                    value = resultType
                        .GetProperty(nameof(SuccessfulExecutionWithResult<object>.ExecutionResult))!
                        .GetValue(result);
                    return true;
                }

                if (resultType.IsGenericType &&
                    resultType.GetGenericTypeDefinition() == typeof(CachedExecution<>))
                {
                    value = resultType
                        .GetProperty(nameof(CachedExecution<object>.CachedResult))!
                        .GetValue(result);
                    return true;
                }

                if (result is SkippedExecution or SuccessfulExecution)
                {
                    value = null;
                    return true;
                }

                value = null;
                return false;
            }
        }
    }
}
