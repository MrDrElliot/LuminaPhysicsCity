using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using LuminaSharp;
using Lumina;

namespace GameScripts;

// A city of sleeping box bodies plus a flying wrecker camera, for stress testing physics at scale.
[Reflect]
public sealed partial class PhysicsCity : CEntityScript
{
    [Property(ToolTip = "City blocks along X.")]
    public int BlocksX = 8;

    [Property(ToolTip = "City blocks along Z.")]
    public int BlocksZ = 8;

    [Property(ToolTip = "Side length of one block in meters, streets excluded.")]
    public float BlockSize = 40.0f;

    [Property(ToolTip = "Width of the streets between blocks in meters.")]
    public float StreetWidth = 14.0f;

    [Property(ToolTip = "Edge length of one building box in meters.")]
    public float CellSize = 2.0f;

    [Property(ToolTip = "Floors on the shortest building.")]
    public int MinFloors = 5;

    [Property(ToolTip = "Floors on the tallest ordinary building. The landmark tower in the middle doubles it.")]
    public int MaxFloors = 22;

    [Property(ToolTip = "Dynamic bodies the city stops at, so a large grid cannot exceed the physics world's capacity.")]
    public int BodyBudget = 60000;

    [Property(ToolTip = "Seed for the layout, so the same city comes back on every rebuild.")]
    public int Seed = 1337;

    [Property(ToolTip = "Material whose base color reads the custom primitive data color.")]
    public string BoxMaterialPath = "/Game/Content/M_CityBox";

    private readonly List<FEntity> CityEntities = new();
    private readonly List<FEntity> DynamicBodies = new();
    private int AwakeSampleCursor;
    private int AwakeInSample;
    private int SampledSoFar;
    private int AwakeEstimate;
    private double BuildMs;
    private float SmoothedFrameMs;
    private long LastFrameTicks;

    // The editor toolbar's time constant, so this readout and the toolbar agree.
    private const float FrameTimeSmoothingSeconds = 0.2f;

    public override void OnReady()
    {
        BuildCity();
        SetupCamera();
    }

    public override void OnUpdate(float DeltaTime)
    {
        SmoothFrameTime();
        TickCamera(DeltaTime);
        TickTools(DeltaTime);
        SampleAwakeBodies();
        DrawHud();
    }

    // Wall-clock frame time weighted by each frame's own length, as the editor toolbar does, so time dilation and uneven frames do not skew it.
    private void SmoothFrameTime()
    {
        long Now = Stopwatch.GetTimestamp();
        if (LastFrameTicks == 0)
        {
            LastFrameTicks = Now;
            return;
        }

        float FrameSeconds = (float)(Now - LastFrameTicks) / Stopwatch.Frequency;
        LastFrameTicks = Now;
        float FrameMs = FrameSeconds * 1000.0f;
        float Weight = 1.0f - MathF.Exp(-FrameSeconds / FrameTimeSmoothingSeconds);
        SmoothedFrameMs = SmoothedFrameMs <= 0.0f ? FrameMs : SmoothedFrameMs + (FrameMs - SmoothedFrameMs) * Weight;
    }

    // A slice of the bodies each frame, so the awake count costs a few hundred interop calls rather than the whole city at once.
    private void SampleAwakeBodies()
    {
        const int SamplesPerFrame = 1500;
        int Count = DynamicBodies.Count;
        if (Count == 0)
        {
            AwakeEstimate = 0;
            return;
        }

        int Steps = Math.Min(SamplesPerFrame, Count);
        for (int Step = 0; Step < Steps; ++Step)
        {
            if (AwakeSampleCursor >= Count)
            {
                AwakeEstimate = SampledSoFar > 0 ? (int)((long)AwakeInSample * Count / SampledSoFar) : 0;
                AwakeSampleCursor = 0;
                AwakeInSample = 0;
                SampledSoFar = 0;
            }

            FEntity Body = DynamicBodies[AwakeSampleCursor++];
            if (World.IsValidEntity(Body) && World.Physics.IsAwake(Body))
            {
                ++AwakeInSample;
            }
            ++SampledSoFar;
        }
    }

    private void DrawHud()
    {
        FVector4 White = new(1.0f, 1.0f, 1.0f, 1.0f);
        FVector4 Dim = new(0.75f, 0.8f, 0.85f, 1.0f);
        FVector4 Accent = new(1.0f, 0.8f, 0.3f, 1.0f);

        World.DebugDraw.DrawText($"Physics City  {1000.0f / Math.Max(SmoothedFrameMs, 0.01f):F0} fps  {SmoothedFrameMs:F2} ms", White);
        World.DebugDraw.DrawText($"Bodies {DynamicBodies.Count + Projectiles.Count:N0}  awake ~{AwakeEstimate:N0}  projectiles {Projectiles.Count:N0}  built in {BuildMs:F0} ms", White);
        World.DebugDraw.DrawText($"Tool [{(int)Tool + 1}] {Tool}  power {Power:F1}x  fly speed {FlySpeed:F0} m/s", Accent);
        if (World.Input.IsGamepadConnected())
        {
            FVector2 Left = World.Input.GetGamepadLeftStick();
            FVector2 Right = World.Input.GetGamepadRightStick();
            World.DebugDraw.DrawText($"Pad  last used {World.Input.GetLastUsedDevice()}  L ({Left.X:F2}, {Left.Y:F2})  R ({Right.X:F2}, {Right.Y:F2})  L2 {World.Input.GetGamepadAxis(EGamepadAxis.LeftTrigger):F2}  R2 {World.Input.GetGamepadAxis(EGamepadAxis.RightTrigger):F2}", Dim);
        }
        else
        {
            World.DebugDraw.DrawText("Pad  not connected", Dim);
        }

        if (UsingGamepad)
        {
            DrawCrosshair();
            World.DebugDraw.DrawText("L stick fly  R stick look  L1/R1 down/up  L2 fast  L3 slow  R2 use tool", Dim);
            World.DebugDraw.DrawText("D-pad left/right tool  up/down power  A bomb rain  X earthquake  View rebuild  Menu quit", Dim);
            return;
        }
        World.DebugDraw.DrawText("RMB look  WASD fly  Q/E down/up  Shift fast  wheel speed  LMB use tool  1-4 tools  [ ] power", Dim);
        World.DebugDraw.DrawText("B bomb rain  G earthquake  R rebuild city  Esc quit", Dim);
    }
}
