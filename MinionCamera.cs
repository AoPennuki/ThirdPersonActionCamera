using System.Diagnostics;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace ThirdPersonActionCamera;

internal sealed unsafe class MinionCamera : IDisposable
{
    // Verified against the local Companion and ModelChara sheets.
    private const string ModelPath = "chara/monster/m8111/obj/body/b0001/model/m8111b0001.mdl";
    private BgObject* proxy;
    private nint owner, rig;
    private int anchorIndex = -1;
    private bool useHead;
    private long previousFrame, lastObstruction;
    private Vector2? restingHeights;
    private float? lastGroundY;
    private float offset, velocity;
    private float frameSeconds, nativeClearance = 1f;
    internal HeightSample? Height { get; private set; }
    internal readonly record struct HeightSample(float GroundY, float AnchorY, bool Grounded);

    internal void Update(GameObject* player, bool perchOnHead)
    {
        Height = null;
        if (player == null)
        {
            Dispose();
            return;
        }
        var pose = PlayerSkeleton.GetPose(player, out var model);
        if (pose == null) return;
        if (owner != (nint)model || rig != (nint)pose->Skeleton || useHead != perchOnHead)
        {
            owner = (nint)model;
            rig = (nint)pose->Skeleton;
            useHead = perchOnHead;
            Reset();
            anchorIndex = -1;
            string name = useHead ? "j_kao" : "n_hkata_r";
            for (int index = 0; index < pose->Skeleton->Bones.Length; index++)
            {
                if (pose->Skeleton->Bones[index].Name.String != name) continue;
                anchorIndex = index;
                break;
            }
        }
        if (anchorIndex < 0) return;
        var transforms = PlayerSkeleton.ModelTransforms(pose);
        if (transforms == null || transforms->Data == null || anchorIndex >= transforms->Length ||
            !PlayerSkeleton.TryFrame(model, out var scale, out var modelRotation)) return;
        var bone = transforms->Data[anchorIndex];
        var point = new Vector3(bone.Translation.X, bone.Translation.Y, bone.Translation.Z);
        // Model-space bone translation includes root animation. Compose it
        // with the rendered world position so jumps affect the anchor too.
        Vector3 world = (Vector3)model->Skeleton->Transform.Position + Vector3.Transform(point * scale, modelRotation);
        Vector3 feet = player->Position;
        if (!PlayerSkeleton.Finite(world) || !PlayerSkeleton.Finite(feet)) return;

        PrepareProxy();
        if (!TryMoveProxy(world, modelRotation, out var anchor)) return;
        Height = SampleHeight(feet.Y, anchor.Y, FindGroundBelow(feet));
    }

    private void PrepareProxy()
    {
        if (proxy != null) return;
        if (BgObject.MemberFunctionPointers.Create == null)
            throw new InvalidOperationException("Client-side world model creation is unavailable.");
        proxy = BgObject.Create(ModelPath, string.Empty);
        if (proxy == null)
            throw new InvalidOperationException("Could not create the invisible minion model.");
        proxy->IsVisible = false;
    }

    private bool TryMoveProxy(Vector3 world, Quaternion rotation, out Vector3 position)
    {
        position = default;
        if (proxy == null || !PlayerSkeleton.Finite(world)) return false;
        proxy->IsVisible = false;
        proxy->Position = world;
        proxy->Rotation = rotation;
        proxy->IsTransformChanged = true;
        position = proxy->Position;
        return true;
    }

    private static float? FindGroundBelow(Vector3 feet) =>
        PlayerSkeleton.Finite(feet) && BGCollisionModule.MemberFunctionPointers.RaycastMaterialFilter != null &&
        BGCollisionModule.RaycastMaterialFilter(feet + .2f * Vector3.UnitY, -Vector3.UnitY, out var ground, 8f)
            ? ground.Point.Y : null;

    internal static bool IsGrounded(float feetY, float surfaceY) =>
        float.IsFinite(surfaceY) && feetY - surfaceY < .10f;

    private HeightSample? SampleHeight(float feetY, float anchorY, float? surfaceY)
    {
        if (surfaceY is float groundY && IsGrounded(feetY, groundY))
        {
            lastGroundY = groundY;
            return new HeightSample(groundY, anchorY, true);
        }
        // Keep the takeoff surface while airborne, even beyond the ground ray's reach.
        return lastGroundY is float takeoffY ? new HeightSample(takeoffY, anchorY, false) : null;
    }

    internal float Follow(float nativePivotY, HeightSample? sample, float strength, float smoothing, long now)
    {
        frameSeconds = 1f / 60f;
        if (previousFrame == 0)
        {
            offset = velocity = default;
        }
        else
        {
            frameSeconds = Math.Clamp((float)Stopwatch.GetElapsedTime(previousFrame, now).TotalSeconds, 0f, .05f);
        }
        previousFrame = now;

        float desired = 0f;
        float dynamicResponse = 0f;
        if (sample is { } height && float.IsFinite(nativePivotY))
        {
            var heights = new Vector2(height.AnchorY - height.GroundY, nativePivotY - height.GroundY);
            if (height.Grounded) restingHeights ??= heights;
            if (restingHeights is { } rest)
            {
                var rise = heights - rest;
                dynamicResponse = CameraHeightSmoothing.Response(rise.X);
                // The native pivot has already absorbed part of the rise.
                desired = Math.Max(0f, rise.X - rise.Y) * strength;
                if (height.Grounded && rise.X <= CameraHeightSmoothing.HeightThresholdCm * .01f)
                    restingHeights = Vector2.Lerp(rest, heights, 1f - MathF.Exp(-frameSeconds / .45f));
            }
        }
        if (lastObstruction != 0 && Stopwatch.GetElapsedTime(lastObstruction, now).TotalSeconds < .18)
        {
            desired = 0f;
            dynamicResponse = 0f;
        }

        offset = CameraHeightSmoothing.Advance(offset, desired, dynamicResponse, smoothing, frameSeconds, ref velocity);
        return Math.Max(0f, offset);
    }

    internal float LimitByCollision(Vector3 position, Vector3 lookAt, float nativeCompression, float height, long now)
    {
        // Native camera compression often changes while pitching near the floor.
        // Fade supplemental motion before checking the actual camera path.
        float desiredClearance = float.IsFinite(nativeCompression)
            ? 1f - Math.Clamp(nativeCompression - 1f, 0f, 1f) : 1f;
        float clearanceTime = desiredClearance < nativeClearance ? .06f : .15f;
        nativeClearance += (desiredClearance - nativeClearance) * (1f - MathF.Exp(-frameSeconds / clearanceTime));
        height *= nativeClearance;
        if (height <= .001f || BGCollisionModule.MemberFunctionPointers.RaycastMaterialFilter == null) return height;

        const float clearance = .15f;
        if (BGCollisionModule.RaycastMaterialFilter(position, Vector3.UnitY, out var ceiling, height + clearance))
            return BlockHeight(Math.Clamp(ceiling.Point.Y - position.Y - clearance, 0f, height), now);

        var view = lookAt - position;
        float distance = view.Length();
        var raised = position + height * Vector3.UnitY;
        if (distance > .01f && BGCollisionModule.RaycastMaterialFilter(raised, view / distance, out _, distance))
            return BlockHeight(0f, now);
        return height;
    }

    private float BlockHeight(float allowed, long now)
    {
        lastObstruction = now;
        offset = Math.Min(offset, allowed);
        velocity = Math.Min(velocity, 0f);
        return allowed;
    }

    internal void Reset()
    {
        previousFrame = lastObstruction = 0;
        restingHeights = null;
        lastGroundY = null;
        nativeClearance = 1f;
        Height = null;
    }

    public void Dispose()
    {
        Reset();
        var model = proxy;
        proxy = null;
        if (model == null) return;
        model->CleanupRender();
        model->Dtor(1);
    }
}
