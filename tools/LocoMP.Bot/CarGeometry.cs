namespace LocoMP.Bot;

/// <summary>
/// One car's real placement geometry, measured off the host's spawned TrainCar (the host log's
/// 'bot spacing hint'). Length = coupler pitch (InterCouplerDistance, coupler anchor to coupler
/// anchor); the insets = along-car distance from each coupler anchor to its bogie pivot. DV seats
/// a spawned remote car BY ITS BOGIES (the Shim streams bogie track points and derives the body
/// pose from them), so a guessed inset shifts the whole body within its coupler span — the G3′
/// tuning finding: measured pitch made the FRONT joint vanilla-perfect while the rear stayed wide.
/// </summary>
public readonly struct CarGeometry
{
    public readonly double Length;
    /// <summary>Nose (front coupler anchor) → front bogie pivot. NaN = use the driver's
    /// min(3.5, len/4) guess (the pre-hint behavior, kept for --car-lengths).</summary>
    public readonly double FrontInset;
    /// <summary>Rear coupler anchor → rear bogie pivot. NaN = guess, as above.</summary>
    public readonly double RearInset;

    public CarGeometry(double length, double frontInset = double.NaN, double rearInset = double.NaN)
    {
        Length = length;
        FrontInset = frontInset;
        RearInset = rearInset;
    }
}
