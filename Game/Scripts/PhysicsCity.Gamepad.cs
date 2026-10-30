using System;
using LuminaSharp;
using Lumina;

namespace GameScripts;

public sealed partial class PhysicsCity
{
    private const float PadLookDegreesPerSecond = 150.0f;

    private bool UsingGamepad => World.Input.IsGamepadConnected() && World.Input.GetLastUsedDevice() == EInputDevice.Gamepad;

    private bool PadPressed(EGamepadButton Button) => World.Input.WasGamepadButtonPressed(Button);

    private bool PadDown(EGamepadButton Button) => World.Input.IsGamepadButtonDown(Button);

    private void TickPadLook(float DeltaTime)
    {
        FVector2 Stick = World.Input.GetGamepadRightStick();
        if (Stick.X == 0.0f && Stick.Y == 0.0f)
        {
            return;
        }

        float Rate = PadLookDegreesPerSecond * DeltaTime;
        Yaw += Stick.X * MathF.Abs(Stick.X) * Rate;
        Pitch = Math.Clamp(Pitch + Stick.Y * MathF.Abs(Stick.Y) * Rate, -89.0f, 89.0f);
        ApplyCameraRotation();
    }

    private FVector3 PadMove()
    {
        FVector2 Stick = World.Input.GetGamepadLeftStick();
        float Up = (PadDown(EGamepadButton.RightShoulder) ? 1.0f : 0.0f) - (PadDown(EGamepadButton.LeftShoulder) ? 1.0f : 0.0f);
        return new FVector3(Stick.Y, Stick.X, Up);
    }

    private float PadSpeedScale()
    {
        float Scale = 1.0f + 3.0f * World.Input.GetGamepadAxis(EGamepadAxis.LeftTrigger);
        return PadDown(EGamepadButton.LeftThumb) ? Scale * 0.25f : Scale;
    }

    private void CyclePadTool()
    {
        int Count = Enum.GetValues<EWreckTool>().Length;
        if (PadPressed(EGamepadButton.DPadRight))
        {
            Tool = (EWreckTool)(((int)Tool + 1) % Count);
        }
        if (PadPressed(EGamepadButton.DPadLeft))
        {
            Tool = (EWreckTool)(((int)Tool + Count - 1) % Count);
        }
        if (PadPressed(EGamepadButton.DPadUp))
        {
            Power = Math.Min(Power * 1.5f, 20.0f);
        }
        if (PadPressed(EGamepadButton.DPadDown))
        {
            Power = Math.Max(Power / 1.5f, 0.1f);
        }
    }

    private void DrawCrosshair()
    {
        SWorldRay Aim = World.Camera.ViewportCenterRay();
        if (Aim.bValid)
        {
            World.DebugDraw.DrawPoint(Aim.Origin + Aim.Direction * 3.0f, 0.03f, new FVector4(1.0f, 1.0f, 1.0f, 1.0f), 1.0f, -1.0f, false);
        }
    }
}
