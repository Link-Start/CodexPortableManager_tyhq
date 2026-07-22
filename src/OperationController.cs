using System;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPortableManager
{
    internal sealed class OperationSnapshot
    {
        internal OperationSnapshot(
            string installRoot,
            bool sandboxCompatibilityEnabled,
            bool unlockModelCatalogEnabled,
            bool supplementChineseUiEnabled,
            bool englishTechnicalParametersEnabled,
            int installPathRevision)
            : this(
                installRoot,
                sandboxCompatibilityEnabled,
                unlockModelCatalogEnabled,
                supplementChineseUiEnabled,
                englishTechnicalParametersEnabled,
                installPathRevision,
                true,
                true,
                true)
        {
        }

        internal OperationSnapshot(
            string installRoot,
            bool sandboxCompatibilityEnabled,
            bool unlockModelCatalogEnabled,
            bool supplementChineseUiEnabled,
            bool englishTechnicalParametersEnabled,
            int installPathRevision,
            bool manageSandboxCompatibility,
            bool manageModelCatalog,
            bool manageLocalization)
        {
            InstallRoot = installRoot;
            Compatibility = new CompatibilityOptions(
                sandboxCompatibilityEnabled,
                unlockModelCatalogEnabled,
                supplementChineseUiEnabled,
                englishTechnicalParametersEnabled,
                manageSandboxCompatibility,
                manageModelCatalog,
                manageLocalization);
            InstallPathRevision = installPathRevision;
        }

        internal string InstallRoot { get; private set; }
        internal CompatibilityOptions Compatibility { get; private set; }
        internal int InstallPathRevision { get; private set; }
    }

    internal sealed class OperationUiState
    {
        internal OperationUiState(
            bool busy,
            bool canCancel,
            bool locksInterface,
            bool cancellationRequested,
            bool canPause,
            bool isPaused)
        {
            Busy = busy;
            CanCancel = canCancel;
            LocksInterface = locksInterface;
            CancellationRequested = cancellationRequested;
            CanPause = canPause;
            IsPaused = isPaused;
        }

        internal bool Busy { get; private set; }
        internal bool CanCancel { get; private set; }
        internal bool LocksInterface { get; private set; }
        internal bool CancellationRequested { get; private set; }
        internal bool CanPause { get; private set; }
        internal bool IsPaused { get; private set; }
    }

    internal sealed class OperationContext
    {
        internal OperationContext(OperationSnapshot snapshot, CancellationToken token)
        {
            Snapshot = snapshot;
            Token = token;
        }

        internal OperationSnapshot Snapshot { get; private set; }
        internal CancellationToken Token { get; private set; }
    }

    internal sealed class UiState
    {
        private UiState() { }

        internal bool InputEnabled { get; private set; }
        internal bool SandboxCompatibilityEnabled { get; private set; }
        internal bool UnlockModelCatalogEnabled { get; private set; }
        internal bool SupplementChineseUiEnabled { get; private set; }
        internal bool EnglishTechnicalParametersEnabled { get; private set; }
        internal bool CheckEnabled { get; private set; }
        internal bool DownloadEnabled { get; private set; }
        internal bool InstallEnabled { get; private set; }
        internal bool OpenFolderEnabled { get; private set; }
        internal bool LaunchEnabled { get; private set; }
        internal bool RollbackEnabled { get; private set; }
        internal bool UninstallEnabled { get; private set; }
        internal bool ApplyCompatibilityEnabled { get; private set; }
        internal bool CheckCompatibilityStatusEnabled { get; private set; }
        internal bool RepairIntegrationEnabled { get; private set; }
        internal bool MigrateEnabled { get; private set; }
        internal bool CancelEnabled { get; private set; }
        internal bool PauseEnabled { get; private set; }
        internal bool PauseActive { get; private set; }

        internal static UiState Create(UiStateInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            OperationUiState operation = input.Operation;
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            bool idle = !operation.Busy;
            bool usable = idle && input.StatusMatchesCurrentPath;
            bool inputEnabled = (idle || !operation.LocksInterface) &&
                !input.DeploymentCleanupPending;
            bool compatibilityActionsEnabled = usable && input.PortableVersionAvailable;
            bool compatibilitySwitchesEnabled = inputEnabled && idle &&
                input.CompatibilityStateReady;
            CompatibilitySwitchFacts compatibility = input.CompatibilityFacts;
            return new UiState
            {
                InputEnabled = inputEnabled,
                SandboxCompatibilityEnabled = compatibilitySwitchesEnabled &&
                    compatibility != null && compatibility.SandboxCompatibilityEnabled.HasValue,
                UnlockModelCatalogEnabled = compatibilitySwitchesEnabled &&
                    compatibility != null && compatibility.UnlockModelCatalogEnabled.HasValue,
                SupplementChineseUiEnabled = compatibilitySwitchesEnabled &&
                    compatibility != null && compatibility.SupplementChineseUiEnabled.HasValue,
                EnglishTechnicalParametersEnabled = compatibilitySwitchesEnabled &&
                    compatibility != null && compatibility.EnglishTechnicalParametersEnabled.HasValue,
                CheckEnabled = idle && !input.UninstallBackgroundCleanupActive,
                DownloadEnabled = idle,
                InstallEnabled = idle && input.HasInstallRoot && !input.DeploymentCleanupPending,
                OpenFolderEnabled = idle && input.HasInstallRoot &&
                    (!input.DeploymentCleanupPending || input.PortableVersionAvailable),
                LaunchEnabled = usable && input.PortableVersionAvailable,
                RollbackEnabled = usable && !input.DeploymentCleanupPending &&
                    input.PortableVersionAvailable && input.RollbackVersionAvailable,
                UninstallEnabled = usable && !input.DeploymentCleanupPending &&
                    (input.PortableVersionAvailable || input.PreviousVersionAvailable),
                ApplyCompatibilityEnabled = compatibilityActionsEnabled &&
                    input.CompatibilityApplyNeeded,
                CheckCompatibilityStatusEnabled = compatibilityActionsEnabled,
                RepairIntegrationEnabled = compatibilityActionsEnabled,
                MigrateEnabled = usable && !input.DeploymentCleanupPending &&
                    input.HasInstallRoot && input.StoreVersionInstalled,
                CancelEnabled = operation.CanCancel,
                PauseEnabled = operation.CanPause,
                PauseActive = operation.IsPaused
            };
        }
    }

    internal sealed class UiStateInput
    {
        internal UiStateInput(
            OperationUiState operation,
            bool statusMatchesCurrentPath,
            bool portableVersionAvailable,
            bool previousVersionAvailable,
            bool storeVersionInstalled,
            bool hasInstallRoot,
            bool deploymentCleanupPending,
            bool uninstallBackgroundCleanupActive,
            bool compatibilityStateReady,
            CompatibilitySwitchFacts compatibilityFacts,
            bool compatibilityApplyNeeded,
            bool cachedRollbackVersionAvailable = false)
        {
            Operation = operation ?? throw new ArgumentNullException(nameof(operation));
            StatusMatchesCurrentPath = statusMatchesCurrentPath;
            PortableVersionAvailable = portableVersionAvailable;
            PreviousVersionAvailable = previousVersionAvailable;
            StoreVersionInstalled = storeVersionInstalled;
            HasInstallRoot = hasInstallRoot;
            DeploymentCleanupPending = deploymentCleanupPending;
            UninstallBackgroundCleanupActive = uninstallBackgroundCleanupActive;
            CompatibilityStateReady = compatibilityStateReady;
            CompatibilityFacts = compatibilityFacts;
            CompatibilityApplyNeeded = compatibilityApplyNeeded;
            CachedRollbackVersionAvailable = cachedRollbackVersionAvailable;
        }

        internal OperationUiState Operation { get; private set; }
        internal bool StatusMatchesCurrentPath { get; private set; }
        internal bool PortableVersionAvailable { get; private set; }
        internal bool PreviousVersionAvailable { get; private set; }
        internal bool CachedRollbackVersionAvailable { get; private set; }
        internal bool RollbackVersionAvailable
        {
            get { return PreviousVersionAvailable || CachedRollbackVersionAvailable; }
        }
        internal bool StoreVersionInstalled { get; private set; }
        internal bool HasInstallRoot { get; private set; }
        internal bool DeploymentCleanupPending { get; private set; }
        internal bool UninstallBackgroundCleanupActive { get; private set; }
        internal bool CompatibilityStateReady { get; private set; }
        internal CompatibilitySwitchFacts CompatibilityFacts { get; private set; }
        internal bool CompatibilityApplyNeeded { get; private set; }
    }

    internal sealed class OperationPauseToken
    {
        private readonly OperationPauseTokenSource source;

        internal OperationPauseToken(OperationPauseTokenSource sourceValue)
        {
            source = sourceValue;
        }

        internal bool IsPaused
        {
            get { return source != null && source.IsPaused; }
        }

        internal int ResumeVersion
        {
            get { return source == null ? 0 : source.ResumeVersion; }
        }

        internal CancellationToken InterruptionToken
        {
            get { return source == null ? CancellationToken.None : source.InterruptionToken; }
        }

        internal CancellationToken RetryInterruptionToken
        {
            get { return source == null ? CancellationToken.None : source.RetryInterruptionToken; }
        }

        internal Task WaitWhilePausedAsync(CancellationToken cancellationToken)
        {
            return source == null
                ? Task.FromResult(0)
                : source.WaitWhilePausedAsync(cancellationToken);
        }
    }

    internal sealed class OperationPauseTokenSource : IDisposable
    {
        private readonly object syncRoot = new object();
        private TaskCompletionSource<bool> resumeSignal;
        private CancellationTokenSource interruption = new CancellationTokenSource();
        private CancellationTokenSource retryInterruption = new CancellationTokenSource();
        private bool paused;
        private bool disposed;
        private int resumeVersion;

        internal OperationPauseToken Token
        {
            get { return new OperationPauseToken(this); }
        }

        internal bool IsPaused
        {
            get
            {
                lock (syncRoot)
                {
                    return paused && !disposed;
                }
            }
        }

        internal int ResumeVersion
        {
            get
            {
                lock (syncRoot)
                {
                    return resumeVersion;
                }
            }
        }

        internal CancellationToken InterruptionToken
        {
            get
            {
                lock (syncRoot)
                {
                    return disposed ? new CancellationToken(true) : interruption.Token;
                }
            }
        }

        internal CancellationToken RetryInterruptionToken
        {
            get
            {
                lock (syncRoot)
                {
                    return disposed ? new CancellationToken(true) : retryInterruption.Token;
                }
            }
        }

        internal void Pause()
        {
            CancellationTokenSource signal;
            lock (syncRoot)
            {
                if (disposed || paused) return;
                paused = true;
                resumeSignal = new TaskCompletionSource<bool>();
                signal = interruption;
            }
            signal.Cancel();
        }

        internal void Resume()
        {
            TaskCompletionSource<bool> signal;
            lock (syncRoot)
            {
                if (!paused) return;
                paused = false;
                signal = resumeSignal;
                resumeSignal = null;
                interruption = new CancellationTokenSource();
                resumeVersion++;
            }
            if (signal != null) signal.TrySetResult(true);
        }

        internal void RequestRetry()
        {
            CancellationTokenSource signal;
            lock (syncRoot)
            {
                if (disposed || paused) return;
                signal = retryInterruption;
                retryInterruption = new CancellationTokenSource();
            }
            signal.Cancel();
        }

        internal Task WaitWhilePausedAsync(CancellationToken cancellationToken)
        {
            Task signal;
            lock (syncRoot)
            {
                if (!paused || disposed) return Task.FromResult(0);
                signal = resumeSignal.Task;
            }
            return WaitForResumeAsync(signal, cancellationToken);
        }

        private static async Task WaitForResumeAsync(
            Task resumeTask,
            CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
            {
                await resumeTask.ConfigureAwait(false);
                return;
            }

            TaskCompletionSource<bool> cancellationSignal = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(() => cancellationSignal.TrySetResult(true)))
            {
                Task completed = await Task.WhenAny(resumeTask, cancellationSignal.Task).ConfigureAwait(false);
                if (completed != resumeTask) cancellationToken.ThrowIfCancellationRequested();
                await resumeTask.ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            TaskCompletionSource<bool> signal;
            CancellationTokenSource interruptionSignal;
            CancellationTokenSource retrySignal;
            lock (syncRoot)
            {
                if (disposed) return;
                disposed = true;
                paused = false;
                signal = resumeSignal;
                resumeSignal = null;
                interruptionSignal = interruption;
                retrySignal = retryInterruption;
            }
            interruptionSignal.Cancel();
            interruptionSignal.Dispose();
            retrySignal.Cancel();
            retrySignal.Dispose();
            if (signal != null) signal.TrySetResult(true);
        }
    }

    internal sealed class OperationController : IDisposable
    {
        private CancellationTokenSource cancellation;
        private bool busy;
        private bool canCancel;
        private bool cancellationPermanentlyDisabled;
        private bool canPause;
        private bool locksInterface = true;
        private OperationPauseTokenSource pauseSource;

        internal OperationUiState State
        {
            get
            {
                bool cancellationRequested = cancellation != null && cancellation.IsCancellationRequested;
                return new OperationUiState(
                    busy,
                    busy && canCancel && cancellation != null && !cancellationRequested,
                    locksInterface,
                    cancellationRequested,
                    busy && canPause && pauseSource != null && !cancellationRequested,
                    pauseSource != null && pauseSource.IsPaused);
            }
        }

        internal OperationPauseToken PauseToken
        {
            get { return pauseSource == null ? new OperationPauseToken(null) : pauseSource.Token; }
        }

        internal bool TryBegin(
            OperationSnapshot snapshot,
            bool operationCanCancel,
            bool operationLocksInterface,
            out OperationContext context)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (busy)
            {
                context = null;
                return false;
            }

            busy = true;
            canCancel = operationCanCancel;
            cancellationPermanentlyDisabled = !operationCanCancel;
            canPause = false;
            locksInterface = operationLocksInterface;
            cancellation = operationCanCancel ? new CancellationTokenSource() : null;
            pauseSource = operationCanCancel ? new OperationPauseTokenSource() : null;
            context = new OperationContext(
                snapshot,
                cancellation == null ? CancellationToken.None : cancellation.Token);
            return true;
        }

        internal bool TryEnterNonCancelablePhase()
        {
            if (!busy) throw new InvalidOperationException("当前没有正在运行的操作。");
            if (cancellation != null && cancellation.IsCancellationRequested)
            {
                return false;
            }
            cancellationPermanentlyDisabled = true;
            canCancel = false;
            SetPauseAvailability(false);
            locksInterface = true;
            return true;
        }

        internal void SetPauseAvailability(bool available)
        {
            canPause = busy && canCancel && available && pauseSource != null &&
                (cancellation == null || !cancellation.IsCancellationRequested);
            if (!canPause && pauseSource != null) pauseSource.Resume();
        }

        internal void SetCancellationAvailability(bool available)
        {
            if (!busy) throw new InvalidOperationException("当前没有正在运行的操作。");
            canCancel = !cancellationPermanentlyDisabled &&
                available &&
                cancellation != null &&
                !cancellation.IsCancellationRequested;
            if (!canCancel) SetPauseAvailability(false);
        }

        internal bool TogglePause()
        {
            if (!busy || !canPause || pauseSource == null ||
                (cancellation != null && cancellation.IsCancellationRequested)) return false;
            if (pauseSource.IsPaused) pauseSource.Resume();
            else pauseSource.Pause();
            return pauseSource.IsPaused;
        }

        internal bool RequestDownloadRetry()
        {
            if (!busy || !canPause || pauseSource == null || pauseSource.IsPaused ||
                (cancellation != null && cancellation.IsCancellationRequested)) return false;
            pauseSource.RequestRetry();
            return true;
        }

        internal bool RequestCancellation()
        {
            if (busy && canCancel && cancellation != null && !cancellation.IsCancellationRequested)
            {
                if (pauseSource != null) pauseSource.Resume();
                canPause = false;
                cancellation.Cancel();
                return true;
            }
            return false;
        }

        internal string GetClosingMessage()
        {
            OperationUiState state = State;
            if (!state.Busy) return null;
            if (state.CancellationRequested)
            {
                return "取消请求已经提交，请等待当前操作到达安全停止点。";
            }
            if (!state.CanCancel)
            {
                return "当前操作正处于不能安全取消的阶段，请等待完成后再关闭管理器。";
            }
            return "当前操作仍在进行。请先点击“取消操作”，并等待操作结束。";
        }

        internal void Complete()
        {
            CancellationTokenSource completed = cancellation;
            OperationPauseTokenSource completedPause = pauseSource;
            cancellation = null;
            pauseSource = null;
            busy = false;
            canCancel = false;
            cancellationPermanentlyDisabled = false;
            canPause = false;
            locksInterface = true;
            if (completedPause != null) completedPause.Dispose();
            if (completed != null) completed.Dispose();
        }

        public void Dispose()
        {
            Complete();
        }
    }
}
