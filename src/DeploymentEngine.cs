using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace CodexPortableManager
{
    internal sealed partial class DeploymentEngine
    {
        internal static Action<string> MissingCleanupReceiptObservedForTest;
        internal static Func<string, DriveType> InstallRootDriveTypeProviderForTest;

        private readonly Action<string> log;
        private readonly ArtifactPipeline artifactPipeline;
        private readonly CompatibilityCoordinator compatibilityCoordinator;
        private readonly CompatibilityMaintenance compatibilityMaintenance;
        private readonly ShellIntegrationCoordinator shellIntegrationCoordinator;

        public DeploymentEngine(
            Action<string> logAction,
            ArtifactPipeline artifactPipelineValue,
            CompatibilityCoordinator compatibilityCoordinatorValue,
            ShellIntegrationCoordinator shellIntegrationCoordinatorValue)
        {
            log = logAction ?? delegate { };
            artifactPipeline = artifactPipelineValue ?? throw new ArgumentNullException(nameof(artifactPipelineValue));
            compatibilityCoordinator = compatibilityCoordinatorValue ?? throw new ArgumentNullException(nameof(compatibilityCoordinatorValue));
            compatibilityMaintenance = new CompatibilityMaintenance(
                compatibilityCoordinator.ApplyOfficialStaging,
                InstallOwnership.WriteMarker,
                log);
            shellIntegrationCoordinator = shellIntegrationCoordinatorValue ?? throw new ArgumentNullException(nameof(shellIntegrationCoordinatorValue));
        }


    }
}
