using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace VajraShelf.Services;

/// <summary>
/// A single finite WPF progress clock. Reversals sample the animated value and
/// stale completion callbacks cannot commit an obsolete endpoint.
/// </summary>
public sealed class ShelfTransitionController : Animatable, IDisposable
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(ShelfTransitionController),
        new PropertyMetadata(0.0, OnProgressChanged));

    private AnimationClock? _clock;
    private EventHandler? _completed;
    private long _generation;
    private bool _disposed;

    public double Progress => (double)GetValue(ProgressProperty);
    public bool IsTransitioning { get; private set; }
    public bool TargetExpanded { get; private set; }
    public event Action<double>? ProgressChanged;
    public event Action<bool>? Settled;

    public void SetExpanded(bool expanded, bool animate)
    {
        VerifyAccess();
        if (_disposed) return;
        if (expanded == TargetExpanded && IsTransitioning && animate) return;

        double current = ShelfGeometry.NormalizeProgress(Progress);
        double target = expanded ? 1 : 0;
        long generation = ++_generation;
        TargetExpanded = expanded;
        RemoveClockAt(current);
        IsTransitioning = animate && Math.Abs(current - target) > 0.000001;
        if (!IsTransitioning)
        {
            SetValue(ProgressProperty, target);
            Settled?.Invoke(expanded);
            return;
        }

        var animation = new DoubleAnimation(current, target,
            ShelfGeometry.TransitionDuration(current, expanded))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        AnimationClock clock = animation.CreateClock();
        _clock = clock;
        _completed = (_, _) =>
        {
            if (_disposed || generation != _generation || !ReferenceEquals(_clock, clock)) return;
            IsTransitioning = false;
            RemoveClockAt(target);
            Settled?.Invoke(expanded);
        };
        clock.Completed += _completed;
        ApplyAnimationClock(ProgressProperty, clock, HandoffBehavior.SnapshotAndReplace);
    }

    public void Settle()
    {
        VerifyAccess();
        if (_disposed) return;
        ++_generation;
        IsTransitioning = false;
        RemoveClockAt(TargetExpanded ? 1 : 0);
        Settled?.Invoke(TargetExpanded);
    }

    private void RemoveClockAt(double progress)
    {
        SetValue(ProgressProperty, progress);
        if (_clock == null) return;
        if (_completed != null) _clock.Completed -= _completed;
        ApplyAnimationClock(ProgressProperty, null);
        _clock.Controller?.Remove();
        _clock = null;
        _completed = null;
    }

    private static void OnProgressChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var controller = (ShelfTransitionController)sender;
        if (!controller._disposed) controller.ProgressChanged?.Invoke((double)args.NewValue);
    }

    protected override Freezable CreateInstanceCore() => new ShelfTransitionController();

    public void Dispose()
    {
        VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        ++_generation;
        IsTransitioning = false;
        RemoveClockAt(TargetExpanded ? 1 : 0);
        ProgressChanged = null;
        Settled = null;
    }
}
