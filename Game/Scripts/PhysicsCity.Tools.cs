using System;
using System.Collections.Generic;
using LuminaSharp;
using Lumina;

namespace GameScripts;

public enum EWreckTool
{
    Blast,
    Cannon,
    Fracture,
    Meteor,
}

public sealed partial class PhysicsCity
{
    private const float LookSensitivity = 0.12f;
    private const float ReachMeters = 3000.0f;
    private const float ProjectileLifetime = 25.0f;

    private FEntity CameraEntity = FEntity.Null;
    private float Yaw = 40.0f;
    private float Pitch = -18.0f;
    private bool bLooking;
    private float FlySpeed = 30.0f;
    private EWreckTool Tool = EWreckTool.Cannon;
    private float Power = 1.0f;
    private float FireCooldown;
    private readonly List<(FEntity Body, double Expires)> Projectiles = new();
    private readonly List<(FVector3 Point, double Time, float Radius, float Strength)> PendingBlasts = new();

    private void SetupCamera()
    {
        float Span = Math.Max(BlocksX, BlocksZ) * (BlockSize + StreetWidth);
        FVector3 Start = new(-Span * 0.55f, Span * 0.22f, -Span * 0.55f);
        CameraEntity = World.CreateEntity("WreckerCamera", Start);
        ref SCameraComponent Camera = ref World.GetOrEmplaceComponent<SCameraComponent>(CameraEntity);
        Camera.FOV = 75.0f;
        Camera.NearPlane = 0.1f;
        Camera.FarPlane = 4000.0f;
        World.Camera.SetActiveCamera(CameraEntity, 0.0f, ECameraBlendFunction.Linear);
        World.Input.SetInputMode(EInputMode.Game);
        World.Input.SetMouseMode(EMouseMode.Normal);
        ApplyCameraRotation();
    }

    private FQuat CameraRotation => FQuat.FromEuler(0.0f, Mathf.Radians(Yaw), 0.0f) * FQuat.FromEuler(Mathf.Radians(-Pitch), 0.0f, 0.0f);

    private void ApplyCameraRotation()
    {
        World.SetEntityRotation(CameraEntity, CameraRotation);
    }

    private void TickCamera(float DeltaTime)
    {
        if (!World.IsValidEntity(CameraEntity) || !World.Input.IsReceivingInput())
        {
            return;
        }

        bool bWantsLook = World.Input.IsMouseButtonDown(EMouseKey.ButtonRight);
        if (bWantsLook != bLooking)
        {
            bLooking = bWantsLook;
            World.Input.SetMouseMode(bLooking ? EMouseMode.Captured : EMouseMode.Normal);
        }

        if (bLooking)
        {
            FVector2 Mouse = World.Input.GetMouseDelta();
            Yaw += Mouse.X * LookSensitivity;
            Pitch = Math.Clamp(Pitch - Mouse.Y * LookSensitivity, -89.0f, 89.0f);
            ApplyCameraRotation();
        }

        TickPadLook(DeltaTime);

        float Wheel = World.Input.GetMouseWheel();
        if (Wheel != 0.0f)
        {
            FlySpeed = Math.Clamp(FlySpeed * (Wheel > 0.0f ? 1.25f : 0.8f), 2.0f, 600.0f);
        }

        FVector3 Pad = PadMove();
        float Forward = Math.Clamp(Axis(EKey.W, EKey.S) + Pad.X, -1.0f, 1.0f);
        float Right = Math.Clamp(Axis(EKey.D, EKey.A) + Pad.Y, -1.0f, 1.0f);
        float Up = Math.Clamp(Axis(EKey.E, EKey.Q) + Pad.Z, -1.0f, 1.0f);
        if (Forward == 0.0f && Right == 0.0f && Up == 0.0f)
        {
            return;
        }

        float Speed = FlySpeed * PadSpeedScale();
        if (World.Input.IsKeyDown(EKey.LeftShift))
        {
            Speed *= 4.0f;
        }
        if (World.Input.IsKeyDown(EKey.LeftControl))
        {
            Speed *= 0.25f;
        }

        FVector3 Move = World.Camera.GetViewForward() * Forward + World.Camera.GetViewRight() * Right + FVector3.Up * Up;
        float Length = Move.Length;
        if (Length > 1.0f)
        {
            Move = Move * (1.0f / Length);
        }
        World.SetEntityLocation(CameraEntity, World.GetEntityLocation(CameraEntity) + Move * Speed * DeltaTime);
    }

    private float Axis(EKey Positive, EKey Negative)
    {
        return (World.Input.IsKeyDown(Positive) ? 1.0f : 0.0f) - (World.Input.IsKeyDown(Negative) ? 1.0f : 0.0f);
    }

    private void TickTools(float DeltaTime)
    {
        PruneProjectiles();
        DetonateDueBlasts();
        FireCooldown -= DeltaTime;

        if (!World.Input.IsReceivingInput())
        {
            return;
        }

        if (World.Input.WasKeyPressed(EKey.Escape) || PadPressed(EGamepadButton.Start))
        {
            Engine.Quit();
            return;
        }

        if (World.Input.WasKeyPressed(EKey.D1)) { Tool = EWreckTool.Blast; }
        if (World.Input.WasKeyPressed(EKey.D2)) { Tool = EWreckTool.Cannon; }
        if (World.Input.WasKeyPressed(EKey.D3)) { Tool = EWreckTool.Fracture; }
        if (World.Input.WasKeyPressed(EKey.D4)) { Tool = EWreckTool.Meteor; }
        if (World.Input.WasKeyPressed(EKey.RightBracket)) { Power = Math.Min(Power * 1.5f, 20.0f); }
        if (World.Input.WasKeyPressed(EKey.LeftBracket)) { Power = Math.Max(Power / 1.5f, 0.1f); }
        CyclePadTool();

        if (World.Input.WasKeyPressed(EKey.R) || PadPressed(EGamepadButton.Back))
        {
            DestroyCity();
            BuildCity();
            return;
        }
        if (World.Input.WasKeyPressed(EKey.G) || PadPressed(EGamepadButton.FaceLeft))
        {
            Earthquake();
        }

        SWorldRay Aim = AimRay();
        if (!Aim.bValid)
        {
            return;
        }

        if ((World.Input.WasKeyPressed(EKey.B) || PadPressed(EGamepadButton.FaceBottom)) && TraceAim(Aim, out SRayResult RainHit))
        {
            BombRain(RainHit.Location);
        }

        bool bPressed = World.Input.WasMouseButtonPressed(EMouseKey.ButtonLeft) || PadPressed(EGamepadButton.RightTrigger);
        bool bHeld = World.Input.IsMouseButtonDown(EMouseKey.ButtonLeft) || PadDown(EGamepadButton.RightTrigger);
        switch (Tool)
        {
        case EWreckTool.Cannon:
            if (bHeld && FireCooldown <= 0.0f)
            {
                FireCooldown = 0.08f;
                FireCannon(Aim);
            }
            break;
        case EWreckTool.Blast:
            if (bPressed && TraceAim(Aim, out SRayResult BlastHit))
            {
                Blast(BlastHit.Location, 9.0f * MathF.Sqrt(Power), Power);
            }
            break;
        case EWreckTool.Fracture:
            if (bPressed && TraceAim(Aim, out SRayResult FractureHit))
            {
                Shatter(new FEntity(FractureHit.Entity), FractureHit.Location);
            }
            break;
        case EWreckTool.Meteor:
            if (bPressed && TraceAim(Aim, out SRayResult MeteorHit))
            {
                DropMeteor(MeteorHit.Location);
            }
            break;
        }
    }

    // While looking the cursor is captured, so aim goes through the screen center like a crosshair.
    private SWorldRay AimRay()
    {
        return bLooking || UsingGamepad
            ? World.Camera.ViewportCenterRay()
            : World.Camera.ScreenToWorldRay(World.Input.GetMousePosition());
    }

    private bool TraceAim(SWorldRay Aim, out SRayResult Hit)
    {
        Hit = World.Physics.Raycast(Aim.Origin, Aim.Origin + Aim.Direction * ReachMeters, FEntity.Null, ECollisionProfiles.All);
        return Hit.bHit;
    }

    private void FireCannon(SWorldRay Aim)
    {
        FVector3 Muzzle = Aim.Origin + Aim.Direction * 2.0f;
        float Scale = MathF.Sqrt(Power);
        Launch(Muzzle, Aim.Direction * (140.0f * Scale), 1.4f, 4000.0f * Power, 6.0f * Scale, 1.5f * Power);
    }

    private void DropMeteor(FVector3 Target)
    {
        FVector3 Sky = Target + new FVector3(30.0f, 180.0f, 20.0f);
        FVector3 Down = (Target - Sky).Normalized();
        float Scale = MathF.Sqrt(Power);
        Launch(Sky, Down * 90.0f, 7.0f * Scale, 300000.0f * Power, 22.0f * Scale, 3.0f * Power);
    }

    private void BombRain(FVector3 Center)
    {
        const int Bombs = 80;
        for (int Index = 0; Index < Bombs; ++Index)
        {
            float Angle = Index * 2.39996f;
            float Radius = 3.0f * MathF.Sqrt(Index);
            FVector3 Start = Center + new FVector3(MathF.Cos(Angle) * Radius, 120.0f + Index * 1.5f, MathF.Sin(Angle) * Radius);
            Launch(Start, new FVector3(0.0f, -40.0f, 0.0f), 1.5f, 3000.0f * Power, 7.0f * MathF.Sqrt(Power), 1.5f * Power);
        }
    }

    // A heavy continuous-collision ball, so fast shots cannot tunnel through a one-box wall, that explodes where its arc first meets something.
    private void Launch(FVector3 From, FVector3 Velocity, float Diameter, float Mass, float BlastRadius, float BlastStrength)
    {
        FEntity Shot = World.CreateEntity("Projectile", From, null, new FVector3(Diameter));
        ref SStaticMeshComponent Mesh = ref World.GetOrEmplaceComponent<SStaticMeshComponent>(Shot);
        Mesh.StaticMesh = Ball;
        if (BoxMaterial is not null)
        {
            Mesh.SetMaterialAtSlot(BoxMaterial, 0);
        }
        Mesh.CustomPrimitiveData.SetAsColor(new FVector4(0.9f, 0.25f, 0.1f, 1.0f));

        ref SRigidBodyComponent RigidBody = ref World.GetOrEmplaceComponent<SRigidBodyComponent>(Shot);
        RigidBody.bOverrideMass = true;
        RigidBody.Mass = Mass;
        RigidBody.bUseContinuousCollision = true;
        RigidBody.RestitutionOverride = 0.1f;
        RigidBody.FrictionOverride = 0.6f;
        World.GetOrEmplaceComponent<SSphereColliderComponent>(Shot);

        World.Physics.SetLinearVelocity(Shot, Velocity);
        World.GetOrEmplaceComponent<SLifetimeComponent>(Shot).Lifetime = ProjectileLifetime;
        Projectiles.Add((Shot, World.ElapsedTime + ProjectileLifetime));

        if (BlastRadius > 0.0f && PredictImpact(From, Velocity, out FVector3 ImpactPoint, out float ImpactTime))
        {
            PendingBlasts.Add((ImpactPoint, World.ElapsedTime + ImpactTime, BlastRadius, BlastStrength));
        }
    }

    // Walks the ballistic arc in short ray segments, which is where the shell will land before anything it hits starts moving.
    private bool PredictImpact(FVector3 From, FVector3 Velocity, out FVector3 Point, out float Time)
    {
        const float Step = 1.0f / 60.0f;
        const float MaxSeconds = 8.0f;
        FVector3 Gravity = new(0.0f, -9.81f, 0.0f);
        FVector3 Position = From;
        for (float Elapsed = 0.0f; Elapsed < MaxSeconds; Elapsed += Step)
        {
            FVector3 Next = Position + Velocity * Step;
            SRayResult Hit = World.Physics.Raycast(Position, Next, FEntity.Null, ECollisionProfiles.All);
            if (Hit.bHit)
            {
                Point = Hit.Location;
                Time = Elapsed + Step * Hit.Fraction;
                return true;
            }
            Position = Next;
            Velocity += Gravity * Step;
        }
        Point = FVector3.Zero;
        Time = 0.0f;
        return false;
    }

    private void DetonateDueBlasts()
    {
        double Now = World.ElapsedTime;
        for (int Index = PendingBlasts.Count - 1; Index >= 0; --Index)
        {
            var Pending = PendingBlasts[Index];
            if (Pending.Time <= Now)
            {
                PendingBlasts.RemoveAt(Index);
                Blast(Pending.Point, Pending.Radius, Pending.Strength);
            }
        }
    }

    private void PruneProjectiles()
    {
        double Now = World.ElapsedTime;
        Projectiles.RemoveAll(Entry => Entry.Expires <= Now || !World.IsValidEntity(Entry.Body));
    }

    // Pushes everything in the radius outward and a little upward, falling off with distance and scaled by mass so light and heavy pieces fly alike.
    private void Blast(FVector3 Center, float Radius, float Strength)
    {
        World.DebugDraw.DrawSphere(Center, Radius, new FVector4(1.0f, 0.5f, 0.1f, 1.0f), 2.0f, 0.4f);

        FEntity[] Caught = World.Physics.OverlapSphere(Center, Radius, FEntity.Null);
        foreach (FEntity Body in Caught)
        {
            FVector3 Offset = World.Physics.GetBodyPosition(Body) - Center;
            float Distance = Offset.Length;
            float Falloff = Math.Clamp(1.0f - Distance / Radius, 0.0f, 1.0f);
            if (Falloff <= 0.0f)
            {
                continue;
            }

            FVector3 Direction = (Offset.NormalizedOr(FVector3.Up) + new FVector3(0.0f, 0.5f, 0.0f)).Normalized();
            float Mass = World.Physics.GetMass(Body);
            World.Physics.AddImpulse(Body, Direction * (Mass * 22.0f * Strength * Falloff));
        }
    }

    private void Shatter(FEntity Target, FVector3 HitPoint)
    {
        if (!World.IsValidEntity(Target) || !DynamicBodies.Contains(Target))
        {
            Blast(HitPoint, 4.0f, Power);
            return;
        }

        ref SDestructibleComponent Destructible = ref World.GetOrEmplaceComponent<SDestructibleComponent>(Target);
        Destructible.FragmentCount = 10;
        Destructible.ExplosionStrength = 6.0f * Power;
        Destructible.FragmentLifetime = 20.0f;

        if (World.FractureEntity(Target, HitPoint, 8.0f * Power))
        {
            DynamicBodies.Remove(Target);
        }

        // Whatever rested on the broken box has to notice it is gone.
        Blast(HitPoint, 3.0f, 0.2f);
    }

    // Shoves every dynamic body at once, which wakes the whole city and is the worst case the solver will see.
    private void Earthquake()
    {
        Random Shake = new(Environment.TickCount);
        foreach (FEntity Body in DynamicBodies)
        {
            if (!World.IsValidEntity(Body))
            {
                continue;
            }
            FVector3 Kick = new((float)(Shake.NextDouble() * 2.0 - 1.0), (float)Shake.NextDouble() * 0.6f, (float)(Shake.NextDouble() * 2.0 - 1.0));
            World.Physics.AddImpulse(Body, Kick * (World.Physics.GetMass(Body) * 2.5f * Power));
        }
    }
}
