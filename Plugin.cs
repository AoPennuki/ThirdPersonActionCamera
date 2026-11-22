using System.Numerics;
using System.Diagnostics;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
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
    private const string ChineseCommand = "/动作模式";
    private const string CommandHelp = "/acti on 开启，/acti off 关闭，/acti config 设置。";
    private const string ChineseCommandHelp = "/动作模式 开、/动作模式 关、/动作模式 设置";
    private const int ReleaseKey = MouseLookKeys.DefaultKey;
    private const int LeftMouseButton = 0x01;
    private const int RightMouseButton = 0x02;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ActionCameraConfiguration configuration;
    private readonly IFramework framework;
    private readonly IClientState client;
    private readonly IGameGui gameGui;
    private readonly IObjectTable objects;
    private readonly ICondition conditions;
    private readonly ICommandManager commands;
    private readonly IChatGui chat;
    private readonly IGameInteropProvider interop;
    private readonly IPluginLog log;
    private readonly bool commandRegistered, legacyCommandRegistered, chineseCommandRegistered;
    private readonly ExcelSheet<ActionRow> actions;
    private readonly MouseLookState state = new();
    private readonly AltCursorHold altCursorHold = new();
    private readonly MouseLookDragPatch drag = new();
    private readonly LensZoom lens = new();
    private readonly MinionCamera minionCamera = new();
    private bool minionCameraFailed, reportedMinionCamera, followingMinion;
    private Hook<AtkModule.Delegates.HandleInput>? inputHook;
    private Hook<Camera.Delegates.GetCameraInputSource>? cameraHook;
    private Hook<PronounModule.Delegates.ResolvePlaceholder>? macroHook;
    private Hook<ActionManager.Delegates.UseAction>? actionHook;
    private Hook<ActionManager.Delegates.GetGroundPositionForCursor>? groundHook;
    private Hook<SceneCamera.Delegates.UpdateRender>? viewHook;
    private Hook<Camera.Delegates.Update>? lensHook;
    private AtkStage* cursorOwner;
    private bool requested, captured, platformTextInput, nativeTextInput;
    private bool settingsOpen;
    private int settingsRace = ShoulderFraming.LalafellRace;
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
    private bool reportedGroundReticle;
    private bool reportedHiddenUiReticle;
    private long lastSelectedTargetUse;
    private Vector2 reticle = new(.5f, .5f);
    private QueuedSelection? queuedSelection;

    private readonly record struct QueuedSelection(ActionType Type, uint Action, ulong Target);

    private static QueuedSelection? ReadQueue(ActionManager* manager) =>
        manager != null && manager->ActionQueued
            ? new QueuedSelection(manager->QueuedActionType, manager->QueuedActionId, manager->QueuedTargetId) : null;

    public Plugin(IDalamudPluginInterface pluginInterface, IFramework framework, IClientState client,
        IObjectTable objects, ICondition conditions, ICommandManager commands, IChatGui chat,
        IGameInteropProvider interop, IPluginLog log, IDataManager data, IGameGui gameGui)
    {
        this.pluginInterface = pluginInterface;
        configuration = pluginInterface.GetPluginConfig() as ActionCameraConfiguration ?? new ActionCameraConfiguration();
        configuration.Normalize();
        this.framework = framework;
        this.client = client;
        this.gameGui = gameGui;
        this.objects = objects;
        this.conditions = conditions;
        this.commands = commands;
        this.chat = chat;
        this.interop = interop;
        this.log = log;
        actions = data.GetExcelSheet<ActionRow>();
        commandRegistered = commands.AddHandler(Command, new CommandInfo(OnCommand) { HelpMessage = CommandHelp });
        legacyCommandRegistered = commands.AddHandler(LegacyCommand, new CommandInfo(OnCommand) { ShowInHelp = false });
        chineseCommandRegistered = commands.AddHandler(ChineseCommand, new CommandInfo(OnCommand) { HelpMessage = ChineseCommandHelp });
        if (!commandRegistered) log.Warning("Could not register /acti; the existing /actioncam alias is still available if registered.");
        if (!chineseCommandRegistered) log.Warning("Could not register /动作模式.");
        framework.Update += Update;
        pluginInterface.UiBuilder.DisableUserUiHide = true;
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleSettings;
        pluginInterface.UiBuilder.OpenMainUi += ToggleSettings;
    }

    private void OnCommand(string command, string args)
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
            case "开":
                try
                {
                    if (inputHook == null) InstallHooks();
                    requested = true;
                    chat.Print(command == ChineseCommand ? "动作镜头已开启。/动作模式 关" : "动作镜头已开启。/acti off 关闭。");
                }
                catch (Exception ex)
                {
                    DisposeHooks();
                    Fail(ex);
                    chat.PrintError("第三人称动作镜头初始化失败，详情见 Dalamud 日志。");
                }
                break;
            case "off":
            case "关":
                requested = false;
                state.Reset();
                Release();
                DisposeHooks();
                ShowNativeCursor();
                chat.Print("第三人称动作镜头已关闭。");
                break;
            case "status":
                chat.Print(captured ? "第三人称动作镜头正在捕获鼠标。" : requested ? "第三人称动作镜头已启用，当前鼠标自由。" : "第三人称动作镜头已关闭。");
                break;
            case "config":
            case "settings":
            case "设置":
                settingsOpen = true;
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
                chat.Print(command == ChineseCommand ? ChineseCommandHelp : CommandHelp);
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

        inputHook = interop.HookFromAddress<AtkModule.Delegates.HandleInput>(
            (nint)AtkModule.MemberFunctionPointers.HandleInput, HandleInput);
        cameraHook = interop.HookFromAddress<Camera.Delegates.GetCameraInputSource>(
            (nint)Camera.MemberFunctionPointers.GetCameraInputSource, GetCameraSource);
        macroHook = interop.HookFromAddress<PronounModule.Delegates.ResolvePlaceholder>(
            (nint)PronounModule.MemberFunctionPointers.ResolvePlaceholder, ResolvePlaceholder);
        actionHook = interop.HookFromAddress<ActionManager.Delegates.UseAction>(
            (nint)ActionManager.MemberFunctionPointers.UseAction, UseAction);
        if (ActionManager.MemberFunctionPointers.GetGroundPositionForCursor != null)
            groundHook = interop.HookFromAddress<ActionManager.Delegates.GetGroundPositionForCursor>(
                (nint)ActionManager.MemberFunctionPointers.GetGroundPositionForCursor, GroundPositionForCursor);
        viewHook = interop.HookFromAddress<SceneCamera.Delegates.UpdateRender>(
            (nint)scene->VirtualTable->UpdateRender, UpdateRender);
        if (ShoulderFraming.Style != ZoomStyle.Dolly)
            lensHook = interop.HookFromAddress<Camera.Delegates.Update>(
                (nint)camera->VirtualTable->Update, UpdateLens);
        macroHook.Enable();
        actionHook.Enable();
        groundHook?.Enable();
        viewHook.Enable();
        lensHook?.Enable();
        cameraHook.Enable();
        inputHook.Enable();
    }

    private Camera* ActiveCamera()
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

    private bool UiInputPaused => nativeTextInput || platformTextInput || settingsOpen;
    private bool Paused => UiInputPaused || altCursorHold.Active || CursorReleaseHeld;
    private bool CaptureUsable => captured && !Paused && ActiveCamera() != null;
    private static bool CursorReleaseHeld => MouseLookKeys.Down(ReleaseKey) && MouseLookKeys.Modifiers == 0;
    private static bool MouseForwardHeld => MouseLookKeys.Down(LeftMouseButton) && MouseLookKeys.Down(RightMouseButton);

    private byte HandleInput(AtkModule* module, UIInputData* input, bool padMouse)
    {
        drag.Restore();
        try
        {
            nativeTextInput = module != null && module->IsTextInputActive();
            var stage = AtkStage.Instance();
            var valid = input != null && stage != null && stage->ScreenSize.Width > 0 && stage->ScreenSize.Height > 0;
            var camera = ActiveCamera();
            var eligible = valid && !padMouse && input->CursorInputs.IsGameWindowFocused && camera != null;
            const MouseButtonFlags captureButtons = MouseButtonFlags.LBUTTON | MouseButtonFlags.RBUTTON;
            var held = valid && ((input->CursorInputs.MouseButtonHeldFlags | input->CursorInputs.MouseButtonPressedFlags) & captureButtons) != 0;
            bool paused = UiInputPaused;
            bool altDown = MouseLookKeys.Down(MouseLookKeys.Alt);
            bool altEligible = eligible && !paused;
            bool altRelease = altCursorHold.Update(Stopwatch.GetTimestamp(), altDown,
                altDown && altEligible && MouseLookKeys.OtherKeyboardKeyDown(), altEligible);
            if (state.Update(eligible, paused, altRelease || CursorReleaseHeld, false, ReleaseKey, held))
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
                    cursorOwner = stage;
                    captured = true;
                }
                var target = ReticlePixels(stage);
                Filter(ref input->CursorInputs, target.X, target.Y);
                Filter(ref input->UIFilteredCursorInputs, target.X, target.Y);
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
                ApplyCameraDrag(manager);
                stage->AtkCursor.SetVisible(false);
            }
            else Release();
        }
        catch (Exception ex) { Fail(ex); }
        return result;
    }

    private (int X, int Y) ReticlePixels(AtkStage* stage) =>
        ((int)(stage->ScreenSize.Width * reticle.X), (int)(stage->ScreenSize.Height * reticle.Y));

    private void Filter(ref CursorInputData input, int targetX, int targetY)
    {
        const MouseButtonFlags clicks = MouseButtonFlags.LBUTTON | MouseButtonFlags.RBUTTON;
        input.MouseButtonHeldFlags &= ~clicks;
        input.MouseButtonPressedFlags &= ~clicks;
        input.MouseButtonReleasedFlags &= ~clicks;
        input.MouseButtonHeldThrottledFlags &= ~clicks;
        input.PositionX = targetX;
        input.PositionY = targetY;
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
                ApplyCameraDrag(manager);
                return CameraInputSource.MouseDrag;
            }
            Release();
        }
        catch (Exception ex) { Fail(ex); }
        return cameraHook!.Original();
    }

    private void ApplyCameraDrag(InputManager* manager) =>
        drag.Apply(manager, centerX, centerY, dx, dy, moveForward: MouseForwardHeld);

    private void UpdateLens(Camera* camera)
    {
        try
        {
            if (camera != null && camera == ActiveCamera())
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
        bool held = configuration.RightClickAim && CaptureUsable && MouseLookKeys.Down(RightMouseButton);
        aim = ShoulderFraming.Style == ZoomStyle.Dolly
            ? ShoulderFraming.AdvanceAim(aim, held, seconds)
            : smoothAim.Advance(held, seconds);
    }

    private void UpdateRender(SceneCamera* scene)
    {
        var active = ActiveCamera();
        if (active == null || scene != &active->SceneCamera || (scene->Flags & 1) == 0 || framingView)
        {
            viewHook!.Original(scene);
            return;
        }
        if (ShoulderFraming.Style == ZoomStyle.Dolly) AdvanceFraming(active);
        framedCamera = scene;
        var magnification = ShoulderFraming.Magnification(aim, aimMagnification);
        var player = objects.LocalPlayer;
        byte race = player?.CustomizeData.Race ?? 0;
        var job = configuration.ForJob(player?.ClassJob.RowId ?? 0);
        var elevation = ShoulderFraming.AimElevation(configuration.PitchFor(race) + job.PitchDegrees);
        framedOffset = ShoulderFraming.Offsets(scene->Position, scene->LookAtVector, blendedShoulder,
            magnification, ShoulderFraming.Style, elevation, job.ShoulderCm);
        var motion = SampleMinionCamera(active, framedOffset);
        framedOffset = (framedOffset.Position + motion, framedOffset.LookAt + motion);
        var view = new ShoulderViewScope(scene, framedOffset);
        framingView = true;
        try
        {
            viewHook!.Original(scene);
            float headY = player is not null && race == ShoulderFraming.LalafellRace
                ? ProjectHeadY(scene, player) : float.NaN;
            reticle = configuration.ReticlePosition(race, headY, job);
        }
        finally
        {
            view.Restore(scene);
            framingView = false;
        }
    }

    private Vector3 SampleMinionCamera(Camera* camera, (Vector3 Position, Vector3 LookAt) framing)
    {
        if (!configuration.DynamicCameraEnabled || configuration.ShoulderMotionStrength <= 0f ||
            minionCameraFailed)
        {
            minionCamera.Reset();
            followingMinion = false;
            return default;
        }
        var sample = minionCamera.Height;
        followingMinion = sample.HasValue;
        Vector3 nativePosition = camera->SceneCamera.Position;
        Vector3 nativePivot = camera->SceneCamera.LookAtVector;
        long now = Stopwatch.GetTimestamp();
        float height = minionCamera.Follow(nativePivot.Y, sample, configuration.ShoulderMotionStrength,
            configuration.ShoulderMotionSmoothing, now);
        float nativeDistance = Vector3.Distance(nativePosition, nativePivot);
        float compression = (camera->Distance - nativeDistance) / Math.Max(.25f, camera->Distance * .12f);
        height = minionCamera.LimitByCollision(nativePosition + framing.Position, nativePivot + framing.LookAt, compression, height, now);
        if (followingMinion && !reportedMinionCamera && height > .001f)
        {
            reportedMinionCamera = true;
            log.Information("Action camera applied its first dynamic camera height adjustment.");
        }
        return height * Vector3.UnitY;
    }

    private static float ProjectHeadY(SceneCamera* scene, IGameObject player)
    {
        var actor = (GameObject*)player.Address;
        var device = Device.Instance();
        if (actor == null || actor->VirtualTable == null || actor->VirtualTable->GetHeight == null || scene->RenderCamera == null ||
            device == null || device->Height == 0) return float.NaN;

        float height = actor->GetHeight();
        if (!float.IsFinite(height) || height <= 0f) return float.NaN;
        var head = player.Position + Vector3.UnitY * height;
        return scene->WorldToScreen(head, out var screen) ? screen.Y / device->Height : float.NaN;
    }

    private GameObject* ResolvePlaceholder(PronounModule* module, CStringPointer token, byte mode, byte flags, bool extra)
    {
        try
        {
            if (captured && !Paused && IsMouseover((byte*)token))
            {
                var camera = ActiveCamera();
                if (camera != null)
                {
                    var picked = ReticleTarget(camera);
                    return ActionTargets.Id(picked) != 0 ? picked : ActionTargets.Selected();
                }
            }
        }
        catch (Exception ex) { Fail(ex); }
        return macroHook!.Original(module, token, mode, flags, extra);
    }

    private GameObject* ReticleTarget(Camera* camera)
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
            var pixel = ReticlePixels(stage);
            var target = targets->GetMouseOverObject(pixel.X, pixel.Y, filter, camera);
            return target != null && target->GetIsTargetable() && objects.LocalPlayer?.Address != (nint)target ? target : null;
        }
        finally { view.Restore(scene); }
    }

    private static bool UsesExternalTarget(ActionRow action) =>
        action.CanTargetHostile || action.CanTargetParty || action.CanTargetAlliance ||
        action.CanTargetAlly || action.CanTargetOwnPet || action.CanTargetPartyPet;

    private bool GroundPositionForCursor(ActionManager* manager, Vector3* position)
    {
        if (!configuration.GroundAtReticle || position == null || !CaptureUsable)
            return groundHook!.Original(manager, position);

        var camera = ActiveCamera();
        var stage = AtkStage.Instance();
        var input = UIInputData.Instance();
        if (camera == null || stage == null || stage != cursorOwner || input == null ||
            stage->ScreenSize.Width <= 0 || stage->ScreenSize.Height <= 0)
            return groundHook!.Original(manager, position);

        var pixel = ReticlePixels(stage);
        var original = (input->CursorInputs.PositionX, input->CursorInputs.PositionY);
        var filtered = (input->UIFilteredCursorInputs.PositionX, input->UIFilteredCursorInputs.PositionY);
        var scene = &camera->SceneCamera;
        var view = new ShoulderViewScope(scene, !framingView && scene == framedCamera ? framedOffset : default);
        try
        {
            input->CursorInputs.PositionX = input->UIFilteredCursorInputs.PositionX = pixel.X;
            input->CursorInputs.PositionY = input->UIFilteredCursorInputs.PositionY = pixel.Y;
            bool hit = groundHook!.Original(manager, position);
            if (hit && manager != null && manager->AreaTargetingExecuteAtCursor && !reportedGroundReticle)
            {
                reportedGroundReticle = true;
                log.Information("Action camera resolved its first ground-targeted position at the reticle.");
            }
            return hit;
        }
        finally
        {
            (input->CursorInputs.PositionX, input->CursorInputs.PositionY) = original;
            (input->UIFilteredCursorInputs.PositionX, input->UIFilteredCursorInputs.PositionY) = filtered;
            view.Restore(scene);
        }
    }

    private bool UseAction(ActionManager* manager, ActionType type, uint action, ulong target, uint extra,
        ActionManager.UseActionMode mode, uint combo, bool* area)
    {
        bool nativeInput = mode is ActionManager.UseActionMode.None or ActionManager.UseActionMode.Combo;
        bool selectAfterUse = false;
        bool fromSelection = false;
        bool placeAtReticle = false;
        uint resolvedAction = 0;
        if (captured && !Paused && manager != null &&
            type is ActionType.Action or ActionType.PvPAction &&
            (nativeInput || mode == ActionManager.UseActionMode.Macro))
        {
            try
            {
                var camera = ActiveCamera();
                if (camera != null)
                {
                    uint spell = type == ActionType.Action ? action : ActionManager.GetSpellIdForAction(type, action);
                    uint adjusted = spell == 0 ? 0 : manager->GetAdjustedActionId(spell);
                    resolvedAction = adjusted;
                    if (adjusted != 0 && actions.GetRowOrDefault(adjusted) is { } row &&
                        (row.TargetArea || UsesExternalTarget(row)))
                    {
                        placeAtReticle = nativeInput && row.TargetArea && configuration.GroundAtReticle && groundHook != null;
                        if (row.TargetArea)
                        {
                            if (placeAtReticle) target = 0xE0000000;
                        }
                        else
                        {
                            var picked = ReticleTarget(camera);
                            if (nativeInput)
                            {
                                var player = (GameObject*)(objects.LocalPlayer?.Address ?? 0);
                                target = ActionTargets.ForSkill(adjusted, player, picked, ActionTargets.Selected(), out fromSelection);
                                if (target == 0) return false;
                                selectAfterUse = configuration.SelectAfterAction && !fromSelection;
                            }
                            else
                            {
                                // Explicit macro targets have already been resolved by the game's pronoun system.
                                ulong id = ActionTargets.Id(picked);
                                selectAfterUse = configuration.SelectAfterAction && id != 0 && id == target;
                                fromSelection = id == 0 && target != 0 && ActionTargets.Id(ActionTargets.Selected()) == target;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
                return false;
            }
        }
        if (placeAtReticle)
        {
            bool areaStarted = false;
            bool result = actionHook!.Original(manager, type, action, target, extra, mode, combo, &areaStarted);
            if (area != null) *area = areaStarted;
            if (areaStarted) manager->AreaTargetingExecuteAtCursor = true;
            return result;
        }
        bool watchQueue = nativeInput && (selectAfterUse || fromSelection);
        var previous = watchQueue ? ReadQueue(manager) : null;
        bool used = actionHook!.Original(manager, type, action, target, extra, mode, combo, area);
        var queued = watchQueue ? ReadQueue(manager) : null;
        bool acceptedIntoQueue = queued is { } entry && entry.Target == target &&
            (entry.Action == action || entry.Action == resolvedAction) && (used || previous != queued);
        if ((used || acceptedIntoQueue) && fromSelection)
            lastSelectedTargetUse = Stopwatch.GetTimestamp();
        if (!configuration.SelectAfterAction) return used;
        if (mode == ActionManager.UseActionMode.Queue)
        {
            bool select = queuedSelection == new QueuedSelection(type, action, target);
            queuedSelection = null;
            if (select && used && CaptureUsable) SelectTarget(target);
        }
        else if (acceptedIntoQueue && CaptureUsable)
        {
            queuedSelection = selectAfterUse ? queued : null;
        }
        else if (used && selectAfterUse && CaptureUsable)
            SelectTarget(target);
        return used;
    }

    private void SelectTarget(ulong id)
    {
        if (TargetSystem.MemberFunctionPointers.SetHardTarget == null) return;
        try
        {
            var targets = TargetSystem.Instance();
            var found = objects.SearchById(id);
            var actor = found == null ? null : (GameObject*)found.Address;
            if (targets != null && actor != null && targets->Target != actor && actor->GetIsTargetable())
                targets->SetHardTarget(actor);
        }
        catch (Exception error)
        {
            log.Error(error, "Action camera could not select the target after skill execution.");
        }
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
        bool hudHidden = gameGui.GameUiHidden;
        bool showSettings = settingsOpen && !hudHidden;
        if (showSettings) DrawSettings();
        if (!configuration.ShowReticle) return;
        if (!CaptureUsable && !(showSettings && ActiveCamera() != null)) return;
        var viewport = ImGui.GetMainViewport();
        var position = viewport.Pos + viewport.Size * reticle;
        var list = ImGui.GetForegroundDrawList();
        list.AddCircleFilled(position, 3.5f, 0xD0000000);
        list.AddCircleFilled(position, 2f, 0xFFFFFFFF);
        if (lastSelectedTargetUse != 0 && Stopwatch.GetElapsedTime(lastSelectedTargetUse).TotalSeconds < .35)
            list.AddCircle(position, 6f, 0xFFD8BB77, 16, 1.3f);
        if (hudHidden && !reportedHiddenUiReticle)
        {
            reportedHiddenUiReticle = true;
            log.Information("Action camera drew the reticle with the HUD hidden and the pointer captured.");
        }
    }

    private void ToggleSettings() => settingsOpen = !settingsOpen;

    private void DrawSettings()
    {
        ImGui.SetNextWindowSize(new Vector2(460f, 440f), ImGuiCond.FirstUseEver);
        bool visible = ImGui.Begin("动作镜头设置###ThirdPersonActionCamera", ref settingsOpen, ImGuiWindowFlags.None);
        if (visible)
        {
            bool showReticle = configuration.ShowReticle;
            if (ImGui.Checkbox("显示准星", ref showReticle))
            {
                configuration.ShowReticle = showReticle;
                configuration.Save(pluginInterface);
            }
            bool rightClickAim = configuration.RightClickAim;
            if (ImGui.Checkbox("按住右键瞄准", ref rightClickAim))
            {
                configuration.RightClickAim = rightClickAim;
                configuration.Save(pluginInterface);
            }
            bool selectAfterAction = configuration.SelectAfterAction;
            if (ImGui.Checkbox("释放技能后选中准星目标", ref selectAfterAction))
            {
                configuration.SelectAfterAction = selectAfterAction;
                queuedSelection = null;
                configuration.Save(pluginInterface);
            }
            ImGui.TextUnformatted(FormattableString.Invariant(
                $"单独按住 Alt {AltCursorHold.HoldSeconds:0.0} 秒或按住 {MouseLookKeys.Name(ReleaseKey)} 临时释放鼠标"));

            bool groundAtReticle = configuration.GroundAtReticle;
            if (ImGui.Checkbox("地面技能在准星落点施放", ref groundAtReticle))
            {
                configuration.GroundAtReticle = groundAtReticle;
                configuration.Save(pluginInterface);
            }
            bool dynamicCameraEnabled = configuration.DynamicCameraEnabled;
            bool saveDynamicCamera = ImGui.Checkbox("第三人称动态镜头", ref dynamicCameraEnabled);
            configuration.DynamicCameraEnabled = dynamicCameraEnabled;
            if (dynamicCameraEnabled)
            {
                configuration.ShoulderMotionStrength = SliderWithReset("跟随强度", configuration.ShoulderMotionStrength,
                    0f, 1f, ActionCameraConfiguration.DefaultShoulderMotionStrength, "%.0f%%", ref saveDynamicCamera,
                    displayScale: 100f);
                configuration.ShoulderMotionSmoothing = SliderWithReset("镜头稳定器", configuration.ShoulderMotionSmoothing,
                    0f, 1f, ActionCameraConfiguration.DefaultShoulderMotionSmoothing, "%.0f%%", ref saveDynamicCamera,
                    displayScale: 100f);
                ImGui.TextUnformatted(minionCameraFailed ? "动态镜头取样不可用。" :
                    followingMinion ? $"低处平滑跟随，离地高度变化超过 {CameraHeightSmoothing.HeightThresholdCm:0} cm 后加快响应。"
                    : "切到第三人称以使用动态镜头。");
            }
            if (saveDynamicCamera) configuration.Save(pluginInterface);

            ImGui.Separator();
            ImGui.TextUnformatted("种族镜头微调");
            ImGui.SetNextItemWidth(280f);
            ImGui.Combo("种族", ref settingsRace, ActionCameraConfiguration.Races);
            settingsRace = Math.Clamp(settingsRace, 1, ActionCameraConfiguration.Races.Count - 1);

            float defaultReticleOffset = settingsRace == ShoulderFraming.LalafellRace
                ? ActionCameraConfiguration.DefaultLalafellReticleOffset : 0f;
            bool saveRace = false;
            configuration.RacePitchDegrees[settingsRace] = SliderWithReset("镜头抬高",
                configuration.RacePitchDegrees[settingsRace], -8f, 8f, 0f, "%+.1f°", ref saveRace);
            configuration.RaceReticleOffset[settingsRace] = SliderWithReset("准星上移",
                configuration.RaceReticleOffset[settingsRace], -.25f, .25f, defaultReticleOffset, "%+.1f%%",
                ref saveRace, displayScale: 100f);
            configuration.RaceReticleHorizontal[settingsRace] = SliderWithReset("准星左右",
                configuration.RaceReticleHorizontal[settingsRace], -.25f, .25f, 0f, "%+.1f%%",
                ref saveRace, displayScale: 100f);

            if (ImGui.Button("重置当前种族"))
            {
                configuration.RacePitchDegrees[settingsRace] = 0f;
                configuration.RaceReticleHorizontal[settingsRace] = 0f;
                configuration.RaceReticleOffset[settingsRace] = defaultReticleOffset;
                saveRace = true;
            }
            if (saveRace) configuration.Save(pluginInterface);

            if (ImGui.CollapsingHeader("当前职业镜头微调") && objects.LocalPlayer?.ClassJob.ValueNullable is { } job)
            {
                ImGui.PushID("JobFraming");
                ImGui.TextUnformatted(job.Name.ExtractText());
                var framing = configuration.ForJob(job.RowId);
                bool save = false;
                float shoulder = SliderWithReset("镜头左右", framing.ShoulderCm, -60f, 60f, 0f, "%+.0f cm", ref save);
                float pitch = SliderWithReset("镜头抬高", framing.PitchDegrees, -8f, 8f, 0f, "%+.1f°", ref save);
                float right = SliderWithReset("准星左右", framing.ReticleRight, -.25f, .25f, 0f, "%+.1f%%", ref save, 100f);
                float up = SliderWithReset("准星上移", framing.ReticleUp, -.25f, .25f, 0f, "%+.1f%%", ref save, 100f);
                framing = new(shoulder, pitch, right, up);
                if (ImGui.Button("重置当前职业"))
                {
                    framing = default;
                    save = true;
                }
                if (save)
                {
                    configuration.SetJob(job.RowId, framing);
                    configuration.Save(pluginInterface);
                }
                ImGui.PopID();
            }
        }
        ImGui.End();
    }

    private static float SliderWithReset(string label, float value, float minimum, float maximum,
        float defaultValue, string format, ref bool save, float displayScale = 1f)
    {
        const string reset = "恢复默认";
        var style = ImGui.GetStyle();
        float resetWidth = ImGui.CalcTextSize(reset).X + 2f * style.FramePadding.X;
        ImGui.PushID(label);
        ImGui.TextUnformatted(label);
        ImGui.SameLine(8f * ImGui.GetFontSize());
        ImGui.SetNextItemWidth(Math.Max(65f, ImGui.GetContentRegionAvail().X - resetWidth - style.ItemSpacing.X));
        float displayValue = value * displayScale;
        if (ImGui.SliderFloat("##value", ref displayValue, minimum * displayScale, maximum * displayScale,
            format, ImGuiSliderFlags.AlwaysClamp))
            value = displayValue / displayScale;
        save |= ImGui.IsItemDeactivatedAfterEdit();
        ImGui.SameLine();
        if (ImGui.Button(reset))
        {
            value = defaultValue;
            save = true;
        }
        ImGui.PopID();
        return value;
    }

    private void Update(IFramework _)
    {
        var camera = ActiveCamera();
        if (camera == null || UiInputPaused) altCursorHold.Cancel();
        try
        {
            if (camera != null && configuration.DynamicCameraEnabled && configuration.ShoulderMotionStrength > 0f &&
                !minionCameraFailed && objects.LocalPlayer is { } player)
                minionCamera.Update((GameObject*)player.Address, player.CustomizeData.Race == ShoulderFraming.LalafellRace);
            else
                minionCamera.Dispose();
        }
        catch (Exception error)
        {
            minionCameraFailed = true;
            minionCamera.Dispose();
            log.Error(error, "Third-person dynamic camera stopped.");
        }
        if (captured && (!CaptureUsable || AtkStage.Instance() != cursorOwner)) Release();
        if (framedCamera != null && ActiveCamera() == null) ResetView();
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
        minionCamera.Reset();
        followingMinion = false;
        reticle = new Vector2(.5f, .5f);
        lastFrame = 0;
        aim = 0;
        smoothAim = default;
    }

    private void Release()
    {
        state.Suspend();
        drag.Restore();
        queuedSelection = null;
        lastSelectedTargetUse = 0;
        if (!captured) return;
        captured = false;
        var stage = AtkStage.Instance();
        if (stage != null && stage == cursorOwner)
        {
            stage->AtkCursor.SetVisible(true);
            if (MouseLookKeys.Foreground)
                MouseDevice.ScheduleCursorMove(Math.Clamp(restoreX, 0, Math.Max(0, stage->ScreenSize.Width - 1)),
                    Math.Clamp(restoreY, 0, Math.Max(0, stage->ScreenSize.Height - 1)));
        }
        cursorOwner = null;
        dx = dy = 0;
    }

    private void ShowNativeCursor()
    {
        if (!MouseLookKeys.Foreground || !client.IsLoggedIn ||
            AtkCursor.MemberFunctionPointers.SetVisible == null) return;
        var stage = AtkStage.Instance();
        if (stage != null) stage->AtkCursor.SetVisible(true);
    }

    private void Fail(Exception ex)
    {
        requested = false;
        state.Reset();
        altCursorHold.Cancel();
        Release();
        ResetView();
        log.Error(ex, "Third-person action camera released the pointer after an error.");
    }

    private void DisposeHooks()
    {
        altCursorHold.Cancel();
        minionCameraFailed = false;
        reportedMinionCamera = false;
        minionCamera.Dispose();
        inputHook?.Dispose();
        actionHook?.Dispose();
        groundHook?.Dispose();
        viewHook?.Dispose();
        lensHook?.Dispose();
        cameraHook?.Dispose();
        macroHook?.Dispose();
        inputHook = null;
        actionHook = null;
        groundHook = null;
        reportedGroundReticle = false;
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
        pluginInterface.UiBuilder.OpenConfigUi -= ToggleSettings;
        pluginInterface.UiBuilder.OpenMainUi -= ToggleSettings;
        if (commandRegistered) commands.RemoveHandler(Command);
        if (legacyCommandRegistered) commands.RemoveHandler(LegacyCommand);
        if (chineseCommandRegistered) commands.RemoveHandler(ChineseCommand);
        bool restoreCursor = requested || captured;
        requested = false;
        Release();
        DisposeHooks();
        if (restoreCursor) ShowNativeCursor();
    }
}
