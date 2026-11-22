using System.Numerics;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ThirdPersonActionCamera;

[Serializable]
internal sealed class ActionCameraConfiguration : IPluginConfiguration
{
    private static readonly string[] RaceNames =
    [
        "未设置",
        "人族",
        "精灵族",
        "拉拉菲尔族",
        "猫魅族",
        "鲁加族",
        "敖龙族",
        "硌狮族",
        "维埃拉族",
    ];

    public int Version { get; set; } = 1;
    public float[] RacePitchDegrees { get; set; } = new float[9];
    public float[] RaceReticleOffset { get; set; } = CreateDefaultReticleOffsets();
    public float[] RaceReticleHorizontal { get; set; } = new float[9];
    public Dictionary<uint, JobFraming> JobAdjustments { get; set; } = new();
    [JsonExtensionData] private IDictionary<string, JToken>? OtherSettings { get; set; }
    public bool ShowReticle { get; set; } = true;
    public bool RightClickAim { get; set; } = true;
    public bool SelectAfterAction { get; set; }
    public bool GroundAtReticle { get; set; } = true;
    public bool DynamicCameraEnabled { get; set; }
    // Keep the saved strength and stabilization settings from earlier test builds.
    public float ShoulderMotionStrength { get; set; } = DefaultShoulderMotionStrength;
    public float ShoulderMotionSmoothing { get; set; } = DefaultShoulderMotionSmoothing;

    internal static IReadOnlyList<string> Races => RaceNames;

    internal const float DefaultLalafellReticleOffset = .07f;
    internal const float DefaultShoulderMotionStrength = .4f;
    internal const float DefaultShoulderMotionSmoothing = .65f;

    internal void Normalize()
    {
        ShoulderMotionStrength = ClampFinite(ShoulderMotionStrength, 0f, 1f, DefaultShoulderMotionStrength);
        ShoulderMotionSmoothing = ClampFinite(ShoulderMotionSmoothing, 0f, 1f, DefaultShoulderMotionSmoothing);
        RacePitchDegrees = NormalizeValues(RacePitchDegrees, -8f, 8f);
        RaceReticleOffset = NormalizeValues(RaceReticleOffset, -.25f, .25f,
            ShoulderFraming.LalafellRace, DefaultLalafellReticleOffset);
        RaceReticleHorizontal = NormalizeValues(RaceReticleHorizontal, -.25f, .25f);
        JobAdjustments ??= new();
        foreach (var (id, framing) in JobAdjustments.ToArray())
            SetJob(id, framing);
    }

    internal float PitchFor(byte race) =>
        race < RacePitchDegrees.Length ? RacePitchDegrees[race] : 0f;

    internal JobFraming ForJob(uint id) => JobAdjustments.GetValueOrDefault(id);

    internal void SetJob(uint id, JobFraming framing)
    {
        framing = new(ClampFinite(framing.ShoulderCm, -60f, 60f, 0f), ClampFinite(framing.PitchDegrees, -8f, 8f, 0f),
            ClampFinite(framing.ReticleRight, -.25f, .25f, 0f), ClampFinite(framing.ReticleUp, -.25f, .25f, 0f));
        if (id == 0 || framing == default) JobAdjustments.Remove(id);
        else JobAdjustments[id] = framing;
    }

    internal Vector2 ReticlePosition(byte race, float headY, JobFraming job)
    {
        float right = job.ReticleRight + (race < RaceReticleHorizontal.Length ? RaceReticleHorizontal[race] : 0f);
        float offset = job.ReticleUp + (race < RaceReticleOffset.Length ? RaceReticleOffset[race] : 0f);
        float position = .5f - offset;
        if (race == ShoulderFraming.LalafellRace && offset > 0f && float.IsFinite(headY))
            position = Math.Min(position, headY - .025f);
        return new(Math.Clamp(.5f + right, .08f, .92f), Math.Clamp(position, .08f, .92f));
    }

    internal void Save(IDalamudPluginInterface pluginInterface)
    {
        Normalize();
        pluginInterface.SavePluginConfig(this);
    }

    private static float[] CreateDefaultReticleOffsets()
    {
        var offsets = new float[9];
        offsets[ShoulderFraming.LalafellRace] = DefaultLalafellReticleOffset;
        return offsets;
    }

    private static float[] NormalizeValues(float[]? values, float minimum, float maximum,
        int defaultIndex = -1, float defaultValue = 0f)
    {
        var result = new float[9];
        if (defaultIndex >= 0 && defaultIndex < result.Length)
            result[defaultIndex] = defaultValue;
        if (values != null)
            for (var index = 0; index < Math.Min(values.Length, result.Length); index++)
                result[index] = ClampFinite(values[index], minimum, maximum, 0f);
        return result;
    }

    private static float ClampFinite(float value, float minimum, float maximum, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

internal readonly record struct JobFraming(float ShoulderCm, float PitchDegrees, float ReticleRight, float ReticleUp);
