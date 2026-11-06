using DeskBox.Contracts;

namespace DeskBox.Features.EyeRest;

/// <summary>
/// Owns the active eye-rest timer session, modeled on
/// <see cref="Todo.TodoReminderRuntime"/> with the single-session
/// simplification (no retiring-session list): the session is created lazily on
/// first reconcile and <see cref="Reconcile(EyeRestRuntimeSettings, bool)"/>
/// refreshes it in place. The session is held as
/// <see cref="IEyeRestTimerSession"/> (same contract shape as the Todo
/// reminder) so this layer never references the service implementation.
/// Lifecycle calls are made on the owning UI thread; the thread is the
/// serialization boundary of the <see cref="IFeatureRuntime"/> state machine.
/// </summary>
internal sealed class EyeRestRuntime(
    Func<IEyeRestTimerSession> createService,
    Func<EyeRestRuntimeSettings> readSettings) : IFeatureRuntime
{
    private bool _stopped;

    internal IEyeRestTimerSession? Current { get; private set; }

    /// <summary>
    /// Reconciles to the given settings: disabled retires the session (or
    /// leaves it retired), enabled creates one if none exists and refreshes it
    /// otherwise. Rollback: a candidate that fails to start is disposed before
    /// the exception propagates — no half lease survives.
    /// </summary>
    internal void Reconcile(EyeRestRuntimeSettings settings, bool forceActive = false)
    {
        if (_stopped)
        {
            return;
        }

        if (!settings.ShouldRun && !forceActive)
        {
            if (Current is { } retiringSession)
            {
                // Route the disable through the session's own refresh before
                // retiring it: Refresh ends a running break (its BreakEnded
                // event closes the overlay) and stops the timer, while a
                // direct retire would strand a covering overlay with a dead
                // session whose skip path no longer exists.
                retiringSession.Refresh(settings);
            }

            Retire();
            return;
        }

        if (Current is { } current)
        {
            current.Refresh(settings);
            return;
        }

        IEyeRestTimerSession candidate = createService();
        try
        {
            candidate.Start();
            Current = candidate;
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    /// <summary>
    /// <see cref="IFeatureRuntime"/> lease entry: reconciles to the settings
    /// that are current right now — the same path the settings coordinator
    /// drives. Idempotency comes from <see cref="Reconcile"/> (an existing
    /// session is only refreshed).
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reconcile(readSettings());
        return Task.CompletedTask;
    }

    /// <summary>
    /// <see cref="IFeatureRuntime"/> release: stops accepting reconciliation
    /// and retires the session. Terminal — a start after this is a no-op.
    /// Repeat calls are no-ops, not double releases.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _stopped = true;
        Retire();
        return ValueTask.CompletedTask;
    }

    private void Retire()
    {
        Current?.Dispose();
        Current = null;
    }
}
