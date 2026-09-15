namespace Broiler.HtmlBridge;

internal readonly record struct AnimationTiming(
    double DurationMs, double DelayMs, string Easing, string Fill,
    double Iterations, double IterationStart);
