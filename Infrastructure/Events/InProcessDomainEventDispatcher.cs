using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Features.Application.Events;
using Features.Domain.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Features.Infrastructure.Events;

internal sealed class InProcessDomainEventDispatcher(
    IServiceProvider serviceProvider,
    IOptionsMonitor<EventInfrastructureOptions> options,
    ILogger<InProcessDomainEventDispatcher> logger) : IDomainEventDispatcher
{
    public async Task DispatchAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var dispatchTimer = Stopwatch.StartNew();
        await ObserveAsync(new DomainEventObservation(domainEvent, DomainEventObservationKind.DispatchStarted), cancellationToken);

        try
        {
            foreach (var filter in serviceProvider.GetServices<IDomainEventFilter>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await filter.ShouldDispatchAsync(domainEvent, cancellationToken).ConfigureAwait(false))
                {
                    dispatchTimer.Stop();
                    logger.LogDebug("Dispatch of {DomainEventType} was rejected by {FilterType}.", domainEvent.GetType().Name, filter.GetType().Name);
                    await ObserveAsync(
                        new DomainEventObservation(domainEvent, DomainEventObservationKind.DispatchFiltered, Elapsed: dispatchTimer.Elapsed),
                        cancellationToken);
                    return;
                }
            }

            var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(domainEvent.GetType());
            var handlers = serviceProvider.GetServices(handlerType);
            var handleMethod = handlerType.GetMethod(nameof(IDomainEventHandler<IDomainEvent>.HandleAsync))!;

            foreach (var handler in handlers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DispatchToHandlerAsync(domainEvent, handler!, handlerType, handleMethod, cancellationToken).ConfigureAwait(false);
            }

            dispatchTimer.Stop();
            logger.LogInformation("Dispatched {DomainEventType} in {ElapsedMilliseconds} ms.", domainEvent.GetType().Name, dispatchTimer.ElapsedMilliseconds);
            await ObserveAsync(
                new DomainEventObservation(domainEvent, DomainEventObservationKind.DispatchCompleted, Elapsed: dispatchTimer.Elapsed),
                cancellationToken);
        }
        catch (Exception exception)
        {
            dispatchTimer.Stop();
            logger.LogError(exception, "Dispatch of {DomainEventType} failed.", domainEvent.GetType().Name);
            await ObserveAsync(
                new DomainEventObservation(domainEvent, DomainEventObservationKind.DispatchFailed, Elapsed: dispatchTimer.Elapsed, Exception: exception),
                CancellationToken.None);
            throw;
        }
    }

    private async Task DispatchToHandlerAsync(
        IDomainEvent domainEvent,
        object handler,
        Type handlerType,
        MethodInfo handleMethod,
        CancellationToken cancellationToken)
    {
        var maxAttempts = options.CurrentValue.MaxRetryAttempts + 1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handlerTimer = Stopwatch.StartNew();
            await ObserveAsync(
                new DomainEventObservation(domainEvent, DomainEventObservationKind.HandlerAttempt, handler.GetType(), attempt),
                cancellationToken);

            try
            {
                var task = InvokeHandler(handleMethod, handler, domainEvent, cancellationToken);
                await task.ConfigureAwait(false);
                handlerTimer.Stop();
                await ObserveAsync(
                    new DomainEventObservation(domainEvent, DomainEventObservationKind.HandlerSucceeded, handler.GetType(), attempt, handlerTimer.Elapsed),
                    cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                handlerTimer.Stop();
                logger.LogWarning(
                    exception,
                    "Handler {HandlerType} failed for {DomainEventType} on attempt {Attempt} of {MaxAttempts}.",
                    handler.GetType().Name,
                    domainEvent.GetType().Name,
                    attempt,
                    maxAttempts);
                await ObserveAsync(
                    new DomainEventObservation(domainEvent, DomainEventObservationKind.HandlerFailed, handler.GetType(), attempt, handlerTimer.Elapsed, exception),
                    CancellationToken.None);

                if (attempt == maxAttempts)
                {
                    throw;
                }

                var retryDelay = options.CurrentValue.RetryDelay;
                if (retryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static Task InvokeHandler(
        MethodInfo handleMethod,
        object handler,
        IDomainEvent domainEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            return (Task)handleMethod.Invoke(handler, [domainEvent, cancellationToken])!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private async Task ObserveAsync(DomainEventObservation observation, CancellationToken cancellationToken)
    {
        foreach (var observer in serviceProvider.GetServices<IDomainEventObserver>())
        {
            try
            {
                await observer.ObserveAsync(observation, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Domain event observer {ObserverType} failed.", observer.GetType().Name);
            }
        }
    }
}
