using DeskBox.Contracts;
using DeskBox.Features.EyeRest;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

/// <summary>
/// The eye-rest session: a 1-second <see cref="DispatcherQueueTimer"/> driving
/// an <see cref="EyeRestBreakPolicy"/>, modeled on the Todo reminder service's
/// thread model (created and driven on the owning UI thread). The host subscribes
/// to the events and owns the visuals:
/// <list type="bullet">
/// <item><see cref="EventRaised"/> with BreakDue → show the overlay on every
/// monitor; BreakImminent → show the long-break preview notification;
/// BreakEnded → close the overlays.</item>
/// <item><see cref="BreakCountdown"/> fires each tick while a break is running
/// with the current remainder, to drive the overlay countdown display.</item>
/// </list>
/// </summary>
public sealed class EyeRestTimerService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly DispatcherQueue? _dispatcherQueue;
    private EyeRestBreakPolicy _policy;
    private EyeRestBreakSchedule _appliedSchedule;
    private EyeRestBreakSchedule _pendingSchedule;
    private DispatcherQueueTimer? _timer;
    private bool _enabled;
    private bool _disposed;

    public EyeRestTimerService(EyeRestRuntimeSettings settings, DispatcherQueue? dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _enabled = settings.Enabled;
        _appliedSchedule = settings.Schedule;
        _pendingSchedule = settings.Schedule;
        _policy = new EyeRestBreakPolicy(settings.Schedule);
    }

    /// <summary>Break-cycle transitions. See the class remarks for the host's routing.</summary>
    internal event Action<EyeRestBreakEvent>? EventRaised;

    /// <summary>Per-tick break remainder (break phases only), for the overlay countdown.</summary>
    internal event Action<TimeSpan>? BreakCountdown;

    /// <summary>Starts the 1-second tick timer. Idempotent; a disabled session does not start.</summary>
    public void Start()
    {
        if (_disposed || !_enabled || _dispatcherQueue is null)
        {
            return;
        }

        if (_timer is not null)
        {
            return;
        }

        _timer = _dispatcherQueue.CreateTimer();
        _timer.Interval = TickInterval;
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    /// <summary>
    /// Settings-change reconciliation. While a break is running the current
    /// break is honored to its end (dropping a screen-covering break mid-flight
    /// because the user nudged a duration would be hostile); the new schedule
    /// is applied by the next tick after the break ends. Disabling mid-break
    /// ends the break immediately so the overlay closes. Re-enabling restarts
    /// the work phase from a full interval.
    /// </summary>
    public void Refresh(EyeRestRuntimeSettings settings)
    {
        if (_disposed)
        {
            return;
        }

        _pendingSchedule = settings.Schedule;

        if (!settings.Enabled)
        {
            if (_enabled)
            {
                _enabled = false;
                if (_policy.Phase != EyeRestPhase.Work)
                {
                    // The overlay is up; the BreakEnded event is what closes it.
                    SkipCurrentBreak();
                }
            }

            StopTimer();
            return;
        }

        bool reEnabling = !_enabled;
        _enabled = true;
        Start();
        if (reEnabling || _policy.Phase == EyeRestPhase.Work)
        {
            ApplyPendingSchedule(force: reEnabling);
        }
    }

    /// <summary>Skips the running break (overlay escape hatch / tray). No-op while working.</summary>
    public void SkipCurrentBreak()
    {
        if (_disposed || _policy.Phase == EyeRestPhase.Work)
        {
            return;
        }

        EyeRestBreakKind endingKind = _policy.Phase == EyeRestPhase.LongBreak
            ? EyeRestBreakKind.Long
            : EyeRestBreakKind.Mini;
        _policy.SkipBreak();
        // SkipBreak performs the transition; emit the ended event the tick
        // loop would have produced had the countdown run out.
        EventRaised?.Invoke(new EyeRestBreakEvent(EyeRestBreakEventKind.BreakEnded, endingKind, TimeSpan.Zero));
    }

    /// <summary>Starts the next break immediately (tray "break now"). No-op during a break or when disabled.</summary>
    public void ForceBreakNow()
    {
        if (_disposed || !_enabled || _policy.Phase != EyeRestPhase.Work)
        {
            return;
        }

        _policy.ForceBreak();
        EventRaised?.Invoke(new EyeRestBreakEvent(
            EyeRestBreakEventKind.BreakDue,
            _policy.Phase == EyeRestPhase.LongBreak ? EyeRestBreakKind.Long : EyeRestBreakKind.Mini,
            _policy.Remaining));
        BreakCountdown?.Invoke(_policy.Remaining);
    }

    /// <summary>Testable tick entry point; the DispatcherQueueTimer callback.</summary>
    internal void TickOnce()
    {
        if (_disposed || !_enabled)
        {
            return;
        }

        EyeRestBreakEvent @event = _policy.Tick(TickInterval);
        if (@event.EventKind != EyeRestBreakEventKind.None)
        {
            EventRaised?.Invoke(@event);
        }

        if (_policy.Phase != EyeRestPhase.Work)
        {
            BreakCountdown?.Invoke(_policy.Remaining);
        }
        else if (@event.EventKind == EyeRestBreakEventKind.BreakEnded)
        {
            // The break just ended and the work phase restarted on the old
            // schedule; swap in a pending mid-break settings change now so it
            // is not silently dropped until the next full cycle.
            ApplyPendingSchedule(force: false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopTimer();
    }

    private void ApplyPendingSchedule(bool force)
    {
        if (!force && _pendingSchedule == _appliedSchedule)
        {
            return;
        }

        _appliedSchedule = _pendingSchedule;
        _policy = new EyeRestBreakPolicy(_pendingSchedule);
    }

    private void StopTimer()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Tick -= Timer_Tick;
        _timer.Stop();
        _timer = null;
    }

    private void Timer_Tick(DispatcherQueueTimer sender, object args) => TickOnce();
}
