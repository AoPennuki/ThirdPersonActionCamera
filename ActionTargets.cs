using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace ThirdPersonActionCamera;

internal static unsafe class ActionTargets
{
    internal static GameObject* Selected()
    {
        var targets = TargetSystem.Instance();
        var selected = targets == null ? null : targets->Target;
        return Id(selected) == 0 ? null : selected;
    }

    internal static ulong Id(GameObject* actor)
    {
        if (actor == null || !actor->GetIsTargetable()) return 0;
        ulong id = actor->GetGameObjectId();
        return id == 0xE0000000 ? 0 : id;
    }

    internal static ulong ForSkill(uint action, GameObject* player, GameObject* underReticle,
        GameObject* selected, out bool fromSelection)
    {
        fromSelection = false;
        if (player == null) return 0;
        ulong hoveredId = CompatibleId(action, underReticle);
        if (underReticle == selected || (hoveredId != 0 && (selected == null || InRange(action, player, underReticle))))
            return hoveredId;
        ulong selectedId = CompatibleId(action, selected);
        if (selectedId == 0 || (hoveredId != 0 && !InRange(action, player, selected))) return hoveredId;
        // Passing the current target directly preserves native auto-face, error feedback and action queuing.
        fromSelection = true;
        return selectedId;
    }

    private static ulong CompatibleId(uint action, GameObject* actor)
    {
        ulong id = Id(actor);
        return id != 0 && ActionManager.CanUseActionOnTarget(action, actor) ? id : 0;
    }

    private static bool InRange(uint action, GameObject* player, GameObject* actor) =>
        ActionManager.MemberFunctionPointers.GetActionInRangeOrLoS == null ||
        ActionManager.GetActionInRangeOrLoS(action, player, actor) == 0;
}
