using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using LuminaSharp;
using Lumina;

namespace GameScripts;

public sealed partial class PhysicsCity
{
    private const float SidewalkHeight = 0.2f;
    private const float FloorsPerSlab = 4;
    private const float SlabThickness = 0.5f;

    // Light enough that a shot can carry a wall away, where real masonry density makes every box an eight tonne block.
    private const float BuildingDensity = 60.0f;

    // Slightly under a cell, so neighbors start a few millimeters apart instead of interpenetrating.
    private const float BoxFill = 0.995f;

    private static readonly FVector4[] FacadeColors =
    {
        new(0.72f, 0.42f, 0.32f, 1.0f),
        new(0.82f, 0.78f, 0.70f, 1.0f),
        new(0.45f, 0.52f, 0.60f, 1.0f),
        new(0.62f, 0.60f, 0.56f, 1.0f),
        new(0.85f, 0.70f, 0.48f, 1.0f),
        new(0.38f, 0.42f, 0.38f, 1.0f),
        new(0.55f, 0.30f, 0.26f, 1.0f),
        new(0.90f, 0.88f, 0.84f, 1.0f),
    };

    private static readonly FVector4[] CarColors =
    {
        new(0.80f, 0.10f, 0.08f, 1.0f),
        new(0.10f, 0.25f, 0.70f, 1.0f),
        new(0.92f, 0.92f, 0.90f, 1.0f),
        new(0.08f, 0.08f, 0.09f, 1.0f),
        new(0.95f, 0.75f, 0.10f, 1.0f),
        new(0.20f, 0.45f, 0.25f, 1.0f),
    };

    private static readonly FVector4 AsphaltColor = new(0.11f, 0.11f, 0.12f, 1.0f);
    private static readonly FVector4 ConcreteColor = new(0.55f, 0.55f, 0.53f, 1.0f);
    private static readonly FVector4 SlabColor = new(0.30f, 0.30f, 0.32f, 1.0f);
    private static readonly FVector4 CrateColor = new(0.62f, 0.45f, 0.24f, 1.0f);
    private static readonly FVector4 PoleColor = new(0.20f, 0.22f, 0.24f, 1.0f);

    private Random Rng = new(1337);
    private CStaticMesh? Cube;
    private CStaticMesh? Ball;
    private CMaterialInterface? BoxMaterial;
    private bool bBudgetReached;

    private void BuildCity()
    {
        long Start = Stopwatch.GetTimestamp();

        Rng = new Random(Seed);
        Cube = CMeshLibrary.GetPrimitiveMesh(EPrimitiveShape.Cube);
        Ball = CMeshLibrary.GetPrimitiveMesh(EPrimitiveShape.Sphere);
        BoxMaterial = LoadObject<CMaterialInterface>(BoxMaterialPath);
        if (BoxMaterial is null)
        {
            Debug.LogWarning($"PhysicsCity could not load {BoxMaterialPath}; boxes keep the primitive's default material.");
        }
        bBudgetReached = false;

        float Pitch = BlockSize + StreetWidth;
        float Width = BlocksX * Pitch + StreetWidth;
        float Depth = BlocksZ * Pitch + StreetWidth;

        SpawnBody("Ground", new FVector3(0.0f, -0.5f, 0.0f), new FVector3(Width + 400.0f, 1.0f, Depth + 400.0f), AsphaltColor, false);

        for (int Bx = 0; Bx < BlocksX; ++Bx)
        {
            for (int Bz = 0; Bz < BlocksZ; ++Bz)
            {
                FVector3 BlockCenter = new((Bx - (BlocksX - 1) * 0.5f) * Pitch, 0.0f, (Bz - (BlocksZ - 1) * 0.5f) * Pitch);
                SpawnBody("Sidewalk", BlockCenter + new FVector3(0.0f, SidewalkHeight * 0.5f, 0.0f), new FVector3(BlockSize, SidewalkHeight, BlockSize), ConcreteColor, false);

                bool bLandmark = Bx == BlocksX / 2 && Bz == BlocksZ / 2;
                BuildBlock(BlockCenter, bLandmark);
            }
        }

        BuildStreetProps(Pitch, Width, Depth);

        // Sleeping from the start, so the city costs nothing until something hits it; queued until each body exists.
        foreach (FEntity Body in DynamicBodies)
        {
            World.Physics.DeactivateBody(Body);
        }

        BuildMs = Stopwatch.GetElapsedTime(Start).TotalMilliseconds;
        Debug.Log($"PhysicsCity built {DynamicBodies.Count:N0} dynamic and {CityEntities.Count - DynamicBodies.Count:N0} static bodies in {BuildMs:F0} ms{(bBudgetReached ? $", stopped at the {BodyBudget:N0} body budget" : "")}.");
    }

    private void DestroyCity()
    {
        foreach (FEntity Body in CityEntities)
        {
            if (World.IsValidEntity(Body))
            {
                World.DestroyEntity(Body);
            }
        }
        CityEntities.Clear();
        DynamicBodies.Clear();
        AwakeSampleCursor = 0;
        AwakeInSample = 0;
        SampledSoFar = 0;
    }

    private void BuildBlock(FVector3 BlockCenter, bool bLandmark)
    {
        float LotSize = (BlockSize - 4.0f) * 0.5f;
        int MaxCells = Math.Max(3, (int)((LotSize - 2.0f) / CellSize));

        if (bLandmark)
        {
            int Cells = Math.Min(MaxCells * 2, (int)((BlockSize - 6.0f) / CellSize));
            BuildTower(BlockCenter, Cells, Cells, MaxFloors * 2, FacadeColors[7]);
            return;
        }

        for (int Lx = 0; Lx < 2; ++Lx)
        {
            for (int Lz = 0; Lz < 2; ++Lz)
            {
                FVector3 LotCenter = BlockCenter + new FVector3((Lx - 0.5f) * (LotSize + 2.0f), 0.0f, (Lz - 0.5f) * (LotSize + 2.0f));
                double Roll = Rng.NextDouble();
                if (Roll < 0.1)
                {
                    BuildCratePyramid(LotCenter, 7);
                    continue;
                }

                // Taller toward the middle of the city, so the skyline reads as a downtown.
                float FromCenter = new FVector3(LotCenter.X, 0.0f, LotCenter.Z).Length / Math.Max(1.0f, BlocksX * (BlockSize + StreetWidth) * 0.5f);
                float Height01 = Math.Clamp(1.0f - FromCenter + (float)(Rng.NextDouble() * 0.5 - 0.25), 0.0f, 1.0f);
                int Floors = MinFloors + (int)MathF.Round(Height01 * (MaxFloors - MinFloors));
                int CellsX = Rng.Next(3, MaxCells + 1);
                int CellsZ = Rng.Next(3, MaxCells + 1);
                BuildTower(LotCenter, CellsX, CellsZ, Floors, FacadeColors[Rng.Next(FacadeColors.Length)]);
            }
        }
    }

    // Perimeter walls of box columns, with a slab across the full footprint every few floors and on the roof, so every box has something under it.
    private void BuildTower(FVector3 Center, int CellsX, int CellsZ, int Floors, FVector4 Facade)
    {
        float Y = SidewalkHeight;
        float SpanX = CellsX * CellSize;
        float SpanZ = CellsZ * CellSize;

        for (int Floor = 0; Floor < Floors; ++Floor)
        {
            for (int Ix = 0; Ix < CellsX; ++Ix)
            {
                for (int Iz = 0; Iz < CellsZ; ++Iz)
                {
                    bool bEdge = Ix == 0 || Iz == 0 || Ix == CellsX - 1 || Iz == CellsZ - 1;
                    if (!bEdge)
                    {
                        continue;
                    }

                    if (!HasBudget())
                    {
                        return;
                    }

                    FVector3 At = Center + new FVector3((Ix - (CellsX - 1) * 0.5f) * CellSize, Y + CellSize * 0.5f, (Iz - (CellsZ - 1) * 0.5f) * CellSize);
                    SpawnBody("Wall", At, new FVector3(CellSize * BoxFill), Tint(Facade, 0.08f), true);
                }
            }
            Y += CellSize;

            bool bSlab = (Floor + 1) % (int)FloorsPerSlab == 0 || Floor == Floors - 1;
            if (bSlab && HasBudget())
            {
                SpawnBody("Slab", Center + new FVector3(0.0f, Y + SlabThickness * 0.5f, 0.0f), new FVector3(SpanX * BoxFill, SlabThickness * BoxFill, SpanZ * BoxFill), SlabColor, true);
                Y += SlabThickness;
            }
        }
    }

    private void BuildCratePyramid(FVector3 Center, int BaseCount)
    {
        const float Crate = 1.2f;
        for (int Layer = 0; Layer < BaseCount; ++Layer)
        {
            int Side = BaseCount - Layer;
            for (int Ix = 0; Ix < Side; ++Ix)
            {
                for (int Iz = 0; Iz < Side; ++Iz)
                {
                    if (!HasBudget())
                    {
                        return;
                    }

                    FVector3 At = Center + new FVector3((Ix - (Side - 1) * 0.5f) * Crate, SidewalkHeight + (Layer + 0.5f) * Crate, (Iz - (Side - 1) * 0.5f) * Crate);
                    SpawnBody("Crate", At, new FVector3(Crate * BoxFill), Tint(CrateColor, 0.12f), true);
                }
            }
        }
    }

    // Parked cars along both curbs of every street and lamp posts on the corners, so the streets have loose things to scatter.
    private void BuildStreetProps(float Pitch, float Width, float Depth)
    {
        const float CarSpacing = 9.0f;
        FVector3 CarSize = new(1.9f, 1.5f, 4.4f);

        for (int Street = 0; Street <= BlocksX; ++Street)
        {
            float X = (Street - BlocksX * 0.5f) * Pitch;
            for (float Z = -Depth * 0.5f + 6.0f; Z < Depth * 0.5f - 6.0f; Z += CarSpacing)
            {
                if (!HasBudget())
                {
                    return;
                }
                if (Rng.NextDouble() < 0.45)
                {
                    float Lane = Rng.NextDouble() < 0.5 ? -1.0f : 1.0f;
                    SpawnBody("Car", new FVector3(X + Lane * (StreetWidth * 0.5f - 2.0f), CarSize.Y * 0.5f, Z), CarSize, CarColors[Rng.Next(CarColors.Length)], true, 1200.0f);
                }
            }
        }

        FVector3 CarAcross = new(CarSize.Z, CarSize.Y, CarSize.X);
        for (int Street = 0; Street <= BlocksZ; ++Street)
        {
            float Z = (Street - BlocksZ * 0.5f) * Pitch;
            for (float X = -Width * 0.5f + 6.0f; X < Width * 0.5f - 6.0f; X += CarSpacing)
            {
                if (!HasBudget())
                {
                    return;
                }
                if (Rng.NextDouble() < 0.45)
                {
                    float Lane = Rng.NextDouble() < 0.5 ? -1.0f : 1.0f;
                    SpawnBody("Car", new FVector3(X, CarAcross.Y * 0.5f, Z + Lane * (StreetWidth * 0.5f - 2.0f)), CarAcross, CarColors[Rng.Next(CarColors.Length)], true, 1200.0f);
                }
            }
        }

        FVector3 PoleSize = new(0.25f, 6.0f, 0.25f);
        float Corner = BlockSize * 0.5f - 0.6f;
        for (int Bx = 0; Bx < BlocksX; ++Bx)
        {
            for (int Bz = 0; Bz < BlocksZ; ++Bz)
            {
                FVector3 BlockCenter = new((Bx - (BlocksX - 1) * 0.5f) * Pitch, 0.0f, (Bz - (BlocksZ - 1) * 0.5f) * Pitch);
                for (int Cx = -1; Cx <= 1; Cx += 2)
                {
                    for (int Cz = -1; Cz <= 1; Cz += 2)
                    {
                        if (!HasBudget())
                        {
                            return;
                        }
                        SpawnBody("Lamp", BlockCenter + new FVector3(Cx * Corner, SidewalkHeight + PoleSize.Y * 0.5f, Cz * Corner), PoleSize, PoleColor, true, 120.0f);
                    }
                }
            }
        }
    }

    private bool HasBudget()
    {
        if (DynamicBodies.Count < BodyBudget)
        {
            return true;
        }
        bBudgetReached = true;
        return false;
    }

    private FVector4 Tint(FVector4 Color, float Amount)
    {
        float Scale = 1.0f + (float)(Rng.NextDouble() * 2.0 - 1.0) * Amount;
        return new FVector4(Math.Clamp(Color.X * Scale, 0.0f, 1.0f), Math.Clamp(Color.Y * Scale, 0.0f, 1.0f), Math.Clamp(Color.Z * Scale, 0.0f, 1.0f), 1.0f);
    }

    // The primitive meshes span one meter, so the entity scale is the body's size and the collider's default half extent follows it.
    private FEntity SpawnBody(string Name, FVector3 Center, FVector3 Size, FVector4 Color, bool bDynamic, float Mass = 0.0f)
    {
        FEntity Body = World.CreateEntity(Name, Center, null, Size);

        ref SStaticMeshComponent Mesh = ref World.GetOrEmplaceComponent<SStaticMeshComponent>(Body);
        Mesh.StaticMesh = Cube;
        if (BoxMaterial is not null)
        {
            Mesh.SetMaterialAtSlot(BoxMaterial, 0);
        }
        Mesh.CustomPrimitiveData.SetAsColor(Color);

        ref SRigidBodyComponent RigidBody = ref World.GetOrEmplaceComponent<SRigidBodyComponent>(Body);
        RigidBody.BodyType = bDynamic ? EBodyType.Dynamic : EBodyType.Static;
        RigidBody.FrictionOverride = 0.7f;
        RigidBody.RestitutionOverride = 0.02f;
        if (bDynamic)
        {
            RigidBody.bOverrideMass = true;
            RigidBody.Mass = Mass > 0.0f ? Mass : Size.X * Size.Y * Size.Z * BuildingDensity;
        }

        World.GetOrEmplaceComponent<SBoxColliderComponent>(Body);

        CityEntities.Add(Body);
        if (bDynamic)
        {
            DynamicBodies.Add(Body);
        }
        return Body;
    }
}
