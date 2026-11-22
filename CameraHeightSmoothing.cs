namespace ThirdPersonActionCamera;

internal static class CameraHeightSmoothing
{
    internal const float HeightThresholdCm = 15f;
    private const float FullResponseHeightCm = 25f;

    internal static float Response(float rise)
    {
        float blend = Math.Clamp((rise * 100f - HeightThresholdCm) /
            (FullResponseHeightCm - HeightThresholdCm), 0f, 1f);
        return blend * blend * (3f - 2f * blend);
    }

    internal static float Advance(float current, float desired, float response, float smoothing,
        float seconds, ref float velocity)
    {
        if (smoothing <= 0f)
        {
            velocity = 0f;
            return desired;
        }

        float slowTime = .075f + .18f * smoothing;
        float fastTime = .02f + .02f * smoothing;
        float rate = 2f / (slowTime + (fastTime - slowTime) * response);
        float difference = current - desired;
        float settlingVelocity = -rate * difference;
        // Limit momentum carried into a slower response so the spring settles without overshoot.
        if (difference == 0f) velocity = 0f;
        else velocity = difference > 0f ? Math.Max(velocity, settlingVelocity) : Math.Min(velocity, settlingVelocity);
        float slope = velocity + rate * difference;
        float decay = MathF.Exp(-rate * seconds);
        velocity = (velocity - rate * slope * seconds) * decay;
        return desired + (difference + slope * seconds) * decay;
    }
}
