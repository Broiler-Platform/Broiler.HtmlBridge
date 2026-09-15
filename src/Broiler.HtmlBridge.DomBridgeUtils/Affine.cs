namespace Broiler.HtmlBridge;

/// <summary>A CSS 2D affine transform <c>matrix(a,b,c,d,e,f)</c> mapping a point
/// <c>(x,y) → (a·x + c·y + e, b·x + d·y + f)</c>.</summary>
internal readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

    public bool IsIdentity =>
        A == 1 && B == 0 && C == 0 && D == 1 && E == 0 && F == 0;

    /// <summary>The matrix that applies <c>this</c> first and then <paramref name="next"/>.</summary>
    public Affine Then(Affine next) => new(
        next.A * A + next.C * B,
        next.B * A + next.D * B,
        next.A * C + next.C * D,
        next.B * C + next.D * D,
        next.A * E + next.C * F + next.E,
        next.B * E + next.D * F + next.F);
}
