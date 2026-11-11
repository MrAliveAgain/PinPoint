using System.Diagnostics;

namespace PinPoint.Core.Timing;

public enum PinTimerMode
{
    Stopwatch,
    Countdown,
}

/// <summary>
/// The clock behind a timer pin: a stopwatch counting up, or a countdown from <see cref="Duration"/>.
/// Pure logic (no UI, no timers of its own) so it can be unit tested; the window just polls <see cref="Display"/>.
/// </summary>
public sealed class PinTimer
{
    private readonly Func<TimeSpan> _clock;
    private TimeSpan _banked;      // time accumulated before the current run
    private TimeSpan? _runStarted; // clock reading when the current run started

    public PinTimer(Func<TimeSpan>? clock = null)
    {
        _clock = clock ?? (() => Stopwatch.GetElapsedTime(0));
    }

    /// <summary>New timers start as a 5-minute countdown.</summary>
    public PinTimerMode Mode { get; private set; } = PinTimerMode.Countdown;

    /// <summary>Countdown length (ignored by the stopwatch).</summary>
    public TimeSpan Duration { get; private set; } = TimeSpan.FromMinutes(5);

    public bool IsRunning => _runStarted is not null;

    public TimeSpan Elapsed => _banked + (_runStarted is { } started ? _clock() - started : TimeSpan.Zero);

    /// <summary>Countdown reached zero (it stops itself there).</summary>
    public bool IsFinished => Mode == PinTimerMode.Countdown && Elapsed >= Duration;

    /// <summary>What to show: time counted up, or time left (never below zero).</summary>
    public TimeSpan Display => Mode == PinTimerMode.Stopwatch
        ? Elapsed
        : Duration - Elapsed is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero;

    public void Start()
    {
        if (IsRunning) return;
        if (IsFinished) Reset();
        _runStarted = _clock();
    }

    public void Pause()
    {
        if (_runStarted is not { } started) return;
        _banked += _clock() - started;
        _runStarted = null;
    }

    public void Toggle()
    {
        if (IsRunning) Pause();
        else Start();
    }

    public void Reset()
    {
        _banked = TimeSpan.Zero;
        _runStarted = null;
    }

    /// <summary>Call regularly while running: stops a countdown exactly at zero. Returns true once, when it finishes.</summary>
    public bool CheckFinished()
    {
        if (!IsRunning || !IsFinished) return false;
        Pause();
        _banked = Duration;
        return true;
    }

    public void SetMode(PinTimerMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        Reset();
    }

    /// <summary>Sets the countdown length (1 second to 24 hours) and switches to countdown, from the top.</summary>
    public void SetDuration(TimeSpan duration)
    {
        Duration = TimeSpan.FromSeconds(Math.Clamp(Math.Round(duration.TotalSeconds), 1, 24 * 3600));
        Mode = PinTimerMode.Countdown;
        Reset();
    }

    /// <summary>Nudges the countdown length while it's stopped at the start (mouse wheel).</summary>
    public void AdjustDuration(TimeSpan delta)
    {
        if (IsRunning || Elapsed > TimeSpan.Zero) return;
        SetDuration(Duration + delta < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : Duration + delta);
    }

    /// <summary>"4:05", "1:02:03" (whole seconds, rounded up for a countdown so it shows 0:00 only at the end).</summary>
    public static string Format(TimeSpan time, bool roundUp)
    {
        long seconds = roundUp ? (long)Math.Ceiling(time.TotalSeconds - 1e-9) : (long)Math.Floor(time.TotalSeconds);
        if (seconds < 0) seconds = 0;
        long h = seconds / 3600, m = seconds / 60 % 60, s = seconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    public string DisplayText => Format(Display, roundUp: Mode == PinTimerMode.Countdown);

    /// <summary>
    /// Reads a typed countdown length, microwave style: the last two digits are seconds, the two before are
    /// minutes, anything before that hours ("425" = 4:25, "90" = 1:30, "10000" = 1:00:00). "4:25" and
    /// "1:02:03" work too. False for empty, zero or unreadable input.
    /// </summary>
    public static bool TryParseTyped(string? text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        string s = (text ?? string.Empty).Trim();
        if (s.Length == 0) return false;

        long h = 0, m, sec;
        if (s.Contains(':'))
        {
            string[] parts = s.Split(':');
            if (parts.Length > 3 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit))) return false;
            long[] n = [.. parts.Select(long.Parse)];
            (h, m, sec) = n.Length == 3 ? (n[0], n[1], n[2]) : (0, n[0], n[1]);
        }
        else
        {
            if (s.Length > 6 || !s.All(char.IsAsciiDigit)) return false;
            long digits = long.Parse(s);
            sec = digits % 100;
            m = digits / 100 % 100;
            h = digits / 10000;
        }

        time = TimeSpan.FromSeconds(h * 3600 + m * 60 + sec);
        return time > TimeSpan.Zero && time <= TimeSpan.FromHours(24);
    }
}
