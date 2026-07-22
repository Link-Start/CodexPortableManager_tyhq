using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CodexPortableManager
{
    internal static class MsixTrustTestRunner
    {
        internal static int Run(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            if (args == null || (args.Length != 7 && args.Length != 9))
            {
                Console.Error.WriteLine(
                    "用法：--msix-trust-test <package> <version> <fullName> <digest> <size> <architecture> [ready-file continue-file]");
                return 64;
            }

            long size;
            if (!long.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out size) || size <= 0)
            {
                Console.Error.WriteLine("MSIX 测试元数据中的文件大小无效。");
                return 64;
            }

            string packagePath = Path.GetFullPath(args[1]);
            PackageMetadata metadata = CreateMetadata(args[2], args[3], args[4], size);
            string architecture = args[6];
            string trustedPackagePath = packagePath;
            try
            {
                AssertRevocationClassification();
                Console.WriteLine("PASS revocation_error_classification");

                if (args.Length == 9)
                {
                    trustedPackagePath = AssertJunctionRetargetKeepsStablePath(
                        packagePath,
                        metadata,
                        architecture,
                        args[7],
                        args[8]);
                    Console.WriteLine("PASS junction_retarget_keeps_stable_path");
                    Console.WriteLine("PASS real_package_trust_and_lock");
                }
                else
                {
                    using (VerifiedArtifactLease lease =
                        MsixPackageTrust.VerifyAndLock(trustedPackagePath, metadata, architecture, Console.WriteLine))
                    {
                        trustedPackagePath = lease.PackagePath;
                        AssertLeaseAllowsReadAndBlocksWrite(trustedPackagePath);
                    }
                    Console.WriteLine("PASS real_package_trust_and_lock");
                }

                AssertStreamedDigestStableHandleTrust(
                    trustedPackagePath,
                    metadata,
                    architecture);
                Console.WriteLine("PASS streamed_digest_stable_handle_trust");

                ExpectInvalid(
                    trustedPackagePath,
                    CreateMetadata(metadata.version, null, metadata.digest, metadata.sizeInBytes),
                    architecture,
                    "fullName");
                ExpectInvalid(
                    trustedPackagePath,
                    CreateMetadata(
                        metadata.version,
                        "OpenAI.Codex_" + metadata.version + "_" + architecture + "__invalidpublisher",
                        metadata.digest,
                        metadata.sizeInBytes),
                    architecture,
                    "PublisherId");
                ExpectInvalid(
                    trustedPackagePath,
                    metadata,
                    string.Equals(architecture, "x64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "x64",
                    "Architecture");
                ExpectInvalid(
                    trustedPackagePath,
                    CreateMetadata(
                        metadata.version,
                        metadata.fullName,
                        Convert.ToBase64String(new byte[32]),
                        metadata.sizeInBytes),
                    architecture,
                    "Digest");
                Console.WriteLine("RESULT=PASS");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("RESULT=FAIL");
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void AssertStreamedDigestStableHandleTrust(
            string packagePath,
            PackageMetadata metadata,
            string architecture)
        {
            FileStream downloadHandle = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete,
                1024 * 1024,
                FileOptions.SequentialScan);
            try
            {
                using (VerifiedArtifactLease lease = MsixPackageTrust.VerifyAndLock(
                    packagePath,
                    metadata,
                    architecture,
                    Console.WriteLine,
                    downloadHandle,
                    metadata.digest))
                {
                    downloadHandle = null;
                    AssertLeaseAllowsReadAndBlocksWrite(lease.PackagePath);
                }
            }
            finally
            {
                if (downloadHandle != null) downloadHandle.Dispose();
            }
        }

        private static string AssertJunctionRetargetKeepsStablePath(
            string packagePath,
            PackageMetadata metadata,
            string architecture,
            string readyPath,
            string continuePath)
        {
            using (VerifiedArtifactLease lease =
                MsixPackageTrust.VerifyAndLock(packagePath, metadata, architecture, Console.WriteLine))
            {
                string stablePath = lease.PackagePath;
                if (PathsEqual(lease.PackagePath, packagePath))
                {
                    throw new InvalidDataException("可信租约没有把 junction 包路径解析为稳定物理路径。");
                }
                AssertLeaseAllowsReadAndBlocksWrite(stablePath);
                File.WriteAllText(readyPath, lease.PackagePath, Encoding.UTF8);
                DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                while (!File.Exists(continuePath))
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        throw new TimeoutException("等待测试进程换向 junction 超时。");
                    }
                    Thread.Sleep(50);
                }

                string swappedContents = File.ReadAllText(packagePath, Encoding.UTF8);
                if (!string.Equals(swappedContents, "UNTRUSTED_SWAP_TARGET", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("测试 junction 换向后没有读取到伪造目标。");
                }
                if (!string.Equals(ComputeSha256Base64(lease.PackagePath), metadata.digest, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("junction 换向改变了可信租约绑定的 MSIX 文件对象。");
                }
                return stablePath;
            }
        }

        private static void AssertLeaseAllowsReadAndBlocksWrite(string packagePath)
        {
            using (FileStream reader = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                if (reader.Length <= 0) throw new InvalidDataException("第二读取者无法读取有效 MSIX。");
            }

            bool writeBlocked = false;
            try
            {
                using (FileStream ignored = new FileStream(
                    packagePath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite))
                {
                }
            }
            catch (IOException)
            {
                writeBlocked = true;
            }
            if (!writeBlocked) throw new InvalidDataException("可信租约未阻止 MSIX 写入。");
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string ComputeSha256Base64(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha256 = SHA256.Create())
            {
                return Convert.ToBase64String(sha256.ComputeHash(stream));
            }
        }

        private static PackageMetadata CreateMetadata(
            string version,
            string fullName,
            string digest,
            long size)
        {
            return new PackageMetadata
            {
                version = version,
                fullName = fullName,
                digest = digest,
                sizeInBytes = size
            };
        }

        private static void AssertRevocationClassification()
        {
            int[] unavailable =
            {
                unchecked((int)0x80092012),
                unchecked((int)0x80092013),
                unchecked((int)0x80092014),
                unchecked((int)0x800B010E)
            };
            foreach (int error in unavailable)
            {
                if (!MsixPackageTrust.IsRevocationStatusUnavailable(error))
                {
                    throw new InvalidDataException("吊销状态不可获取错误未被识别：0x" +
                        unchecked((uint)error).ToString("X8"));
                }
            }

            int[] strictFailures =
            {
                unchecked((int)0x800B010C),
                unchecked((int)0x80092010),
                unchecked((int)0x80096010),
                unchecked((int)0x800B0100)
            };
            foreach (int error in strictFailures)
            {
                if (MsixPackageTrust.IsRevocationStatusUnavailable(error))
                {
                    throw new InvalidDataException("严格信任错误被错误降级：0x" +
                        unchecked((uint)error).ToString("X8"));
                }
            }
        }

        private static void ExpectInvalid(
            string packagePath,
            PackageMetadata metadata,
            string architecture,
            string name)
        {
            try
            {
                using (MsixPackageTrust.VerifyAndLock(packagePath, metadata, architecture, null))
                {
                }
            }
            catch (InvalidDataException)
            {
                Console.WriteLine("PASS reject_" + name);
                return;
            }
            throw new InvalidDataException("无效样本未被拒绝：" + name);
        }
    }
}
