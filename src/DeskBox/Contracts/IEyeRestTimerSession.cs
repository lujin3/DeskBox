namespace DeskBox.Contracts;

/// <summary>
/// Session lease the eye-rest runtime (<see cref="Features.EyeRest.EyeRestRuntime"/>)
/// consumes, modeled on <see cref="ITodoReminderSession"/>: the feature layer
/// drives the break session only through this contract so it stays decoupled
/// from the service implementation (Services owns timers; Features owns policy
/// composition). The events stay internal to the service — only the App host,
/// which creates the session, subscribes to them.
/// </summary>
internal interface IEyeRestTimerSession : IDisposable
{
    /// <summary>Starts the 1-second tick timer. Idempotent; a disabled session does not start.</summary>
    void Start();

    /// <summary>
    /// Settings-change reconciliation: a running break is honored to its end
    /// (the new schedule applies when it finishes); disabling mid-break ends
    /// the break immediately and stops the timer; re-enabling restarts the
    /// work phase from a full interval.
    /// </summary>
    void Refresh(EyeRestRuntimeSettings settings);

    /// <summary>Skips the running break. No-op while working.</summary>
    void SkipCurrentBreak();

    /// <summary>Starts the next break immediately. No-op during a break or when disabled.</summary>
    void ForceBreakNow();
}
