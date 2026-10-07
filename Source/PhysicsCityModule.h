#pragma once
#include "Core/Module/ModuleInterface.h"

namespace Lumina
{
    class PHYSICSCITY_API FPhysicsCityModule : public IModuleInterface
    {
    public:
        
        void StartupModule() override;
        void ShutdownModule() override;
        
    };
}
