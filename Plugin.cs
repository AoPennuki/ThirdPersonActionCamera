using System.Numerics;
using System.Diagnostics;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
using Lumina.Excel;
using PovPlus;
using ActionRow = Lumina.Excel.Sheets.Action;
using SceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;
using SceneCameras = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace ThirdPersonActionCamera;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string Command = "/acti";
    private const string LegacyCommand = "/actioncam";
    private const string CommandHelp = "/acti on 开启，/acti off 关闭。";
    private const int ReleaseKey = MouseLookKeys.DefaultKey;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IClientState client;
    private readonly IObjectTable objects;
    private readonly ICondition conditions;
    private readonly ICommandManager commands;
    private readonly IChatGui chat;
    private readonly IGameInteropProvider interop;
    private readonly IPluginLog log;
    private readonly bool commandRegistered, legacyCommandRegistered;
    private readonly ExcelSheet<ActionRow> actions;
    private readonly MouseLookState state = new();
    private readonly MouseLookDragPatch drag = new();
    private readonly LensZoom lens = new();
    private Hook<MouseDevice.Delegates.ProcessMouseInputMessage>? mouseMessageHook;
    private Hook<AtkModule.Delegates.HandleInput>? inputHook;
    private Hook<Camera.Delegates.GetCameraInputSource>? cameraHook;
    private Hook<PronounModule.Delegates.ResolvePlaceholder>? macroHook;
    private Hook<ActionManager.Delegates.UseAction>? actionHook;
    private Hook<SceneCamera.Delegates.UpdateRender>? viewHook;
    private Hook<Camera.Delegates.Update>? lensHook;
    private AtkStage* cursorOwner;
    private bool requested, captured, platformTextInput, nativeTextInput, restoreVisible;
    private int centerX, centerY, restoreX, restoreY, dx, dy;
    private int shoulderSide = 1;
    private float blendedShoulder, aim;
    private SmoothAim smoothAim;
    private float aimMagnification = ShoulderFraming.DefaultMagnification;
    private long lastFrame;
    private Camera* lensCamera;
    private SceneCamera* framedCamera;
    private (Vector3 Position, Vector3 LookAt) framedOffset;
    private bool framingView;

    public Plugin(IDalamudPluginInterface pluginInterface, IFramework framework, IClientState client,
        IObjectTable objects, ICondition conditions, ICommandManager commands, IChatGui chat,
        IGameInteropProvider interop, IPluginLog log, IDataManager data)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.client = client;
        this.objects = objects;
        this.conditions = conditions;
        this.commands = commands;
        this.chat = chat;
        this.interop = interop;
        this.log = log;
        actions = data.GetExcelSheet<ActionRow>();
        commandRegistered = commands.AddHandler(Command, new CommandInfo(OnCommand) { HelpMessage = CommandHelp });
        legacyCommandRegistered = commands.AddHandler(LegacyCommand, new CommandInfo(OnCommand) { ShowInHelp = false });
        if (!commandRegistered) log.Warning("Could not register /acti; the existing /actioncam alias is still available if registered.");
        framework.Update += Update;
        pluginInterface.UiBuilder.Draw += Draw;
    }

    private void OnCommand(string _, string args)
    {
        string option = args.Trim().ToLowerInvariant();
        if (option.StartsWith("zoom ", StringComparison.Ordinal))
        {
            if (float.TryParse(option.AsSpan(5), NumberStyles.Float, CultureInfo.InvariantCulture, out var magnification) &&
                magnification is >= ShoulderFraming.MinimumMagnification and <= ShoulderFraming.MaximumMagnification)
            {
                aimMagnification = magnification;
                chat.Print(FormattableString.Invariant($"右键拉近倍率为 {aimMagnification:0.00}×。"));
            }
            else chat.Print("用法 /acti zoom 2，范围 1.1 到 3.0。");
            return;
        }
        switch (option)
        {
            case "on":
                try
                {
                    if (inputHook == null) InstallHooks();
                    requested = true;
                    chat.Print("动作镜头已开启。/acti off 关闭。");
                }
                catch (Exception ex)
                {
                    DisposeHooks();
                    Fail(ex);
                    chat.PrintError("第三人称动作镜头初始化失败，详情见 Dalamud 日志。");
                }
                break;
            case "off":
                requested = false;
                state.Reset();
                Release();
                DisposeHooks();
                chat.Print("第三人称动作镜头已关闭。");
                break;
            case "status":
                chat.Print(captured ? "第三人称动作镜头正在捕获鼠标。" : requested ? "第三人称动作镜头已启用，当前鼠标自由。" : "第三人称动作镜头已关闭。");
                break;
            case "left":
                shoulderSide = -1;
                chat.Print("第三人称动作镜头已切到左肩。");
                break;
            case "right":
                shoulderSide = 1;
                chat.Print("第三人称动作镜头已切到右肩。");
                break;
            case "center":
                shoulderSide = 0;
                chat.Print("第三人称动作镜头已切到居中。");
                break;
            default:
                chat.Print(CommandHelp);
                break;
        }
    }

    private void InstallHooks()
    {
        var control = Control.Instance();
        var camera = control == null ? null : control->CameraManager.Camera;
        var scene = camera == null ? null : &camera->SceneCamera;
        if (InputManager.Instance() == null ||
            (nint)MouseDevice.MemberFunctionPointers.ScheduleCursorMove == 0 ||
            (nint)MouseDevice.MemberFunctionPointers.ProcessMouseInputMessage == 0 ||
            (nint)AtkCursor.MemberFunctionPointers.SetVisible == 0 ||
            (nint)AtkModule.MemberFunctionPointers.IsTextInputActive == 0 ||
            (nint)TargetSystem.MemberFunctionPointers.GetMouseOverObject == 0 ||
            (nint)AtkModule.MemberFunctionPointers.HandleInput == 0 ||
            (nint)Camera.MemberFunctionPointers.GetCameraInputSource == 0 ||
            (nint)PronounModule.MemberFunctionPointers.ResolvePlaceholder == 0 ||
            (nint)ActionManager.MemberFunctionPointers.UseAction == 0 ||
            (nint)ActionManager.MemberFunctionPointers.GetAdjustedActionId == 0 ||
            (nint)ActionManager.MemberFunctionPointers.GetSpellIdForAction == 0 ||
            (nint)ActionManager.MemberFunctionPointers.CanUseActionOnTarget == 0 ||
            (ShoulderFraming.Style != ZoomStyle.Dolly &&
             (camera == null || camera->VirtualTable == null || (nint)camera->VirtualTable->Update == 0)) ||
            scene == null || scene->VirtualTable == null || (nint)scene->VirtualTable->UpdateRender == 0)
            throw new InvalidOperationException("Third-person input or native mouseover is unavailable.");

        mouseMessageHook = interop.HookFromAddress<MouseDevice.Delegates.ProcessMouseInputMessage>(
            (nint)MouseDevice.MemberFunctionPointers.ProcessMouseInputMessage, ProcessMouseInputMessage);
        inputHook = interop.HookFromAddress<AtkModule.Delegates.HandleInput>(
            (nint)AtkModule.MemberFunctionPointers.HandleInput, HandleInput);
        cameraHook = interop.HookFromAddress<Camera.Delegates.GetCameraInputSource>(
            (nint)Camera.MemberFunctionPointers.GetCameraInputSource, GetCameraSource);
        macroHook = interop.HookFromAddress<PronounModule.Delegates.ResolvePlaceholder>(
            (nint)PronounModule.MemberFunctionPointers.ResolvePlaceholder, ResolvePlaceholder);
        actionHook = interop.HookFromAddress<ActionManager.Delegates.UseAction>(
            (nint)ActionManager.MemberFunctionPointers.UseAction, UseAction);
        viewHook = interop.HookFromAddress<SceneCamera.Delegates.UpdateRender>(
            (nint)scene->VirtualTable->UpdateRender, UpdateRender);
        if (ShoulderFraming.Style != ZoomStyle.Dolly)
            lensHook = interop.HookFromAddress<Camera.Delegates.Update>(
                (nint)camera->VirtualTable->Update, UpdateLens);
        macroHook.Enable();
        actionHook.Enable();
        viewHook.Enable();
        lensHook?.Enable();
        cameraHook.Enable();
        mouseMessageHook.Enable();
        inputHook.Enable();
    }

    private Camera* ActiveThirdPersonCamera()
    {
        if (!requested || !MouseLookKeys.Foreground || !client.IsLoggedIn || client.IsGPosing ||
            objects.LocalPlayer == null || conditions[ConditionFlag.WatchingCutscene] ||
            conditions[ConditionFlag.WatchingCutscene78] || conditions[ConditionFlag.OccupiedInCutSceneEvent] ||
            conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51])
            return null;
        var control = Control.Instance();
        var scene = SceneCameras.Instance();
        if (control == null || scene == null) return null;
        var camera = control->CameraManager.Camera;
        if (camera == null || camera->ZoomMode != CameraZoomMode.ThirdPerson || camera->IsEventCameraAutoControl ||
            scene->CurrentCamera != &((CameraBase*)camera)->SceneCamera)
            return null;
        return camera;
    }

    private bool Paused => nativeTextInput || platformTextInput || MouseLookKeys.Down(ReleaseKey);
    private bool CaptureUsable => captured && !Paused && ActiveThirdPersonCamera() != null;

    private void ProcessMouseInputMessage(nint window, uint message, nint parameter)
    {
        const uint leftDown = 0x0201, leftDoubleClick = 0x0203;
        try
        {
            // Movement also reads native button state before the UI filter.
            if (message is leftDown or leftDoubleClick && CaptureUsable) return;
        }
        catch (Exception ex) { Fail(ex); }
        mouseMessageHook!.Original(window, message, parameter);
    }

    private byte HandleInput(AtkModule* module, UIInputData* input, bool padMouse)
    {
        drag.Restore();
        try
        {
            nativeTextInput = module != null && module->IsTextInputActive();
            var stage = AtkStage.Instance();
            var valid = input != null && stage != null && stage->ScreenSize.Width > 0 && stage->ScreenSize.Height > 0;
            var eligible = valid && !padMouse && input->CursorInputs.IsGameWindowFocused && ActiveThirdPersonCamera() != null;
            const MouseButtonFlags clicks = MouseButtonFlags.LBUTTON | MouseButtonFlags.RBUTTON;
            var held = valid && ((input->CursorInputs.MouseButtonHeldFlags | input->CursorInputs.MouseButtonPressedFlags) & clicks) != 0;
            if (state.Update(eligible, nativeTextInput || platformTextInput, MouseLookKeys.Down(ReleaseKey), false, ReleaseKey, held))
            {
                if (captured && cursorOwner != stage) Release();
                int x = stage->ScreenSize.Width / 2, y = stage->ScreenSize.Height / 2;
                bool continuing = captured && centerX == x && centerY == y;
                dx = continuing ? input->CursorInputs.DeltaX : 0;
                dy = continuing ? input->CursorInputs.DeltaY : 0;
                centerX = x;
                centerY = y;
                if (!captured)
                {
                    restoreX = input->CursorInputs.PositionX;
                    restoreY = input->CursorInputs.PositionY;
                    restoreVisible = stage->AtkCursor.IsVisible;
                    cursorOwner = stage;
                    captured = true;
                }
                Filter(ref input->CursorInputs);
                Filter(ref input->UIFilteredCursorInputs);
                MouseDevice.ScheduleCursorMove(centerX, centerY);
                stage->AtkCursor.SetVisible(false);
            }
            else Release();
        }
        catch (Exception ex) { Fail(ex); }

        var result = inputHook!.Original(module, input, padMouse);
        try
        {
            nativeTextInput = module != null && module->IsTextInputActive();
            var stage = AtkStage.Instance();
            var manager = InputManager.Instance();
            if (CaptureUsable && stage != null && stage == cursorOwner && manager != null)
            {
                drag.Apply(manager, centerX, centerY, dx, dy);
                stage->AtkCursor.SetVisible(false);
            }
            else Release();
        }
        catch (Exception ex) { Fail(ex); }
        return result;
    }

    private void Filter(ref CursorInputData input)
    {
        const MouseButtonFlags clicks = MouseButtonFlags.LBUTTON | MouseButtonFlags.RBUTTON;
        input.MouseButtonHeldFlags &= ~clicks;
        input.MouseButtonPressedFlags &= ~clicks;
        input.MouseButtonReleasedFlags &= ~clicks;
        input.MouseButtonHeldThrottledFlags &= ~clicks;
        input.PositionX = centerX;
        input.PositionY = centerY;
        input.DeltaX = dx;
        input.DeltaY = dy;
    }

    private CameraInputSource GetCameraSource()
    {
        try
        {
            var manager = InputManager.Instance();
            if (CaptureUsable && manager != null)
            {
                drag.Apply(manager, centerX, centerY, dx, dy);
                return CameraInputSource.MouseDrag;
            }
            Release();
        }
        catch (Exception ex) { Fail(ex); }
        return cameraHook!.Original();
    }

    private void UpdateLens(Camera* camera)
    {
        try
        {
            if (camera != null && camera == ActiveThirdPersonCamera())
            {
                AdvanceFraming(camera);
                lens.Apply(camera, ShoulderFraming.Magnification(aim, aimMagnification));
            }
            else lens.Restore(camera);
        }
        catch (Exception ex) { Fail(ex); }
        lensHook!.Original(camera);
    }

    private void AdvanceFraming(Camera* camera)
    {
        long now = Stopwatch.GetTimestamp();
        if (lensCamera != camera || lastFrame == 0)
        {
            lensCamera = camera;
            lastFrame = now;
            blendedShoulder = shoulderSide;
            aim = 0f;
            smoothAim = default;
        }
        float seconds = (float)Stopwatch.GetElapsedTime(lastFrame, now).TotalSeconds;
        lastFrame = now;
        float weight = 1f - MathF.Exp(-16f * seconds);
        blendedShoulder += (shoulderSide - blendedShoulder) * weight;
        bool held = CaptureUsable && MouseLookKeys.Down(0x02);
        aim = ShoulderFraming.Style == ZoomStyle.Dolly
            ? ShoulderFraming.AdvanceAim(aim, held, seconds)
            : smoothAim.Advance(held, seconds);
    }

    private void UpdateRender(SceneCamera* scene)
    {
        var active = ActiveThirdPersonCamera();
        if (active == null || scene != &active->SceneCamera || (scene->Flags & 1) == 0 || framingView)
        {
            viewHook!.Original(scene);
            return;
        }
        if (ShoulderFraming.Style == ZoomStyle.Dolly) AdvanceFraming(active);
        framedCamera = scene;
        var magnification = ShoulderFraming.Magnification(aim, aimMagnification);
        var elevation = ShoulderFraming.AimElevation(objects.LocalPlayer?.CustomizeData.Race ?? 0, lens.UnzoomedFieldOfView(active));
        framedOffset = ShoulderFraming.Offsets(scene->Position, scene->LookAtVector, blendedShoulder,
            magnification, ShoulderFraming.Style, elevation);
        var view = new ShoulderViewScope(scene, framedOffset);
        framingView = true;
        try { viewHook!.Original(scene); }
        finally
        {
            view.Restore(scene);
            framingView = false;
        }
    }

    private GameObject* ResolvePlaceholder(PronounModule* module, CStringPointer token, byte mode, byte flags, bool extra)
    {
        try
        {
            if (captured && !Paused && IsMouseover((byte*)token))
            {
                var camera = ActiveThirdPersonCamera();
                if (camera != null) return CenterTarget(camera);
            }
        }
        catch (Exception ex) { Fail(ex); }
        return macroHook!.Original(module, token, mode, flags, extra);
    }

    private GameObject* CenterTarget(Camera* camera)
    {
        var targets = TargetSystem.Instance();
        var stage = AtkStage.Instance();
        if (stage == null || stage != cursorOwner || targets == null ||
            stage->ScreenSize.Width <= 0 || stage->ScreenSize.Height <= 0) return null;
        var filter = targets->ObjectFilterArray1.Length > 0 ? &targets->ObjectFilterArray1 : &targets->TargetableObjectsOnScreen;
        if (filter->Length is <= 0 or > 819) return null;
        var scene = &camera->SceneCamera;
        // The native render call already built the view matrix. Provide the
        // same camera origin to native picking and restore it on return.
        var view = new ShoulderViewScope(scene, !framingView && scene == framedCamera ? framedOffset : default);
        try
        {
            var target = targets->GetMouseOverObject(stage->ScreenSize.Width / 2, stage->ScreenSize.Height / 2, filter, camera);
            return target != null && target->GetIsTargetable() && objects.LocalPlayer?.Address != (nint)target ? target : null;
        }
        finally { view.Restore(scene); }
    }

    private static bool UsesExternalTarget(ActionRow action) =>
        action.CanTargetHostile || action.CanTargetParty || action.CanTargetAlliance ||
        action.CanTargetAlly || action.CanTargetOwnPet || action.CanTargetPartyPet;

    private bool UseAction(ActionManager* manager, ActionType type, uint action, ulong target, uint extra,
        ActionManager.UseActionMode mode, uint combo, bool* area)
    {
        // A queued request already has its target. Explicit macros still resolve their own placeholders.
        if (captured && !Paused && manager != null &&
            type is ActionType.Action or ActionType.PvPAction &&
            mode is ActionManager.UseActionMode.None or ActionManager.UseActionMode.Combo)
        {
            try
            {
                var camera = ActiveThirdPersonCamera();
                if (camera != null)
                {
                    uint spell = type == ActionType.Action ? action : ActionManager.GetSpellIdForAction(type, action);
                    uint adjusted = spell == 0 ? 0 : manager->GetAdjustedActionId(spell);
                    if (adjusted != 0 && actions.GetRowOrDefault(adjusted) is { } row &&
                        (row.TargetArea || UsesExternalTarget(row)))
                    {
                        var picked = CenterTarget(camera);
                        ulong id = picked == null ? 0 : picked->GetGameObjectId();
                        if (id is not (0 or 0xE0000000))
                        {
                            // Ground actions use the object's position as the placement point.
                            if (!row.TargetArea && !ActionManager.CanUseActionOnTarget(adjusted, picked)) return false;
                            target = id;
                        }
                        else if (!row.TargetArea) return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
                return false;
            }
        }
        return actionHook!.Original(manager, type, action, target, extra, mode, combo, area);
    }

    private static bool IsMouseover(byte* text)
    {
        if (text == null) return false;
        bool bracketed = *text == '<';
        if (bracketed) text++;
        if ((*text | 32) != 'm' || (text[1] | 32) != 'o') return false;
        return bracketed ? text[2] == '>' && text[3] == 0 : text[2] == 0;
    }

    private void Draw()
    {
        platformTextInput = ImGui.GetIO().WantTextInput;
        if (!CaptureUsable) return;
        var viewport = ImGui.GetMainViewport();
        var center = viewport.Pos + viewport.Size * .5f;
        var list = ImGui.GetForegroundDrawList();
        list.AddCircleFilled(center, 3.5f, 0xD0000000);
        list.AddCircleFilled(center, 2f, 0xFFFFFFFF);
    }

    private void Update(IFramework _)
    {
        if (captured && (!CaptureUsable || AtkStage.Instance() != cursorOwner)) Release();
        if (framedCamera != null && ActiveThirdPersonCamera() == null) ResetView();
    }

    private void ResetView()
    {
        if (ShoulderFraming.Style != ZoomStyle.Dolly)
        {
            var control = Control.Instance();
            lens.Restore(control == null ? null : control->CameraManager.Camera);
        }
        lensCamera = null;
        framedCamera = null;
        framedOffset = default;
        lastFrame = 0;
        aim = 0;
        smoothAim = default;
    }

    private void Release()
    {
        state.Suspend();
        drag.Restore();
        if (!captured) return;
        captured = false;
        var stage = AtkStage.Instance();
        if (stage != null && stage == cursorOwner)
        {
            if (restoreVisible) stage->AtkCursor.SetVisible(true);
            if (MouseLookKeys.Foreground)
                MouseDevice.ScheduleCursorMove(Math.Clamp(restoreX, 0, Math.Max(0, stage->ScreenSize.Width - 1)),
                    Math.Clamp(restoreY, 0, Math.Max(0, stage->ScreenSize.Height - 1)));
        }
        cursorOwner = null;
        dx = dy = 0;
    }

    private void Fail(Exception ex)
    {
        requested = false;
        state.Reset();
        Release();
        ResetView();
        log.Error(ex, "Third-person action camera released the pointer after an error.");
    }

    private void DisposeHooks()
    {
        inputHook?.Dispose();
        mouseMessageHook?.Dispose();
        actionHook?.Dispose();
        viewHook?.Dispose();
        lensHook?.Dispose();
        cameraHook?.Dispose();
        macroHook?.Dispose();
        inputHook = null;
        mouseMessageHook = null;
        actionHook = null;
        viewHook = null;
        lensHook = null;
        ResetView();
        cameraHook = null;
        macroHook = null;
    }

    public void Dispose()
    {
        framework.Update -= Update;
        pluginInterface.UiBuilder.Draw -= Draw;
        if (commandRegistered) commands.RemoveHandler(Command);
        if (legacyCommandRegistered) commands.RemoveHandler(LegacyCommand);
        requested = false;
        Release();
        DisposeHooks();
    }
}
