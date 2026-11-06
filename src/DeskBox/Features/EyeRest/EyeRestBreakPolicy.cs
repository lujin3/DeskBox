namespace DeskBox.Features.EyeRest;

/// <summary>Which break class a cycle produces: short eye pauses or long rests.</summary>
public enum EyeRestBreakKind
{
    Mini,
    Long
}

/// <summary>What a single policy tick produced: nothing, a break starting, the one-shot long-break preview, or a break ending.</summary>
public enum EyeRestBreakEventKind
{
    None,
    BreakDue,
    BreakImminent,
    BreakEnded
}

/// <summary>
/// One tick's outcome. <c>BreakDue</c> carries the break duration as
/// <paramref name="Remaining"/>; <c>BreakEnded</c> carries zero; the other
/// kinds carry the current phase remainder.
/// </summary>
public sealed record EyeRestBreakEvent(
    EyeRestBreakEventKind EventKind,
    EyeRestBreakKind BreakKind,
    TimeSpan Remaining);

/// <summary>
/// Validated break schedule. Values are converted and clamped by
/// <see cref="EyeRestBreakScheduleFactory"/> before they get here.
/// </summary>
public sealed record EyeRestBreakSchedule(
    TimeSpan WorkInterval,
    TimeSpan MiniBreakDuration,
    TimeSpan LongBreakDuration,
    int LongBreakEveryMiniBreaks,
    TimeSpan LongBreakLeadTime);

/// <summary>
/// Builds a clamped schedule from raw settings values (minutes/seconds as
/// stored in <c>EyeRestSettingsSlice</c>). The long-break preview lead time is
/// a fixed 30 seconds by design decision, not a user setting.
/// </summary>
public static class EyeRestBreakScheduleFactory
{
    public const int MinWorkIntervalMinutes = 1;
    public const int MaxWorkIntervalMinutes = 180;
    public const int MinMiniBreakSeconds = 5;
    public const int MaxMiniBreakSeconds = 300;
    public const int MinLongBreakMinutes = 1;
    public const int MaxLongBreakMinutes = 60;
    public const int MinLongBreakEveryMiniBreaks = 1;
    public const int MaxLongBreakEveryMiniBreaks = 12;

    /// <summary>Design-fixed long-break preview lead time (30s).</summary>
    public static readonly TimeSpan LongBreakLeadTime = TimeSpan.FromSeconds(30);

    public static EyeRestBreakSchedule Create(
        int workIntervalMinutes,
        int miniBreakSeconds,
        int longBreakMinutes,
        int longBreakEveryMiniBreaks)
    {
        return new EyeRestBreakSchedule(
            TimeSpan.FromMinutes(Math.Clamp(workIntervalMinutes, MinWorkIntervalMinutes, MaxWorkIntervalMinutes)),
            TimeSpan.FromSeconds(Math.Clamp(miniBreakSeconds, MinMiniBreakSeconds, MaxMiniBreakSeconds)),
            TimeSpan.FromMinutes(Math.Clamp(longBreakMinutes, MinLongBreakMinutes, MaxLongBreakMinutes)),
            Math.Clamp(longBreakEveryMiniBreaks, MinLongBreakEveryMiniBreaks, MaxLongBreakEveryMiniBreaks),
            LongBreakLeadTime);
    }
}

/// <summary>Current phase of the break cycle.</summary>
public enum EyeRestPhase
{
    Work,
    MiniBreak,
    LongBreak
}

/// <summary>
/// Pure-logic break cycle state machine (no WinUI or timer dependencies).
/// Semantics, per the design decisions:
/// <list type="bullet">
/// <item><b>Counting at break start.</b> A mini break increments
/// <see cref="MiniBreaksSinceLongBreak"/> when it *begins*; a long break resets
/// the counter when it begins. Skipping a mini break therefore still counts
/// toward the long-break cadence.</item>
/// <item><b>Auto transitions.</b> <see cref="Tick"/> moves Work→break at the
/// work interval and break→Work at the break duration, emitting
/// BreakDue/BreakEnded; a single tick larger than the remainder produces one
/// event, never a burst.</item>
/// <item><b>One-shot preview.</b> While working toward a long break, the first
/// tick whose remainder lands at/below the lead time emits BreakImminent once
/// per work cycle; never before a mini break.</item>
/// <item><b>Escape hatches.</b> <see cref="SkipBreak"/> ends the current break
/// early (Work-phase call is a no-op); <see cref="ForceBreak"/> starts the
/// next break immediately (break-phase call is a no-op) with normal counting.</item>
/// </list>
/// </summary>
public sealed class EyeRestBreakPolicy
{
    private readonly EyeRestBreakSchedule _schedule;
    private EyeRestPhase _phase = EyeRestPhase.Work;
    private TimeSpan _remaining;
    private int _miniBreaksSinceLongBreak;
    private bool _longBreakImminentAnnounced;

    public EyeRestBreakPolicy(EyeRestBreakSchedule schedule)
    {
        _schedule = schedule;
        _remaining = schedule.WorkInterval;
    }

    public EyeRestPhase Phase => _phase;

    /// <summary>Remainder of the current phase.</summary>
    public TimeSpan Remaining => _remaining;

    /// <summary>Mini breaks started since the last long break (skip-inclusive).</summary>
    public int MiniBreaksSinceLongBreak => _miniBreaksSinceLongBreak;

    public EyeRestBreakKind NextBreakKind =>
        _miniBreaksSinceLongBreak >= _schedule.LongBreakEveryMiniBreaks
            ? EyeRestBreakKind.Long
            : EyeRestBreakKind.Mini;

    /// <summary>
    /// Advances the cycle by <paramref name="delta"/> and returns the single
    /// event this tick produced (see class remarks for the transition rules).
    /// </summary>
    public EyeRestBreakEvent Tick(TimeSpan delta)
    {
        if (delta <= TimeSpan.Zero)
        {
            return NoneEvent();
        }

        if (_phase == EyeRestPhase.Work)
        {
            _remaining -= delta;
            if (_remaining > TimeSpan.Zero)
            {
                if (!_longBreakImminentAnnounced &&
                    NextBreakKind == EyeRestBreakKind.Long &&
                    _remaining <= _schedule.LongBreakLeadTime)
                {
                    _longBreakImminentAnnounced = true;
                    return new EyeRestBreakEvent(
                        EyeRestBreakEventKind.BreakImminent,
                        EyeRestBreakKind.Long,
                        _remaining);
                }

                return NoneEvent();
            }

            EyeRestBreakKind dueKind = NextBreakKind;
            BeginBreakCore(dueKind);
            return new EyeRestBreakEvent(EyeRestBreakEventKind.BreakDue, dueKind, _remaining);
        }

        EyeRestBreakKind endingKind = _phase == EyeRestPhase.LongBreak
            ? EyeRestBreakKind.Long
            : EyeRestBreakKind.Mini;
        _remaining -= delta;
        if (_remaining > TimeSpan.Zero)
        {
            return NoneEvent();
        }

        return EndBreakCore(endingKind);
    }

    /// <summary>Ends the current break early. Counting already happened at break start and is not rolled back. No-op while working.</summary>
    public void SkipBreak()
    {
        if (_phase == EyeRestPhase.Work)
        {
            return;
        }

        EyeRestBreakKind endingKind = _phase == EyeRestPhase.LongBreak
            ? EyeRestBreakKind.Long
            : EyeRestBreakKind.Mini;
        EndBreakCore(endingKind);
    }

    /// <summary>Starts the next break immediately (tray "break now"). No-op during a break.</summary>
    public void ForceBreak()
    {
        if (_phase != EyeRestPhase.Work)
        {
            return;
        }

        BeginBreakCore(NextBreakKind);
    }

    private void BeginBreakCore(EyeRestBreakKind kind)
    {
        _phase = kind == EyeRestBreakKind.Long ? EyeRestPhase.LongBreak : EyeRestPhase.MiniBreak;
        _remaining = kind == EyeRestBreakKind.Long
            ? _schedule.LongBreakDuration
            : _schedule.MiniBreakDuration;
        if (kind == EyeRestBreakKind.Long)
        {
            _miniBreaksSinceLongBreak = 0;
        }
        else
        {
            _miniBreaksSinceLongBreak++;
        }

        // The preview belongs to the work cycle that just ended; the next one
        // starts from a clean slate.
        _longBreakImminentAnnounced = false;
    }

    private EyeRestBreakEvent EndBreakCore(EyeRestBreakKind endedKind)
    {
        _phase = EyeRestPhase.Work;
        _remaining = _schedule.WorkInterval;
        _longBreakImminentAnnounced = false;
        return new EyeRestBreakEvent(EyeRestBreakEventKind.BreakEnded, endedKind, TimeSpan.Zero);
    }

    private EyeRestBreakEvent NoneEvent() => new(
        EyeRestBreakEventKind.None,
        _phase == EyeRestPhase.Work ? NextBreakKind : (_phase == EyeRestPhase.LongBreak ? EyeRestBreakKind.Long : EyeRestBreakKind.Mini),
        _remaining);
}
