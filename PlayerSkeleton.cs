using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Container.Array;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using CharacterBase = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase;
using Skeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace ThirdPersonActionCamera;

internal static unsafe class PlayerSkeleton
{
    private const int CharacterModelScaleOffset = 0x2A4;

    private static CharacterBase* GetModel(GameObject* actor)
    {
        var model = actor == null || actor->DrawObject == null || actor->VirtualTable == null ||
            actor->VirtualTable->GetCharacterBase == null ? null : actor->GetCharacterBase();
        return model != null && (nint)model == (nint)actor->DrawObject ? model : null;
    }

    internal static hkaPose* GetPose(GameObject* actor, out CharacterBase* model)
    {
        model = GetModel(actor);
        return model == null ? null : GetBasePose(model->Skeleton);
    }

    private static hkaPose* GetBasePose(Skeleton* skeleton)
    {
        if (skeleton == null || skeleton->PartialSkeletons == null || skeleton->PartialSkeletonCount is 0 or > 32)
            return null;
        var pose = skeleton->PartialSkeletons->GetHavokPose(0);
        return pose != null && pose->Skeleton != null && pose->Skeleton->Bones.Data != null &&
            pose->Skeleton->Bones.Length is > 0 and <= 2048 && pose->ModelPose.Data != null &&
            pose->ModelPose.Length == pose->Skeleton->Bones.Length ? pose : null;
    }

    internal static hkArray<hkQsTransformf>* ModelTransforms(hkaPose* pose) =>
        pose->ModelInSync != 0 ? &pose->ModelPose :
        hkaPose.MemberFunctionPointers.GetSyncedPoseModelSpace == null ? null : pose->GetSyncedPoseModelSpace();

    internal static bool TryFrame(CharacterBase* model, out Vector3 scale, out Quaternion rotation)
    {
        bool validScale = TryScale(model, out scale);
        return Normalize(model->Skeleton->Transform.Rotation, out rotation) && validScale;
    }

    private static bool TryScale(CharacterBase* model, out Vector3 scale)
    {
        scale = (Vector3)model->Scale * *(float*)((byte*)model + CharacterModelScaleOffset);
        return Finite(scale) && scale.X is > 0f and <= 4f && scale.Y is > 0f and <= 4f && scale.Z is > 0f and <= 4f;
    }

    private static bool Normalize(Quaternion value, out Quaternion rotation)
    {
        rotation = Quaternion.Identity;
        float squared = value.LengthSquared();
        if (!float.IsFinite(squared) || squared < .000001f) return false;
        rotation = Quaternion.Normalize(value);
        return true;
    }

    internal static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
