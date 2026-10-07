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

    private Entity CameraEntity = Entity.Null;
    private float Yaw = 40.0f;
    private float Pitch = -18.0f;
    private bool bLooking;
    private float FlySpeed = 30.0f;
    private EWreckTool Tool = EWreckTool.Cannon;
    private float Power = 1.0f;
    private float FireCooldown;
    private readonly List<(Entity Body, double Expires)> Projectiles = new();
    private readonly List<(FVector3 Point, double Time, float Radius, float Strength)> PendingBlasts = new();

    private void SetupCamera()
    {
        float Span = Math.Max(BlocksX, BlocksZ) * (BlockSize + StreetWidth);
        FVector3 Start = new(-Span * 0.55f, Span * 0.22f, -Span * 0.55f);
        CameraEntity = World.CreateEntity("WreckerCamera", Start);
        SCameraComponent? Camera = Registry.GetOrAdd<SCameraComponent>(CameraEntity);
        if (Camera is not null)
        {
            Camera.FOV = 75.0f;
            Camera.NearPlane = 0.1f;
            Camera.FarPlane = 4000.0f;
        }
        CCameraLibrary.SetActiveCamera(World, CameraEntity, 0.0f, ECameraBlendFunction.Linear);
        CInputLibrary.SetInputMode(World, EInputMode.Game);
        CInputLibrary.SetMouseMode(World, EMouseMode.Normal);
        ApplyCameraRotation();
    }

    private FQuat CameraRotation => FQuat.FromEuler(0.0f, Mathf.Radians(Yaw), 0.0f) * FQuat.FromEuler(Mathf.Radians(-Pitch), 0.0f, 0.0f);

    private void ApplyCameraRotation()
    {
        World.SetEntityRotation(CameraEntity, CameraRotation);
    }

    private void TickCamera(float DeltaTime)
    {
        if (!World.IsValidEntity(CameraEntity) || !CInputLibrary.IsReceivingInput(World))
        {
            return;
        }

        bool bWantsLook = CInputLibrary.IsMouseButtonDown(World, EMouseKey.ButtonRight);
        if (bWantsLook != bLooking)
        {
            bLooking = bWantsLook;
            CInputLibrary.SetMouseMode(World, bLooking ? EMouseMode.Captured : EMouseMode.Normal);
        }

        if (bLooking)
        {
            FVector2 Mouse = CInputLibrary.GetMouseDelta(World);
            Yaw += Mouse.X * LookSensitivity;
            Pitch = Math.Clamp(Pitch - Mouse.Y * LookSensitivity, -89.0f, 89.0f);
            ApplyCameraRotation();
        }

        float Wheel = CInputLibrary.GetMouseWheel(World);
        if (Wheel != 0.0f)
        {
            FlySpeed = Math.Clamp(FlySpeed * (Wheel > 0.0f ? 1.25f : 0.8f), 2.0f, 600.0f);
        }

        float Forward = Axis(EKey.W, EKey.S);
        float Right = Axis(EKey.D, EKey.A);
        float Up = Axis(EKey.E, EKey.Q);
        if (Forward == 0.0f && Right == 0.0f && Up == 0.0f)
        {
            return;
        }

        float Speed = FlySpeed;
        if (CInputLibrary.IsKeyDown(World, EKey.LeftShift))
        {
            Speed *= 4.0f;
        }
        if (CInputLibrary.IsKeyDown(World, EKey.LeftControl))
        {
            Speed *= 0.25f;
        }

        FVector3 Move = CCameraLibrary.GetViewForward(World) * Forward + CCameraLibrary.GetViewRight(World) * Right + FVector3.Up * Up;
        World.SetEntityLocation(CameraEntity, World.GetEntityLocation(CameraEntity) + Move.NormalizedOr(FVector3.Zero) * Speed * DeltaTime);
    }

    private float Axis(EKey Positive, EKey Negative)
    {
        return (CInputLibrary.IsKeyDown(World, Positive) ? 1.0f : 0.0f) - (CInputLibrary.IsKeyDown(World, Negative) ? 1.0f : 0.0f);
    }

    private void TickTools(float DeltaTime)
    {
        PruneProjectiles();
        DetonateDueBlasts();
        FireCooldown -= DeltaTime;

        if (!CInputLibrary.IsReceivingInput(World))
        {
            return;
        }

        if (CInputLibrary.WasKeyPressed(World, EKey.D1)) { Tool = EWreckTool.Blast; }
        if (CInputLibrary.WasKeyPressed(World, EKey.D2)) { Tool = EWreckTool.Cannon; }
        if (CInputLibrary.WasKeyPressed(World, EKey.D3)) { Tool = EWreckTool.Fracture; }
        if (CInputLibrary.WasKeyPressed(World, EKey.D4)) { Tool = EWreckTool.Meteor; }
        if (CInputLibrary.WasKeyPressed(World, EKey.RightBracket)) { Power = Math.Min(Power * 1.5f, 20.0f); }
        if (CInputLibrary.WasKeyPressed(World, EKey.LeftBracket)) { Power = Math.Max(Power / 1.5f, 0.1f); }

        if (CInputLibrary.WasKeyPressed(World, EKey.R))
        {
            DestroyCity();
            BuildCity();
            return;
        }
        if (CInputLibrary.WasKeyPressed(World, EKey.G))
        {
            Earthquake();
        }

        FWorldRay Aim = AimRay();
        if (!Aim.bValid)
        {
            return;
        }

        if (CInputLibrary.WasKeyPressed(World, EKey.B) && TraceAim(Aim, out SRayResult RainHit))
        {
            BombRain(RainHit.Location);
        }

        bool bPressed = CInputLibrary.WasMouseButtonPressed(World, EMouseKey.ButtonLeft);
        bool bHeld = CInputLibrary.IsMouseButtonDown(World, EMouseKey.ButtonLeft);
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
                Shatter(new Entity(FractureHit.Entity), FractureHit.Location);
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
    private FWorldRay AimRay()
    {
        return bLooking
            ? CCameraLibrary.ViewportCenterRay(World)
            : CCameraLibrary.ScreenToWorldRay(World, CInputLibrary.GetMousePosition(World));
    }

    private bool TraceAim(FWorldRay Aim, out SRayResult Hit)
    {
        Hit = CPhysicsLibrary.Raycast(World, Aim.Origin, Aim.Origin + Aim.Direction * ReachMeters, Entity.Null, ECollisionProfiles.All);
        return Hit.bHit;
    }

    private void FireCannon(FWorldRay Aim)
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
        Entity Shot = World.CreateEntity("Projectile", From, null, new FVector3(Diameter));
        SStaticMeshComponent? Mesh = Registry.GetOrAdd<SStaticMeshComponent>(Shot);
        if (Mesh is not null)
        {
            Mesh.StaticMesh = Ball;
            if (BoxMaterial is not null)
            {
                Mesh.SetMaterialAtSlot(BoxMaterial, 0);
            }
            Mesh.CustomPrimitiveData.SetAsColor(new FVector4(0.9f, 0.25f, 0.1f, 1.0f));
        }

        SRigidBodyComponent? RigidBody = Registry.GetOrAdd<SRigidBodyComponent>(Shot);
        if (RigidBody is not null)
        {
            RigidBody.bOverrideMass = true;
            RigidBody.Mass = Mass;
            RigidBody.bUseContinuousCollision = true;
            RigidBody.RestitutionOverride = 0.1f;
            RigidBody.FrictionOverride = 0.6f;
        }
        Registry.GetOrAdd<SSphereColliderComponent>(Shot);

        CPhysicsLibrary.SetLinearVelocity(World, Shot, Velocity);
        World.SetLifetime(Shot, ProjectileLifetime);
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
            SRayResult Hit = CPhysicsLibrary.Raycast(World, Position, Next, Entity.Null, ECollisionProfiles.All);
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
        CDebugDrawLibrary.DrawSphere(World, Center, Radius, new FVector4(1.0f, 0.5f, 0.1f, 1.0f), 2.0f, 0.4f);

        Entity[] Caught = CPhysicsLibrary.OverlapSphere(World, Center, Radius, Entity.Null);
        foreach (Entity Body in Caught)
        {
            FVector3 Offset = CPhysicsLibrary.GetBodyPosition(World, Body) - Center;
            float Distance = Offset.Length;
            float Falloff = Math.Clamp(1.0f - Distance / Radius, 0.0f, 1.0f);
            if (Falloff <= 0.0f)
            {
                continue;
            }

            FVector3 Direction = (Offset.NormalizedOr(FVector3.Up) + new FVector3(0.0f, 0.5f, 0.0f)).Normalized();
            float Mass = CPhysicsLibrary.GetMass(World, Body);
            CPhysicsLibrary.AddImpulse(World, Body, Direction * (Mass * 22.0f * Strength * Falloff));
        }
    }

    private void Shatter(Entity Target, FVector3 HitPoint)
    {
        if (!World.IsValidEntity(Target) || !DynamicBodies.Contains(Target))
        {
            Blast(HitPoint, 4.0f, Power);
            return;
        }

        SDestructibleComponent? Destructible = Registry.GetOrAdd<SDestructibleComponent>(Target);
        if (Destructible is not null)
        {
            Destructible.FragmentCount = 10;
            Destructible.ExplosionStrength = 6.0f * Power;
            Destructible.FragmentLifetime = 20.0f;
        }

        if (CDestructionLibrary.FractureEntity(World, Target, HitPoint, 8.0f * Power))
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
        foreach (Entity Body in DynamicBodies)
        {
            if (!World.IsValidEntity(Body))
            {
                continue;
            }
            FVector3 Kick = new((float)(Shake.NextDouble() * 2.0 - 1.0), (float)Shake.NextDouble() * 0.6f, (float)(Shake.NextDouble() * 2.0 - 1.0));
            CPhysicsLibrary.AddImpulse(World, Body, Kick * (CPhysicsLibrary.GetMass(World, Body) * 2.5f * Power));
        }
    }
}
