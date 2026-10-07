using LuminaBuildTool.Configuration;

public class PhysicsCityTarget : LuminaGameTargetRules
{
    public PhysicsCityTarget(TargetInfo Target)
        : base(Target)
    {
        LaunchModuleName = "PhysicsCity";

        // This target builds a library the editor loads, so Run and Debug launch the editor with
        // this project already open rather than trying to execute the library.
        SetProjectFileToOpen("../PhysicsCity.lproject");
    }
}
