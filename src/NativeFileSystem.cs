using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexPortableManager
{
    internal enum NativePathKind
    {
        Missing = 0,
        File = 1,
        Directory = 2,
        ReparsePoint = 3
    }

    internal static class NativeFileSystem
    {
        private const uint DeleteAccess = 0x00010000;
        private const uint SynchronizeAccess = 0x00100000;
        private const uint FileListDirectory = 0x00000001;
        private const uint FileReadAttributes = 0x00000080;
        private const uint FileWriteAttributes = 0x00000100;

        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint SafeTraversalShare = FileShareRead | FileShareWrite | FileShareDelete;

        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;

        private const uint FileOpen = 1;
        private const uint FileSynchronousIoNonAlert = 0x00000020;
        private const uint FileOpenReparsePoint = 0x00200000;
        private const uint ObjCaseInsensitive = 0x00000040;

        private const uint FileAttributeReadOnly = 0x00000001;
        private const uint FileAttributeHidden = 0x00000002;
        private const uint FileAttributeSystem = 0x00000004;
        private const uint FileAttributeDirectory = 0x00000010;
        private const uint FileAttributeArchive = 0x00000020;
        private const uint FileAttributeNormal = 0x00000080;
        private const uint FileAttributeTemporary = 0x00000100;
        private const uint FileAttributeReparsePoint = 0x00000400;
        private const uint FileAttributeOffline = 0x00001000;
        private const uint FileAttributeNotContentIndexed = 0x00002000;
        private const uint MutableFileAttributeMask = FileAttributeHidden
            | FileAttributeSystem
            | FileAttributeArchive
            | FileAttributeTemporary
            | FileAttributeOffline
            | FileAttributeNotContentIndexed;

        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorNoMoreFiles = 18;
        private const int DirectoryQueryBufferSize = 64 * 1024;
        private const int FileIdBothDirectoryInfoFileAttributesOffset = 56;
        private const int FileIdBothDirectoryInfoFileNameLengthOffset = 60;
        private const int FileIdBothDirectoryInfoFileNameOffset = 104;

        public static void DeleteDirectoryRecursively(string path)
        {
            DeleteDirectoryRecursively(path, null);
        }

        internal static void DeleteEmptyDirectory(string path)
        {
            DeleteEmptyDirectory(path, null);
        }

        internal static void DeleteEmptyDirectory(
            string path,
            string expectedDirectoryIdentity)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("待删除空目录不能为空。", nameof(path));
            }

            string fullPath = TrimEndingDirectorySeparators(Path.GetFullPath(path));
            fullPath = ExpandShortPathAliases(fullPath);
            string parentPath = Path.GetDirectoryName(fullPath);
            string leafName = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(parentPath) || string.IsNullOrWhiteSpace(leafName))
            {
                throw new IOException("出于安全原因，禁止删除文件系统根目录：" + fullPath);
            }

            using (SafeFileHandle parent = OpenStableParent(parentPath, fullPath))
            {
                if (parent == null)
                {
                    return;
                }
                using (SafeFileHandle component = OpenChild(
                    parent,
                    leafName,
                    fullPath,
                    true,
                    true))
                {
                    if (component == null)
                    {
                        return;
                    }
                    FileAttributeTagInfo info = GetAttributeTagInfo(component, fullPath);
                    if ((info.FileAttributes & FileAttributeDirectory) == 0 ||
                        (info.FileAttributes & FileAttributeReparsePoint) != 0)
                    {
                        throw new IOException("待删除空目录不是普通目录：" + fullPath);
                    }
                    if (expectedDirectoryIdentity != null)
                    {
                        InstallOwnership.EnsureManagedDirectoryIdentity(
                            component,
                            fullPath,
                            expectedDirectoryIdentity);
                    }
                    if (FindChildEntries(component, fullPath).Count != 0)
                    {
                        throw new IOException("待删除目录不再为空，已拒绝递归清理：" + fullPath);
                    }

                    // 若枚举后又有子项进入，Windows 会以目录非空拒绝这一句柄删除。
                    DeleteOpenedEntry(component, fullPath, info.FileAttributes);
                }
            }
        }

        internal static void DeleteFile(string path)
        {
            DeleteFile(path, null);
        }

        internal static void DeleteFile(string path, string expectedFileIdentity)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("待删除文件不能为空。", nameof(path));
            }

            string fullPath = ExpandShortPathAliases(Path.GetFullPath(path));
            string parentPath = Path.GetDirectoryName(fullPath);
            string leafName = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(parentPath) || string.IsNullOrWhiteSpace(leafName))
            {
                throw new IOException("待删除文件路径缺少安全父目录：" + fullPath);
            }

            using (SafeFileHandle parent = OpenStableParent(parentPath, fullPath))
            {
                if (parent == null)
                {
                    return;
                }
                using (SafeFileHandle component = OpenChild(
                    parent,
                    leafName,
                    fullPath,
                    false,
                    true))
                {
                    if (component == null)
                    {
                        return;
                    }
                    FileAttributeTagInfo info = GetAttributeTagInfo(component, fullPath);
                    bool isDirectory = (info.FileAttributes & FileAttributeDirectory) != 0;
                    bool isReparsePoint = (info.FileAttributes & FileAttributeReparsePoint) != 0;
                    if (isDirectory && !isReparsePoint)
                    {
                        throw new IOException("待删除文件路径被普通目录占用：" + fullPath);
                    }
                    if (expectedFileIdentity != null)
                    {
                        if (isReparsePoint)
                        {
                            throw new InvalidDataException("带身份凭据的待删除文件被重解析点替换：" + fullPath);
                        }
                        EnsurePersistentFileIdentity(component, fullPath, expectedFileIdentity);
                    }
                    DeleteOpenedEntry(component, fullPath, info.FileAttributes);
                }
            }
        }

        internal static NativePathKind GetPathKind(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("待探测路径不能为空。", nameof(path));
            }
            string fullPath = Path.GetFullPath(path);
            using (SafeFileHandle handle = CreateFile(
                ToExtendedPath(fullPath),
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (IsMissingError(error))
                    {
                        return NativePathKind.Missing;
                    }
                    throw new Win32Exception(error, "无法可靠探测文件系统路径：" + fullPath);
                }
                FileAttributeTagInfo info = GetAttributeTagInfo(handle, fullPath);
                if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    return NativePathKind.ReparsePoint;
                }
                return (info.FileAttributes & FileAttributeDirectory) != 0
                    ? NativePathKind.Directory
                    : NativePathKind.File;
            }
        }

        internal static void DeleteDirectoryRecursively(
            string path,
            string expectedDirectoryIdentity)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("待删除目录不能为空。", nameof(path));
            }

            string fullPath = TrimEndingDirectorySeparators(Path.GetFullPath(path));
            fullPath = ExpandShortPathAliases(fullPath);
            string parentPath = Path.GetDirectoryName(fullPath);
            string leafName = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(parentPath) || string.IsNullOrWhiteSpace(leafName))
            {
                throw new IOException("出于安全原因，禁止递归删除文件系统根目录：" + fullPath);
            }

            SafeFileHandle parent = CreateFile(
                ToExtendedPath(parentPath),
                0,
                SafeTraversalShare,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);

            if (parent.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                parent.Dispose();
                if (IsMissingError(error))
                {
                    return;
                }
                throw new Win32Exception(error, "无法安全打开待删除目录的父目录：" + parentPath);
            }

            using (parent)
            {
                string stableParent = TrimEndingDirectorySeparators(
                    GetStablePathFromHandle(parent));
                string expectedParent = TrimEndingDirectorySeparators(
                    Path.GetFullPath(parentPath));
                if (!string.Equals(
                    stableParent,
                    expectedParent,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        "待删除路径包含重解析点祖先，已拒绝跟随：" + parentPath +
                        " -> " + stableParent);
                }

                using (SafeFileHandle component = OpenChild(
                    parent,
                    leafName,
                    fullPath,
                    true,
                    true))
                {
                    if (component == null)
                    {
                        return;
                    }

                    FileAttributeTagInfo info = GetAttributeTagInfo(component, fullPath);
                    bool isDirectory = (info.FileAttributes & FileAttributeDirectory) != 0;
                    bool isReparsePoint = (info.FileAttributes & FileAttributeReparsePoint) != 0;

                    // 最终组件本身是重解析点时，只删除链接对象，绝不枚举其目标目录。
                    if (isReparsePoint)
                    {
                        if (expectedDirectoryIdentity != null)
                        {
                            throw new InvalidDataException(
                                "带身份凭据的清理目录已被重解析点替换：" + fullPath);
                        }
                        DeleteOpenedEntry(component, fullPath, info.FileAttributes);
                        return;
                    }

                    if (!isDirectory)
                    {
                        throw new IOException("待删除路径不是目录：" + fullPath);
                    }

                    if (expectedDirectoryIdentity != null)
                    {
                        // 身份校验和递归删除共用最终目录句柄，消除路径复验后的替换窗口。
                        InstallOwnership.EnsureManagedDirectoryIdentity(
                            component,
                            fullPath,
                            expectedDirectoryIdentity);
                    }

                    DeleteDirectoryContents(component, fullPath);
                    DeleteOpenedEntry(component, fullPath, info.FileAttributes);
                }
            }
        }

        private static void DeleteDirectoryContents(SafeFileHandle directory, string displayPath)
        {
            while (true)
            {
                List<DeleteDirectoryEntry> children = FindChildEntries(directory, displayPath);
                if (children.Count == 0)
                {
                    return;
                }

                foreach (DeleteDirectoryEntry entry in children)
                {
                    string childDisplayPath = CombineForDisplay(displayPath, entry.Name);
                    using (SafeFileHandle child = OpenChild(
                        directory,
                        entry.Name,
                        childDisplayPath,
                        entry.IsTraversableDirectory,
                        entry.MayRequireAttributeWrite))
                    {
                        if (child == null)
                        {
                            // 批量枚举后文件可能被其他进程删除；跳过并在下一轮重新核对目录。
                            continue;
                        }

                        FileAttributeTagInfo info = GetAttributeTagInfo(child, childDisplayPath);
                        bool isDirectory = (info.FileAttributes & FileAttributeDirectory) != 0;
                        bool isReparsePoint = (info.FileAttributes & FileAttributeReparsePoint) != 0;
                        if (isDirectory && !isReparsePoint && !entry.IsTraversableDirectory)
                        {
                            throw new IOException(
                                "目录子项在枚举后由文件变为目录，已拒绝使用权限不足的旧句柄：" +
                                childDisplayPath);
                        }
                        if ((info.FileAttributes & FileAttributeReadOnly) != 0 &&
                            !entry.MayRequireAttributeWrite)
                        {
                            throw new IOException(
                                "目录子项在枚举后变为只读，正在使用新权限重试：" + childDisplayPath);
                        }

                        // 子级重解析点只删除链接对象本身，始终不读取其目标内容。
                        if (isDirectory && !isReparsePoint)
                        {
                            DeleteDirectoryContents(child, childDisplayPath);
                        }

                        DeleteOpenedEntry(child, childDisplayPath, info.FileAttributes);
                    }
                }
            }
        }

        private static SafeFileHandle OpenStableParent(
            string parentPath,
            string displayPath)
        {
            SafeFileHandle parent = CreateFile(
                ToExtendedPath(parentPath),
                0,
                SafeTraversalShare,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);
            if (parent.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                parent.Dispose();
                if (IsMissingError(error))
                {
                    return null;
                }
                throw new Win32Exception(
                    error,
                    "无法安全打开待删除对象的父目录：" + parentPath);
            }

            try
            {
                string stableParent = TrimEndingDirectorySeparators(
                    GetStablePathFromHandle(parent));
                string expectedParent = TrimEndingDirectorySeparators(
                    Path.GetFullPath(parentPath));
                if (!string.Equals(
                    stableParent,
                    expectedParent,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        "待删除路径包含重解析点祖先，已拒绝跟随：" + parentPath +
                        " -> " + stableParent + "（目标：" + displayPath + "）");
                }
                return parent;
            }
            catch
            {
                parent.Dispose();
                throw;
            }
        }

        private static SafeFileHandle OpenChild(
            SafeFileHandle parent,
            string childName,
            string displayPath,
            bool includeListDirectory,
            bool includeWriteAttributes)
        {
            if (string.IsNullOrEmpty(childName)
                || childName == "."
                || childName == ".."
                || childName.IndexOf('\\') >= 0
                || childName.IndexOf('/') >= 0)
            {
                throw new IOException("目录枚举返回了不安全的子项名称：" + displayPath);
            }

            IntPtr nameBuffer = IntPtr.Zero;
            IntPtr unicodeStringBuffer = IntPtr.Zero;
            bool parentReferenceAdded = false;
            try
            {
                nameBuffer = Marshal.StringToHGlobalUni(childName);
                UnicodeString unicodeName = new UnicodeString
                {
                    Length = checked((ushort)(childName.Length * sizeof(char))),
                    MaximumLength = checked((ushort)((childName.Length + 1) * sizeof(char))),
                    Buffer = nameBuffer
                };

                unicodeStringBuffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UnicodeString)));
                Marshal.StructureToPtr(unicodeName, unicodeStringBuffer, false);

                parent.DangerousAddRef(ref parentReferenceAdded);
                ObjectAttributes objectAttributes = new ObjectAttributes
                {
                    Length = Marshal.SizeOf(typeof(ObjectAttributes)),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = unicodeStringBuffer,
                    Attributes = ObjCaseInsensitive,
                    SecurityDescriptor = IntPtr.Zero,
                    SecurityQualityOfService = IntPtr.Zero
                };

                IoStatusBlock ioStatus;
                IntPtr rawHandle;
                uint desiredAccess = GetDeleteChildDesiredAccess(
                    includeListDirectory,
                    includeWriteAttributes);

                int status = NtCreateFile(
                    out rawHandle,
                    desiredAccess,
                    ref objectAttributes,
                    out ioStatus,
                    IntPtr.Zero,
                    0,
                    // 父目录句柄已经把解析固定在原目录对象上；允许共享写入和删除不会
                    // 跟随后来替换的路径，只会让现有句柄继续操作最初打开的对象。
                    SafeTraversalShare,
                    FileOpen,
                    FileSynchronousIoNonAlert | FileOpenReparsePoint,
                    IntPtr.Zero,
                    0);

                if (status < 0)
                {
                    int error = unchecked((int)RtlNtStatusToDosError(status));
                    if (IsMissingError(error))
                    {
                        return null;
                    }

                    throw new Win32Exception(error, "无法安全打开目录子项：" + displayPath);
                }

                if (rawHandle == IntPtr.Zero || rawHandle == new IntPtr(-1))
                {
                    throw new IOException("系统为目录子项返回了无效句柄：" + displayPath);
                }

                return new SafeFileHandle(rawHandle, true);
            }
            finally
            {
                if (parentReferenceAdded)
                {
                    parent.DangerousRelease();
                }
                if (unicodeStringBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(unicodeStringBuffer);
                }
                if (nameBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(nameBuffer);
                }
            }
        }

        private static uint GetDeleteChildDesiredAccess(
            bool includeListDirectory,
            bool includeWriteAttributes)
        {
            uint desiredAccess = SynchronizeAccess | DeleteAccess | FileReadAttributes;
            if (includeListDirectory) desiredAccess |= FileListDirectory;
            if (includeWriteAttributes) desiredAccess |= FileWriteAttributes;
            return desiredAccess;
        }

        internal static bool DeleteChildUsesListDirectoryAccessForTest(
            bool isDirectory,
            bool isReparsePoint)
        {
            return (GetDeleteChildDesiredAccess(isDirectory && !isReparsePoint, false) &
                FileListDirectory) != 0;
        }

        private static List<DeleteDirectoryEntry> FindChildEntries(
            SafeFileHandle directory,
            string displayPath)
        {
            List<DeleteDirectoryEntry> entries = new List<DeleteDirectoryEntry>();
            IntPtr buffer = Marshal.AllocHGlobal(DirectoryQueryBufferSize);
            try
            {
                FileInfoByHandleClass infoClass = FileInfoByHandleClass.FileIdBothDirectoryRestartInfo;
                while (true)
                {
                    if (!GetFileInformationByHandleEx(directory, infoClass, buffer, DirectoryQueryBufferSize))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == ErrorNoMoreFiles)
                        {
                            return entries;
                        }

                        throw new Win32Exception(error, "无法通过目录句柄枚举内容：" + displayPath);
                    }

                    int entryOffset = 0;
                    while (true)
                    {
                        if (entryOffset < 0
                            || entryOffset > DirectoryQueryBufferSize - FileIdBothDirectoryInfoFileNameOffset)
                        {
                            throw new IOException("目录枚举返回了越界的条目偏移：" + displayPath);
                        }

                        uint nextEntryOffset = unchecked((uint)Marshal.ReadInt32(buffer, entryOffset));
                        uint fileAttributes = unchecked((uint)Marshal.ReadInt32(
                            buffer,
                            entryOffset + FileIdBothDirectoryInfoFileAttributesOffset));
                        uint fileNameLength = unchecked((uint)Marshal.ReadInt32(
                            buffer,
                            entryOffset + FileIdBothDirectoryInfoFileNameLengthOffset));
                        int maximumFileNameLength = DirectoryQueryBufferSize
                            - entryOffset
                            - FileIdBothDirectoryInfoFileNameOffset;

                        if ((fileNameLength & 1) != 0
                            || fileNameLength > maximumFileNameLength)
                        {
                            throw new IOException("目录枚举返回了损坏的文件名数据：" + displayPath);
                        }

                        string name = Marshal.PtrToStringUni(
                            IntPtr.Add(buffer, entryOffset + FileIdBothDirectoryInfoFileNameOffset),
                            checked((int)(fileNameLength / sizeof(char))));
                        if (name != "." && name != "..")
                        {
                            entries.Add(new DeleteDirectoryEntry(name, fileAttributes));
                        }

                        if (nextEntryOffset == 0)
                        {
                            break;
                        }

                        if (nextEntryOffset < FileIdBothDirectoryInfoFileNameOffset
                            || nextEntryOffset > maximumFileNameLength)
                        {
                            throw new IOException("目录枚举返回了损坏的偏移数据：" + displayPath);
                        }

                        entryOffset += checked((int)nextEntryOffset);
                    }

                    infoClass = FileInfoByHandleClass.FileIdBothDirectoryInfo;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private sealed class DeleteDirectoryEntry
        {
            internal DeleteDirectoryEntry(string name, uint attributes)
            {
                Name = name;
                IsTraversableDirectory = (attributes & FileAttributeDirectory) != 0 &&
                    (attributes & FileAttributeReparsePoint) == 0;
                MayRequireAttributeWrite = (attributes & FileAttributeReadOnly) != 0;
            }

            internal string Name { get; private set; }
            internal bool IsTraversableDirectory { get; private set; }
            internal bool MayRequireAttributeWrite { get; private set; }
        }

        private static FileAttributeTagInfo GetAttributeTagInfo(SafeFileHandle handle, string displayPath)
        {
            FileAttributeTagInfo info;
            if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileAttributeTagInfo,
                out info,
                Marshal.SizeOf(typeof(FileAttributeTagInfo))))
            {
                ThrowLastError("无法读取文件属性", displayPath);
            }

            return info;
        }

        private static void DeleteOpenedEntry(SafeFileHandle handle, string displayPath, uint attributes)
        {
            if ((attributes & FileAttributeReadOnly) != 0)
            {
                ClearReadOnlyAttribute(handle, displayPath);
            }

            FileDispositionInfo disposition = new FileDispositionInfo { DeleteFile = 1 };
            if (!SetFileInformationByHandle(
                handle,
                FileInfoByHandleClass.FileDispositionInfo,
                ref disposition,
                Marshal.SizeOf(typeof(FileDispositionInfo))))
            {
                ThrowLastError("无法删除文件系统对象", displayPath);
            }
        }

        private static void ClearReadOnlyAttribute(SafeFileHandle handle, string displayPath)
        {
            FileBasicInfo basicInfo;
            if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileBasicInfo,
                out basicInfo,
                Marshal.SizeOf(typeof(FileBasicInfo))))
            {
                ThrowLastError("无法读取只读文件属性", displayPath);
            }

            uint mutableAttributes = basicInfo.FileAttributes & MutableFileAttributeMask;
            basicInfo.FileAttributes = mutableAttributes == 0 ? FileAttributeNormal : mutableAttributes;
            if (!SetFileInformationByHandle(
                handle,
                FileInfoByHandleClass.FileBasicInfo,
                ref basicInfo,
                Marshal.SizeOf(typeof(FileBasicInfo))))
            {
                ThrowLastError("无法清除只读文件属性", displayPath);
            }
        }

        private static string TrimEndingDirectorySeparators(string path)
        {
            string root = Path.GetPathRoot(path);
            int minimumLength = string.IsNullOrEmpty(root) ? 0 : root.Length;
            int length = path.Length;
            while (length > minimumLength
                && (path[length - 1] == Path.DirectorySeparatorChar
                    || path[length - 1] == Path.AltDirectorySeparatorChar))
            {
                length--;
            }

            return length == path.Length ? path : path.Substring(0, length);
        }

        private static string ExpandShortPathAliases(string fullPath)
        {
            if (fullPath.IndexOf('~') < 0)
            {
                return fullPath;
            }

            string root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new IOException("无法确定包含 8.3 短名称的路径根目录：" + fullPath);
            }
            string[] components = fullPath.Substring(root.Length).Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            string expanded = root;
            foreach (string component in components)
            {
                expanded = Path.Combine(expanded, component);
                if (component.IndexOf('~') < 0)
                {
                    continue;
                }
                expanded = ExpandExistingShortPathPrefix(expanded, fullPath);
            }
            return TrimEndingDirectorySeparators(Path.GetFullPath(expanded));
        }

        private static string ExpandExistingShortPathPrefix(string prefix, string fullPath)
        {
            StringBuilder buffer = new StringBuilder(512);
            uint length = GetLongPathName(prefix, buffer, (uint)buffer.Capacity);
            if (length == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (IsMissingError(error))
                {
                    return prefix;
                }
                throw new Win32Exception(error, "无法展开待删除路径中的 8.3 短名称：" + fullPath);
            }
            if (length >= buffer.Capacity)
            {
                buffer.Capacity = checked((int)length + 1);
                length = GetLongPathName(prefix, buffer, (uint)buffer.Capacity);
                if (length == 0 || length >= buffer.Capacity)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "无法完整展开待删除路径中的 8.3 短名称：" + fullPath);
                }
            }
            return buffer.ToString();
        }

        private static string CombineForDisplay(string parent, string child)
        {
            return parent.EndsWith("\\", StringComparison.Ordinal)
                ? parent + child
                : parent + "\\" + child;
        }

        private static bool IsMissingError(int error)
        {
            return error == ErrorFileNotFound || error == ErrorPathNotFound;
        }

        internal static string ToExtendedPath(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return path;
            }
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return @"\\?\UNC\" + path.Substring(2);
            }
            return @"\\?\" + path;
        }

        internal static string GetStablePathForExistingPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("路径不能为空。", nameof(path));
            }

            string fullPath = Path.GetFullPath(path);
            using (SafeFileHandle handle = CreateFile(
                ToExtendedPath(fullPath),
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "无法打开路径并解析其物理位置：" + fullPath);
                }

                return GetStablePathFromHandle(handle);
            }
        }

        internal static string GetStablePathForPotentialPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("路径不能为空。", nameof(path));
            }

            string fullPath = TrimEndingDirectorySeparators(Path.GetFullPath(path));
            string existingPath = fullPath;
            List<string> missingComponents = new List<string>();
            while (true)
            {
                NativePathKind kind = GetPathKind(existingPath);
                if (kind != NativePathKind.Missing)
                {
                    if (kind != NativePathKind.Directory)
                    {
                        throw new IOException(
                            "路径的最近现有祖先不是普通目录：" + existingPath);
                    }
                    break;
                }

                string name = Path.GetFileName(existingPath);
                string parent = Path.GetDirectoryName(existingPath);
                if (string.IsNullOrWhiteSpace(name) ||
                    string.IsNullOrWhiteSpace(parent))
                {
                    throw new IOException(
                        "无法解析路径的现有物理祖先：" + fullPath);
                }
                missingComponents.Insert(0, name);
                existingPath = parent;
            }

            string stablePath = GetStablePathForExistingPath(existingPath);
            foreach (string component in missingComponents)
            {
                stablePath = Path.Combine(stablePath, component);
            }
            return Path.GetFullPath(stablePath);
        }

        internal static bool ReferToSameFile(SafeFileHandle first, SafeFileHandle second)
        {
            if (first == null || first.IsInvalid)
            {
                throw new ArgumentException("第一个文件句柄无效。", nameof(first));
            }
            if (second == null || second.IsInvalid)
            {
                throw new ArgumentException("第二个文件句柄无效。", nameof(second));
            }

            FileIdInfo firstIdentity;
            FileIdInfo secondIdentity;
            if (TryGetFileId(first, out firstIdentity) && TryGetFileId(second, out secondIdentity))
            {
                return firstIdentity.VolumeSerialNumber == secondIdentity.VolumeSerialNumber &&
                    firstIdentity.FileIdLow == secondIdentity.FileIdLow &&
                    firstIdentity.FileIdHigh == secondIdentity.FileIdHigh;
            }

            return string.Equals(
                GetStablePathFromHandle(first),
                GetStablePathFromHandle(second),
                StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryGetPersistentDirectoryIdentity(
            SafeFileHandle handle,
            out string identity)
        {
            identity = null;
            if (handle == null || handle.IsInvalid)
            {
                throw new ArgumentException("目录句柄无效。", nameof(handle));
            }
            FileIdInfo fileId;
            if (!TryGetFileId(handle, out fileId) ||
                fileId.VolumeSerialNumber == 0 ||
                fileId.FileIdLow == 0 && fileId.FileIdHigh == 0)
            {
                return false;
            }
            identity = string.Format(
                CultureInfo.InvariantCulture,
                "directory-identity|{0:x16}|{1:x16}{2:x16}",
                fileId.VolumeSerialNumber,
                fileId.FileIdHigh,
                fileId.FileIdLow);
            return true;
        }

        internal static string GetPersistentFileIdentity(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("文件路径不能为空。", nameof(path));
            }
            string fullPath = Path.GetFullPath(path);
            using (SafeFileHandle handle = CreateFile(
                ToExtendedPath(fullPath),
                FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "无法打开事务来源 anchor 文件：" + fullPath);
                }
                FileAttributeTagInfo info = GetAttributeTagInfo(handle, fullPath);
                if ((info.FileAttributes & FileAttributeDirectory) != 0 ||
                    (info.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "事务来源 anchor 必须是普通文件：" + fullPath);
                }
                return GetPersistentFileIdentity(handle, fullPath);
            }
        }

        internal static string GetPersistentFileIdentity(
            SafeFileHandle handle,
            string displayPath)
        {
            if (handle == null || handle.IsInvalid)
            {
                throw new ArgumentException("文件句柄无效。", nameof(handle));
            }
            FileIdInfo fileId;
            if (!TryGetFileId(handle, out fileId) ||
                fileId.VolumeSerialNumber == 0 ||
                fileId.FileIdLow == 0 && fileId.FileIdHigh == 0)
            {
                throw new IOException(
                    "当前文件系统无法提供可靠的文件身份：" + displayPath);
            }
            return string.Format(
                CultureInfo.InvariantCulture,
                "file-identity|{0:x16}|{1:x16}{2:x16}",
                fileId.VolumeSerialNumber,
                fileId.FileIdHigh,
                fileId.FileIdLow);
        }

        internal static bool IsPersistentFileIdentity(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            string[] parts = value.Split('|');
            ulong volume;
            ulong high;
            ulong low;
            return parts.Length == 3 &&
                string.Equals(parts[0], "file-identity", StringComparison.Ordinal) &&
                parts[1].Length == 16 &&
                parts[2].Length == 32 &&
                ulong.TryParse(
                    parts[1],
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out volume) &&
                ulong.TryParse(
                    parts[2].Substring(0, 16),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out high) &&
                ulong.TryParse(
                    parts[2].Substring(16, 16),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out low) &&
                volume != 0 &&
                (high != 0 || low != 0);
        }

        internal static void EnsurePersistentFileIdentity(
            string path,
            string expectedIdentity)
        {
            if (!IsPersistentFileIdentity(expectedIdentity))
            {
                throw new InvalidDataException("事务来源 anchor 身份格式无效：" + path);
            }
            string actualIdentity = GetPersistentFileIdentity(path);
            if (!string.Equals(
                actualIdentity,
                expectedIdentity,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "事务来源 anchor 已被替换，拒绝恢复该目录：" + path);
            }
        }

        private static void EnsurePersistentFileIdentity(
            SafeFileHandle handle,
            string displayPath,
            string expectedIdentity)
        {
            if (!IsPersistentFileIdentity(expectedIdentity))
            {
                throw new InvalidDataException("待删除文件身份格式无效：" + displayPath);
            }
            string actualIdentity = GetPersistentFileIdentity(handle, displayPath);
            if (!string.Equals(
                actualIdentity,
                expectedIdentity,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "待删除文件已被替换，拒绝删除：" + displayPath);
            }
        }

        internal static void DeleteFileIfSha256Matches(
            string path,
            string expectedSha256)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("待删除文件路径不能为空。", nameof(path));
            }
            if (!IsSha256(expectedSha256))
            {
                throw new InvalidDataException("待删除文件摘要格式无效：" + path);
            }

            string fullPath = Path.GetFullPath(path);
            SafeFileHandle handle = CreateFile(
                ToExtendedPath(fullPath),
                DeleteAccess | SynchronizeAccess | FileListDirectory |
                    FileReadAttributes | FileWriteAttributes,
                FileShareRead,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (IsMissingError(error))
                {
                    return;
                }
                throw new Win32Exception(error, "无法打开待删除状态文件：" + fullPath);
            }

            using (handle)
            using (FileStream stream = new FileStream(
                handle,
                FileAccess.Read,
                4096,
                false))
            using (SHA256 sha256 = SHA256.Create())
            {
                FileAttributeTagInfo info = GetAttributeTagInfo(handle, fullPath);
                if ((info.FileAttributes & FileAttributeDirectory) != 0 ||
                    (info.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "待删除状态文件被目录或重解析点替换：" + fullPath);
                }
                string actualSha256 = BitConverter.ToString(
                    sha256.ComputeHash(stream)).Replace("-", string.Empty);
                if (!string.Equals(
                    actualSha256,
                    expectedSha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "待删除状态文件已被替换，摘要不再匹配：" + fullPath);
                }
                DeleteOpenedEntry(handle, fullPath, info.FileAttributes);
            }
        }

        internal static string GetStablePathFromHandle(SafeFileHandle fileHandle)
        {
            if (fileHandle == null || fileHandle.IsInvalid) throw new ArgumentException("文件句柄无效。", nameof(fileHandle));
            StringBuilder path = new StringBuilder(1024);
            uint length = GetFinalPathNameByHandle(fileHandle, path, (uint)path.Capacity, 0);
            if (length >= (uint)path.Capacity)
            {
                path = new StringBuilder(checked((int)length + 1));
                length = GetFinalPathNameByHandle(fileHandle, path, (uint)path.Capacity, 0);
            }
            if (length == 0 || length >= (uint)path.Capacity)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法从文件句柄解析稳定路径。");
            }

            string value = path.ToString();
            if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                value = @"\\" + value.Substring(8);
            }
            else if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                value = value.Substring(4);
            }
            if (!Path.IsPathRooted(value)) throw new IOException("文件句柄的稳定路径不是绝对路径：" + value);
            return Path.GetFullPath(value);
        }

        private static bool TryGetFileId(SafeFileHandle handle, out FileIdInfo identity)
        {
            if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileIdInfo,
                out identity,
                Marshal.SizeOf(typeof(FileIdInfo))))
            {
                return false;
            }
            return identity.VolumeSerialNumber != 0 &&
                (identity.FileIdLow != 0 || identity.FileIdHigh != 0);
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64)
            {
                return false;
            }
            foreach (char character in value)
            {
                if (!((character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F')))
                {
                    return false;
                }
            }
            return true;
        }

        private static void ThrowLastError(string message, string path)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), message + "：" + path);
        }

        private enum FileInfoByHandleClass
        {
            FileBasicInfo = 0,
            FileDispositionInfo = 4,
            FileAttributeTagInfo = 9,
            FileIdBothDirectoryInfo = 10,
            FileIdBothDirectoryRestartInfo = 11,
            FileIdInfo = 18
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdInfo
        {
            public ulong VolumeSerialNumber;
            public ulong FileIdLow;
            public ulong FileIdHigh;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTagInfo
        {
            public uint FileAttributes;
            public uint ReparseTag;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileBasicInfo
        {
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public long ChangeTime;
            public uint FileAttributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInfo
        {
            public byte DeleteFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock
        {
            public IntPtr Status;
            public UIntPtr Information;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder filePath,
            uint filePathSize,
            uint flags);

        [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetLongPathName(
            string shortPath,
            StringBuilder longPath,
            uint longPathSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            FileInfoByHandleClass fileInformationClass,
            IntPtr fileInformation,
            int bufferSize);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            FileInfoByHandleClass fileInformationClass,
            out FileAttributeTagInfo fileInformation,
            int bufferSize);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            FileInfoByHandleClass fileInformationClass,
            out FileBasicInfo fileInformation,
            int bufferSize);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            FileInfoByHandleClass fileInformationClass,
            out FileIdInfo fileInformation,
            int bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            FileInfoByHandleClass fileInformationClass,
            ref FileDispositionInfo fileInformation,
            int bufferSize);

        [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            FileInfoByHandleClass fileInformationClass,
            ref FileBasicInfo fileInformation,
            int bufferSize);

        [DllImport("ntdll.dll")]
        private static extern int NtCreateFile(
            out IntPtr fileHandle,
            uint desiredAccess,
            ref ObjectAttributes objectAttributes,
            out IoStatusBlock ioStatusBlock,
            IntPtr allocationSize,
            uint fileAttributes,
            uint shareAccess,
            uint createDisposition,
            uint createOptions,
            IntPtr eaBuffer,
            uint eaLength);

        [DllImport("ntdll.dll")]
        private static extern uint RtlNtStatusToDosError(int status);
    }
}
