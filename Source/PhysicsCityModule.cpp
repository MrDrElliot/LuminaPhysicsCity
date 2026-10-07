
#include "PhysicsCityModule.h"
#include "Core/Module/ModuleManager.h"
#include "Log/Log.h"

using namespace Lumina;

IMPLEMENT_MODULE(FPhysicsCityModule, "PhysicsCity");

void FPhysicsCityModule::StartupModule()
{
    LOG_INFO("PhysicsCity Startup Module");
}

void FPhysicsCityModule::ShutdownModule()
{
    LOG_INFO("PhysicsCity Shutdown Module");

}
