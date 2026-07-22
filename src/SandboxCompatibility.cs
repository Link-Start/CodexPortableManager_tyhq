using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Esprima.Ast;

namespace CodexPortableManager
{
    internal static class SandboxCompatibility
    {
        internal const string ManagedMarker =
            "/*codex-portable-manager:sandbox-account-environment*/";

        private const int ErrorInsufficientBuffer = 122;
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly string ManagedScript =
            "(()=>{let u=process.env.USERNAME,d=process.env.USERDOMAIN;" +
            "if(u&&d&&!u.includes(\"\\\\\")&&!u.includes(\"@\"))" +
            "process.env.USERNAME=d+\"\\\\\"+u})()" + ManagedMarker + ";";
        private static readonly string ManagedInsertion = ";" + ManagedScript;

        public static bool TryConfigure(string executablePath, bool enabled, Action<string> log)
        {
            try
            {
                Configure(executablePath, enabled, log);
                return true;
            }
            catch (Exception exception)
            {
                SafeLog(
                    log,
                    "警告：Windows 沙箱账户名兼容设置未能完成，已保留当前 app.asar。原因：" +
                    exception.Message);
                return false;
            }
        }

        public static void Configure(string executablePath, bool enabled, Action<string> log)
        {
            string asarPath = AsarSession.GetAsarPath(executablePath);
            using (AsarSession session = AsarSession.Open(asarPath))
            {
                CompatibilityFeatureChange change = Plan(session, enabled, log);
                if (!change.Succeeded)
                {
                    throw new InvalidDataException(
                        change.Error ?? "Windows 沙箱账户名兼容设置不支持当前 app.asar。");
                }
                if (!change.Changed)
                {
                    SafeLog(log, change.CompletionMessage);
                    return;
                }
                session.WriteAtomically(change.Verify);
                SafeLog(log, change.CompletionMessage);
            }
        }

        public static bool IsEnabled(string executablePath)
        {
            try
            {
                using (AsarSession session = AsarSession.Open(AsarSession.GetAsarPath(executablePath)))
                {
                    return Inspect(session).State == CompatibilityPatchState.Patched;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static CompatibilityFeatureChange InspectFeature(AsarSession session)
        {
            SandboxPatchInspection inspection = Inspect(session);
            if (inspection.State == CompatibilityPatchState.Official ||
                inspection.State == CompatibilityPatchState.Patched)
            {
                string state = ToFeatureState(inspection.State);
                return new CompatibilityFeatureChange
                {
                    Succeeded = true,
                    Changed = false,
                    Before = state,
                    Desired = state,
                    After = state,
                    Status = CompatibilityFeatureStatus.AlreadySatisfied,
                    RecipeId = CompatibilityCoordinator.SandboxRecipeId
                };
            }
            return CompatibilityFeatureChange.Failure(
                inspection.Error ?? "Windows 沙箱账户名补丁处于非当前结构。",
                inspection.State == CompatibilityPatchState.Unsupported
                    ? CompatibilityFeatureStatus.Unsupported
                    : CompatibilityFeatureStatus.Failed);
        }

        internal static CompatibilityFeatureChange Plan(
            AsarSession session,
            bool enabled,
            Action<string> log)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            SandboxPatchInspection inspection = Inspect(session);
            CompatibilityPatchState desired = enabled
                ? CompatibilityPatchState.Patched
                : CompatibilityPatchState.Official;
            string desiredState = ToFeatureState(desired);

            if (inspection.State == CompatibilityPatchState.Unsupported ||
                inspection.State == CompatibilityPatchState.Mixed)
            {
                string error = inspection.Error ?? "Windows 沙箱账户名补丁不符合当前唯一结构。";
                SafeLog(log, "警告：" + error);
                return new CompatibilityFeatureChange
                {
                    Succeeded = false,
                    Changed = false,
                    Before = ToFeatureState(inspection.State),
                    Desired = desiredState,
                    After = ToFeatureState(inspection.State),
                    Status = inspection.State == CompatibilityPatchState.Unsupported
                        ? CompatibilityFeatureStatus.Unsupported
                        : CompatibilityFeatureStatus.Failed,
                    Error = error,
                    RecipeId = CompatibilityCoordinator.SandboxRecipeId
                };
            }

            if (inspection.State == desired)
            {
                return new CompatibilityFeatureChange
                {
                    Succeeded = true,
                    Changed = false,
                    Before = ToFeatureState(inspection.State),
                    Desired = desiredState,
                    After = ToFeatureState(inspection.State),
                    Status = CompatibilityFeatureStatus.AlreadySatisfied,
                    RecipeId = CompatibilityCoordinator.SandboxRecipeId,
                    CompletionMessage = enabled
                        ? "Windows 沙箱账户名环境修正已经处于开启状态。"
                        : "Windows 沙箱账户名环境修正已经处于关闭状态。"
                };
            }

            string changedText = enabled
                ? inspection.Text.Substring(0, inspection.InsertionIndex) +
                    ManagedInsertion +
                    inspection.Text.Substring(inspection.InsertionIndex)
                : inspection.Text.Substring(0, inspection.InsertionIndex) +
                    inspection.Text.Substring(
                        inspection.InsertionIndex + ManagedInsertion.Length);
            session.StageEntry(inspection.Entry, Encoding.UTF8.GetBytes(changedText));
            return new CompatibilityFeatureChange
            {
                Succeeded = true,
                Changed = true,
                Before = ToFeatureState(inspection.State),
                Desired = desiredState,
                After = desiredState,
                Status = CompatibilityFeatureStatus.Applied,
                RecipeId = CompatibilityCoordinator.SandboxRecipeId,
                CompletionMessage = enabled
                    ? "已启用 Windows 沙箱账户名环境修正；官方签名 helper 保持原位且未被改写。"
                    : "已关闭 Windows 沙箱账户名环境修正；app.asar 已恢复官方脚本。",
                Verify = verified =>
                {
                    SandboxPatchInspection result = Inspect(verified);
                    if (result.State != desired)
                    {
                        throw new InvalidDataException("Windows 沙箱账户名环境修正提交验证失败。");
                    }
                }
            };
        }

        internal static bool NeedsCompatibilityFix(out string reason)
        {
            reason = string.Empty;
            string bareUserName = Environment.GetEnvironmentVariable("USERNAME");
            if (string.IsNullOrWhiteSpace(bareUserName) ||
                bareUserName.IndexOf('\\') >= 0 ||
                bareUserName.IndexOf('@') >= 0)
            {
                reason = "当前 USERNAME 已带限定信息或无法读取。";
                return false;
            }

            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query))
                {
                    SecurityIdentifier currentSid = identity == null ? null : identity.User;
                    if (currentSid == null)
                    {
                        reason = "无法取得当前 token 用户 SID。";
                        return false;
                    }

                    SecurityIdentifier bareSid;
                    SidNameUse bareUse;
                    int bareError;
                    bool bareResolved = TryLookupAccountName(
                        bareUserName,
                        out bareSid,
                        out bareUse,
                        out bareError);
                    if (bareResolved &&
                        bareUse == SidNameUse.User &&
                        currentSid.Equals(bareSid))
                    {
                        reason = "裸 USERNAME 已正确绑定当前 token SID。";
                        return false;
                    }

                    string domain = Environment.GetEnvironmentVariable("USERDOMAIN");
                    string qualifiedName = string.IsNullOrWhiteSpace(domain)
                        ? null
                        : domain + "\\" + bareUserName;
                    SecurityIdentifier qualifiedSid;
                    SidNameUse qualifiedUse;
                    int qualifiedError;
                    if (string.IsNullOrWhiteSpace(qualifiedName) ||
                        !TryLookupAccountName(
                            qualifiedName,
                            out qualifiedSid,
                            out qualifiedUse,
                            out qualifiedError) ||
                        qualifiedUse != SidNameUse.User ||
                        !currentSid.Equals(qualifiedSid))
                    {
                        reason = "裸用户名解析异常，但 USERDOMAIN\\USERNAME 不能可靠绑定当前 token SID。";
                        return false;
                    }

                    reason = bareResolved
                        ? "裸用户名被解析为 " + bareUse + "，SID=" + bareSid.Value + "。"
                        : "LookupAccountNameW(\"" + bareUserName + "\") 失败，Win32=" + bareError + "。";
                    return true;
                }
            }
            catch (Exception exception)
            {
                reason = "账户解析检测未完成，因此不修改 app.asar：" + exception.Message;
                return false;
            }
        }

        private static SandboxPatchInspection Inspect(AsarSession session)
        {
            AsarArchiveEntry entry;
            try
            {
                entry = AsarPackageMetadata.ResolveElectronMainEntry(session);
            }
            catch (Exception exception)
            {
                return SandboxPatchInspection.Unsupported(exception.Message);
            }

            byte[] data;
            try
            {
                data = session.GetEntryData(entry);
            }
            catch (Exception exception)
            {
                return SandboxPatchInspection.Unsupported(exception.Message);
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(data);
            }
            catch (DecoderFallbackException exception)
            {
                return SandboxPatchInspection.Unsupported(
                    "package.json.main 指向的 JavaScript 条目不是有效 UTF-8：" + exception.Message);
            }

            int markerCount = AsarSession.CountAscii(data, ManagedMarker);
            int markerCountOutsideEntry = 0;
            string firstUnexpectedEntry = null;
            try
            {
                session.ScanEntries(
                    value => !object.ReferenceEquals(value, entry),
                    delegate(AsarArchiveEntry candidate, byte[] candidateData)
                    {
                        int count = AsarSession.CountAscii(candidateData, ManagedMarker);
                        if (count <= 0) return;
                        markerCountOutsideEntry += count;
                        if (firstUnexpectedEntry == null) firstUnexpectedEntry = candidate.Path;
                    });
            }
            catch (Exception exception)
            {
                return SandboxPatchInspection.Unsupported(
                    "检查 app.asar 中的沙箱补丁标记失败：" + exception.Message);
            }

            if (markerCountOutsideEntry > 0)
            {
                return SandboxPatchInspection.Mixed(
                    entry,
                    text,
                    "Windows 沙箱账户名补丁标记出现在非 Electron 入口条目：" +
                    firstUnexpectedEntry + "。");
            }
            if (markerCount == 0)
            {
                int insertionIndex;
                string insertionError;
                if (!TryFindSafeInsertionIndex(text, out insertionIndex, out insertionError))
                {
                    return SandboxPatchInspection.Unsupported(insertionError);
                }
                return SandboxPatchInspection.Current(
                    entry,
                    text,
                    CompatibilityPatchState.Official,
                    insertionIndex);
            }
            int managedIndex = text.IndexOf(ManagedInsertion, StringComparison.Ordinal);
            if (markerCount == 1 && managedIndex >= 0 &&
                text.IndexOf(
                    ManagedInsertion,
                    managedIndex + ManagedInsertion.Length,
                    StringComparison.Ordinal) < 0)
            {
                string restored = text.Substring(0, managedIndex) +
                    text.Substring(managedIndex + ManagedInsertion.Length);
                int expectedIndex;
                string insertionError;
                if (TryFindSafeInsertionIndex(
                        restored,
                        out expectedIndex,
                        out insertionError) &&
                    expectedIndex == managedIndex)
                {
                    return SandboxPatchInspection.Current(
                        entry,
                        text,
                        CompatibilityPatchState.Patched,
                        managedIndex);
                }
            }
            return SandboxPatchInspection.Mixed(
                entry,
                text,
                "Windows 沙箱账户名补丁标记存在，但不符合当前唯一脚本结构。");
        }

        private static bool TryFindSafeInsertionIndex(
            string text,
            out int insertionIndex,
            out string error)
        {
            insertionIndex = 0;
            error = null;
            if (text == null)
            {
                error = "Electron 主入口脚本为空。";
                return false;
            }

            int prefixLength = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0;
            if (text.Length >= prefixLength + 2 &&
                text[prefixLength] == '#' &&
                text[prefixLength + 1] == '!')
            {
                int lineEnd = text.IndexOf('\n', prefixLength + 2);
                prefixLength = lineEnd < 0 ? text.Length : lineEnd + 1;
            }

            string source = text.Substring(prefixLength);
            JavaScriptSemanticDocument document;
            try { document = JavaScriptSemanticDocument.Parse(source); }
            catch (Exception exception)
            {
                error = "Electron 主入口脚本无法进行安全语义定位：" + exception.Message;
                return false;
            }

            int relativeIndex = 0;
            foreach (Node node in document.Root.ChildNodes)
            {
                ExpressionStatement statement = node as ExpressionStatement;
                if (statement == null ||
                    JavaScriptSemanticDocument.StringValue(statement.Expression) == null)
                {
                    break;
                }
                relativeIndex = statement.Range.End;
            }
            insertionIndex = prefixLength + relativeIndex;
            return true;
        }

        private static string ToFeatureState(CompatibilityPatchState state)
        {
            if (state == CompatibilityPatchState.Patched) return "Enabled";
            if (state == CompatibilityPatchState.Official) return "Disabled";
            return state.ToString();
        }

        private static void SafeLog(Action<string> log, string message)
        {
            if (log == null || string.IsNullOrWhiteSpace(message)) return;
            try { log(message); }
            catch { }
        }

        private static bool TryLookupAccountName(
            string accountName,
            out SecurityIdentifier sid,
            out SidNameUse use,
            out int error)
        {
            sid = null;
            use = SidNameUse.Unknown;
            error = 0;
            uint sidLength = 0;
            uint domainLength = 0;
            LookupAccountName(
                null,
                accountName,
                null,
                ref sidLength,
                null,
                ref domainLength,
                out use);
            error = Marshal.GetLastWin32Error();
            if (error != ErrorInsufficientBuffer || sidLength == 0) return false;

            byte[] sidBuffer = new byte[sidLength];
            StringBuilder domain = new StringBuilder((int)Math.Max(domainLength, 1));
            if (!LookupAccountName(
                null,
                accountName,
                sidBuffer,
                ref sidLength,
                domain,
                ref domainLength,
                out use))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            sid = new SecurityIdentifier(sidBuffer, 0);
            error = 0;
            return true;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupAccountName(
            string systemName,
            string accountName,
            byte[] sid,
            ref uint sidSize,
            StringBuilder referencedDomainName,
            ref uint referencedDomainNameSize,
            out SidNameUse use);

        private enum SidNameUse
        {
            User = 1,
            Group = 2,
            Domain = 3,
            Alias = 4,
            WellKnownGroup = 5,
            DeletedAccount = 6,
            Invalid = 7,
            Unknown = 8,
            Computer = 9,
            Label = 10,
            LogonSession = 11
        }

        private sealed class SandboxPatchInspection
        {
            internal AsarArchiveEntry Entry;
            internal string Text;
            internal CompatibilityPatchState State;
            internal string Error;
            internal int InsertionIndex;

            internal static SandboxPatchInspection Current(
                AsarArchiveEntry entry,
                string text,
                CompatibilityPatchState state,
                int insertionIndex)
            {
                return new SandboxPatchInspection
                {
                    Entry = entry,
                    Text = text,
                    State = state,
                    InsertionIndex = insertionIndex
                };
            }

            internal static SandboxPatchInspection Unsupported(string error)
            {
                return new SandboxPatchInspection
                {
                    State = CompatibilityPatchState.Unsupported,
                    Error = error
                };
            }

            internal static SandboxPatchInspection Mixed(
                AsarArchiveEntry entry,
                string text,
                string error)
            {
                return new SandboxPatchInspection
                {
                    Entry = entry,
                    Text = text,
                    State = CompatibilityPatchState.Mixed,
                    Error = error
                };
            }
        }
    }
}
