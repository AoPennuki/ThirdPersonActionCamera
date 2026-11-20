using System.Numerics;
using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;
using SceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;
using NativeVector = FFXIVClientStructs.FFXIV.Common.Math.Vector3;

namespace ThirdPersonActionCamera;

internal enum ZoomStyle { Dolly, LensWithPan, LensOnly }

internal static class ShoulderFraming
{
    internal const byte LalafellRace = 4;
    private const float LalafellScreenRaise = .14f;
    private const float LalafellReticleHeight = .43f;
    private const float ReticleHeadClearance = .025f;

#if ACTIONCAM_DOLLY
    internal const float DefaultMagnification = 3.0f;
    internal static readonly ZoomStyle Style = ZoomStyle.Dolly;
#elif ACTIONCAM_LENS_ONLY
    internal const float DefaultMagnification = 1.65f;
    internal static readonly ZoomStyle Style = ZoomStyle.LensOnly;
#else
    internal const float DefaultMagnification = 1.65f;
    internal static readonly ZoomStyle Style = ZoomStyle.LensWithPan;
#endif
    internal const float MinimumMagnification = 1.1f;
    internal const float MaximumMagnification = 3.0f;
    internal const float TransitionSeconds = .20f;

    internal static float AdvanceAim(float current, bool held, float seconds)
    {
        float step = seconds / TransitionSeconds;
        return Math.Clamp(current + (held ? step : -step), 0f, 1f);
    }

    internal static float Magnification(float aim, float maximum) => 1f + (maximum - 1f) * aim;

    internal static float ZoomFieldOfView(float fieldOfView, float magnification) =>
        2f * MathF.Atan(MathF.Tan(fieldOfView * .5f) / magnification);

    // Tilting down moves a fixed world point up in the rendered image.
    internal static float AimElevation(byte race, float unzoomedFieldOfView) =>
        race == LalafellRace && unzoomedFieldOfView is > 0f and < MathF.PI
            ? -MathF.Atan(2f * LalafellScreenRaise * MathF.Tan(unzoomedFieldOfView * .5f))
            : 0f;

    internal static float LalafellReticleY(float headY) =>
        float.IsFinite(headY)
            ? Math.Clamp(headY - ReticleHeadClearance, .08f, LalafellReticleHeight)
            : LalafellReticleHeight;

    internal static (Vector3 Position, Vector3 LookAt) Offsets(Vector3 position, Vector3 lookAt, float shoulder,
        float magnification, ZoomStyle style, float elevation)
    {
        var view = lookAt - position;
        float distance = view.Length();
        var side = Vector3.Cross(view, Vector3.UnitY);
        float horizontal = side.Length();
        if (!float.IsFinite(distance) || !float.IsFinite(horizontal) || distance < .01f || horizontal < .01f)
            return default;

        // Fade the shift near an already close camera, such as when the
        // engine pulls the orbit in during a collision.
        float clearance = Math.Clamp((distance - 1.1f) / 1.4f, 0f, 1f);
        float lateral = shoulder * .62f * clearance;
        var right = side / horizontal;
        var offset = right * lateral;
        if (style == ZoomStyle.Dolly)
        {
            // Follow the ray from the shoulder camera toward the unshifted
            // look-at point. Heading stays fixed as the origin moves inward.
            float approach = Math.Min(1f - 1f / magnification, Math.Max(0f, 1f - 1.1f / distance));
            var translated = offset * (1f - approach) + view * approach;
            return (translated, translated + ElevationDelta(view, elevation));
        }
        if (style == ZoomStyle.LensOnly) return (offset, offset + ElevationDelta(view, elevation));

        // Pan the optical axis toward the character as the lens tightens.
        // Keep the same screen-side clearance as the unzoomed shoulder view.
        float bearing = MathF.Atan2(lateral, distance);
        float desiredBearing = MathF.Atan2(lateral / magnification, distance);
        float pan = desiredBearing - bearing;
        var pannedView = view * MathF.Cos(pan) + right * (MathF.Sin(pan) * distance);
        return (offset, offset + (pannedView - view) + ElevationDelta(pannedView, elevation));
    }

    private static Vector3 ElevationDelta(Vector3 view, float elevation)
    {
        if (elevation == 0f) return default;
        var right = Vector3.Cross(view, Vector3.UnitY);
        float horizontal = right.Length();
        float pitch = MathF.Atan2(view.Y, horizontal);
        const float pitchLimit = 89.5f * MathF.PI / 180f;
        float rotation = Math.Clamp(elevation, Math.Min(0f, -pitchLimit - pitch), Math.Max(0f, pitchLimit - pitch));
        var up = Vector3.Cross(right / horizontal, view);
        return view * (MathF.Cos(rotation) - 1f) + up * MathF.Sin(rotation);
    }
}

internal struct SmoothAim
{
    internal float Value { get; private set; }
    internal float Velocity { get; private set; }

    internal float Advance(bool held, float seconds)
    {
        const float rate = 17f;
        float target = held ? 1f : 0f;
        float difference = Value - target;
        float slope = Velocity + rate * difference;
        float decay = MathF.Exp(-rate * seconds);
        Value = target + (difference + slope * seconds) * decay;
        Velocity = (Velocity - rate * slope * seconds) * decay;
        if (Value <= 0f || Value >= 1f || (MathF.Abs(Value - target) < .0001f && MathF.Abs(Velocity) < .001f))
        {
            Value = Value <= 0f ? 0f : Value >= 1f ? 1f : target;
            Velocity = 0f;
        }
        return Value;
    }
}

internal sealed unsafe class LensZoom
{
    private nint owner;
    private Lens original;
    private float applied;

    private readonly record struct Lens(float FieldOfView, float Minimum, float Maximum);

    internal float UnzoomedFieldOfView(GameCamera* camera) =>
        owner == (nint)camera && (camera->FoV == applied || (camera->MinFoV == applied && camera->MaxFoV == applied))
            ? original.FieldOfView : camera->FoV;

    internal void Apply(GameCamera* camera, float magnification)
    {
        if (magnification <= 1f)
        {
            Restore(camera);
            return;
        }
        bool same = owner == (nint)camera;
        var baseline = new Lens(
            UnzoomedFieldOfView(camera),
            same && camera->MinFoV == applied ? original.Minimum : camera->MinFoV,
            same && camera->MaxFoV == applied ? original.Maximum : camera->MaxFoV);
        if (!(baseline.FieldOfView is > 0f and < MathF.PI))
        {
            Restore(camera);
            return;
        }
        original = baseline;
        applied = ShoulderFraming.ZoomFieldOfView(baseline.FieldOfView, magnification);
        owner = (nint)camera;
        camera->FoV = camera->MinFoV = camera->MaxFoV = applied;
    }

    internal void Restore(GameCamera* camera)
    {
        if (camera == null || owner == 0 || owner != (nint)camera) return;
        if (camera->FoV == applied || (camera->MinFoV == applied && camera->MaxFoV == applied))
            camera->FoV = original.FieldOfView;
        if (camera->MinFoV == applied) camera->MinFoV = original.Minimum;
        if (camera->MaxFoV == applied) camera->MaxFoV = original.Maximum;
        owner = 0;
    }
}

internal readonly unsafe struct ShoulderViewScope
{
    private readonly bool active;
    private readonly NativeVector position, lookAt, shiftedPosition, shiftedLookAt;

    internal ShoulderViewScope(SceneCamera* scene, (Vector3 Position, Vector3 LookAt) offset)
    {
        if (scene == null || offset == default) return;
        position = scene->Position;
        lookAt = scene->LookAtVector;
        shiftedPosition = (Vector3)position + offset.Position;
        shiftedLookAt = (Vector3)lookAt + offset.LookAt;
        scene->Position = shiftedPosition;
        scene->LookAtVector = shiftedLookAt;
        active = true;
    }

    internal void Restore(SceneCamera* scene)
    {
        if (!active) return;
        if (scene->Position.Equals(shiftedPosition)) scene->Position = position;
        if (scene->LookAtVector.Equals(shiftedLookAt)) scene->LookAtVector = lookAt;
    }
}
