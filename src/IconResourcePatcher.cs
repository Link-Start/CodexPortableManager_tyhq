using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace CodexPortableManager
{
    internal static class IconResourcePatcher
    {
        private const uint LoadLibraryAsDataFile = 0x00000002;
        private static readonly IntPtr RtIcon = new IntPtr(3);
        private static readonly IntPtr RtGroupIcon = new IntPtr(14);

        public static void CopyIcons(string sourceExe, string targetExe)
        {
            IconGroup sourceGroup = ReadFirstIconGroup(sourceExe);
            PatchAtomically(sourceGroup, targetExe);
        }

        public static void CopyIconsFromIco(string sourceIco, string targetExe)
        {
            PatchAtomically(ReadIco(sourceIco), targetExe);
        }

        internal static void ValidateIco(string sourceIco)
        {
            ReadIco(sourceIco);
        }

        private static void PatchAtomically(IconGroup sourceGroup, string targetExe)
        {
            string target = Path.GetFullPath(targetExe);
            if (!File.Exists(target)) throw new FileNotFoundException("没有找到待修改的 Codex 程序。", target);
            string temporary = target + ".icon-new-" + Guid.NewGuid().ToString("N");
            try
            {
                CopyFileDurably(target, temporary);
                WriteIconGroup(sourceGroup, temporary);
                IconGroup verified = ReadFirstIconGroup(temporary);
                if (!IconGroupsEqual(sourceGroup, verified))
                {
                    throw new InvalidDataException("临时 EXE 的图标资源写入后验证失败。");
                }
                File.Replace(temporary, target, null, true);
            }
            finally
            {
                if (File.Exists(temporary)) NativeFileSystem.DeleteFile(temporary);
            }
        }

        private static void CopyFileDurably(string source, string destination)
        {
            using (FileStream input = new FileStream(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan))
            using (FileStream output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.SequentialScan))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
        }

        private static void WriteIconGroup(IconGroup sourceGroup, string targetExe)
        {
            List<ResourceEntry> targetGroups = ReadResourceEntries(targetExe, RtGroupIcon);
            if (targetGroups.Count == 0)
            {
                targetGroups.Add(new ResourceEntry(new ResourceName(1), 0));
            }

            IntPtr update = BeginUpdateResource(targetExe, false);
            if (update == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开 Codex 程序的图标资源。");
            }

            bool discard = true;
            try
            {
                HashSet<ushort> languages = new HashSet<ushort>();
                foreach (ResourceEntry group in targetGroups)
                {
                    languages.Add(group.Language);
                }

                foreach (ushort language in languages)
                {
                    foreach (KeyValuePair<ushort, byte[]> icon in sourceGroup.Icons)
                    {
                        UpdateResourceBytes(update, RtIcon, new ResourceName(icon.Key), language, icon.Value);
                    }
                }

                foreach (ResourceEntry group in targetGroups)
                {
                    UpdateResourceBytes(update, RtGroupIcon, group.Name, group.Language, sourceGroup.GroupData);
                }

                if (!EndUpdateResource(update, false))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保存 Codex 图标资源。");
                }
                discard = false;
            }
            finally
            {
                if (discard)
                {
                    EndUpdateResource(update, true);
                }
            }
        }

        public static bool HaveSameIcons(string sourceExe, string targetExe)
        {
            try
            {
                IconGroup source = ReadFirstIconGroup(sourceExe);
                IconGroup target = ReadFirstIconGroup(targetExe);
                if (!ByteArraysEqual(source.GroupData, target.GroupData) || source.Icons.Count != target.Icons.Count)
                {
                    return false;
                }
                foreach (KeyValuePair<ushort, byte[]> icon in source.Icons)
                {
                    byte[] targetData;
                    if (!target.Icons.TryGetValue(icon.Key, out targetData) || !ByteArraysEqual(icon.Value, targetData))
                    {
                        return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool HaveSameIconsFromIco(string sourceIco, string targetExe)
        {
            try
            {
                return IconGroupsEqual(ReadIco(sourceIco), ReadFirstIconGroup(targetExe));
            }
            catch
            {
                return false;
            }
        }

        private static bool IconGroupsEqual(IconGroup source, IconGroup target)
        {
            if (!ByteArraysEqual(source.GroupData, target.GroupData) || source.Icons.Count != target.Icons.Count)
            {
                return false;
            }
            foreach (KeyValuePair<ushort, byte[]> icon in source.Icons)
            {
                byte[] targetData;
                if (!target.Icons.TryGetValue(icon.Key, out targetData) || !ByteArraysEqual(icon.Value, targetData))
                {
                    return false;
                }
            }
            return true;
        }

        private static IconGroup ReadIco(string sourceIco)
        {
            byte[] data = File.ReadAllBytes(sourceIco);
            if (data.Length < 6 || BitConverter.ToUInt16(data, 0) != 0 || BitConverter.ToUInt16(data, 2) != 1)
            {
                throw new InvalidDataException("官方托盘图标不是有效的 ICO 文件。");
            }
            ushort count = BitConverter.ToUInt16(data, 4);
            if (count == 0 || data.Length < 6 + count * 16)
            {
                throw new InvalidDataException("官方托盘图标目录不完整。");
            }
            byte[] groupData = new byte[6 + count * 14];
            Buffer.BlockCopy(data, 0, groupData, 0, 6);
            Dictionary<ushort, byte[]> icons = new Dictionary<ushort, byte[]>();
            for (int index = 0; index < count; index++)
            {
                int sourceOffset = 6 + index * 16;
                int targetOffset = 6 + index * 14;
                uint length = BitConverter.ToUInt32(data, sourceOffset + 8);
                uint offset = BitConverter.ToUInt32(data, sourceOffset + 12);
                if (length == 0 || offset > data.Length || length > data.Length - offset)
                {
                    throw new InvalidDataException("官方托盘图标包含无效图像偏移。");
                }
                ushort iconId = checked((ushort)(index + 1));
                Buffer.BlockCopy(data, sourceOffset, groupData, targetOffset, 12);
                Buffer.BlockCopy(BitConverter.GetBytes(iconId), 0, groupData, targetOffset + 12, 2);
                byte[] image = new byte[length];
                Buffer.BlockCopy(data, (int)offset, image, 0, (int)length);
                icons.Add(iconId, image);
            }
            return new IconGroup(groupData, icons);
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }
            for (int index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }
            return true;
        }

        private static IconGroup ReadFirstIconGroup(string sourceExe)
        {
            List<ResourceEntry> groups = ReadResourceEntries(sourceExe, RtGroupIcon);
            if (groups.Count == 0)
            {
                throw new InvalidDataException("管理器程序中没有可用的图标资源。");
            }

            IntPtr module = LoadLibraryEx(sourceExe, IntPtr.Zero, LoadLibraryAsDataFile);
            if (module == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取管理器图标资源。");
            }

            try
            {
                ResourceEntry group = groups[0];
                byte[] groupData = ReadResourceBytes(module, RtGroupIcon, group.Name, group.Language);
                if (groupData.Length < 6)
                {
                    throw new InvalidDataException("管理器图标组格式无效。");
                }

                ushort count = BitConverter.ToUInt16(groupData, 4);
                Dictionary<ushort, byte[]> icons = new Dictionary<ushort, byte[]>();
                for (int index = 0; index < count; index++)
                {
                    int offset = 6 + index * 14;
                    if (offset + 14 > groupData.Length)
                    {
                        throw new InvalidDataException("管理器图标组条目不完整。");
                    }
                    ushort iconId = BitConverter.ToUInt16(groupData, offset + 12);
                    if (!icons.ContainsKey(iconId))
                    {
                        icons.Add(iconId, ReadResourceBytes(module, RtIcon, new ResourceName(iconId), group.Language));
                    }
                }
                return new IconGroup(groupData, icons);
            }
            finally
            {
                FreeLibrary(module);
            }
        }

        private static List<ResourceEntry> ReadResourceEntries(string filePath, IntPtr type)
        {
            IntPtr module = LoadLibraryEx(filePath, IntPtr.Zero, LoadLibraryAsDataFile);
            if (module == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取程序资源：" + filePath);
            }

            List<ResourceEntry> entries = new List<ResourceEntry>();
            try
            {
                EnumResNameProc nameCallback = delegate (IntPtr moduleHandle, IntPtr resourceType, IntPtr resourceName, IntPtr parameter)
                {
                    ResourceName name = ResourceName.FromPointer(resourceName);
                    EnumResLangProc languageCallback = delegate (IntPtr languageModule, IntPtr languageType, IntPtr languageName, ushort language, IntPtr languageParameter)
                    {
                        entries.Add(new ResourceEntry(name, language));
                        return true;
                    };
                    EnumResourceLanguages(moduleHandle, resourceType, resourceName, languageCallback, IntPtr.Zero);
                    return true;
                };
                EnumResourceNames(module, type, nameCallback, IntPtr.Zero);
                return entries;
            }
            finally
            {
                FreeLibrary(module);
            }
        }

        private static byte[] ReadResourceBytes(IntPtr module, IntPtr type, ResourceName name, ushort language)
        {
            IntPtr namePointer = name.AllocatePointer();
            try
            {
                IntPtr resource = FindResourceEx(module, type, namePointer, language);
                if (resource == IntPtr.Zero && language != 0)
                {
                    resource = FindResourceEx(module, type, namePointer, 0);
                }
                if (resource == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "未找到图标资源。");
                }

                uint size = SizeofResource(module, resource);
                IntPtr loaded = LoadResource(module, resource);
                IntPtr data = LockResource(loaded);
                byte[] bytes = new byte[size];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                name.FreePointer(namePointer);
            }
        }

        private static void UpdateResourceBytes(IntPtr update, IntPtr type, ResourceName name, ushort language, byte[] data)
        {
            IntPtr namePointer = name.AllocatePointer();
            try
            {
                if (!UpdateResource(update, type, namePointer, language, data, (uint)data.Length))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "写入图标资源失败。");
                }
            }
            finally
            {
                name.FreePointer(namePointer);
            }
        }

        private sealed class IconGroup
        {
            public IconGroup(byte[] groupData, Dictionary<ushort, byte[]> icons)
            {
                GroupData = groupData;
                Icons = icons;
            }

            public byte[] GroupData { get; private set; }
            public Dictionary<ushort, byte[]> Icons { get; private set; }
        }

        private sealed class ResourceEntry
        {
            public ResourceEntry(ResourceName name, ushort language)
            {
                Name = name;
                Language = language;
            }

            public ResourceName Name { get; private set; }
            public ushort Language { get; private set; }
        }

        private sealed class ResourceName
        {
            public ResourceName(ushort id)
            {
                IsId = true;
                Id = id;
            }

            public ResourceName(string name)
            {
                Name = name;
            }

            public bool IsId { get; private set; }
            public ushort Id { get; private set; }
            public string Name { get; private set; }

            public static ResourceName FromPointer(IntPtr pointer)
            {
                ulong value = unchecked((ulong)pointer.ToInt64());
                return (value >> 16) == 0 ? new ResourceName((ushort)value) : new ResourceName(Marshal.PtrToStringUni(pointer));
            }

            public IntPtr AllocatePointer()
            {
                return IsId ? new IntPtr(Id) : Marshal.StringToHGlobalUni(Name);
            }

            public void FreePointer(IntPtr pointer)
            {
                if (!IsId && pointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(pointer);
                }
            }
        }

        private delegate bool EnumResNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
        private delegate bool EnumResLangProc(IntPtr module, IntPtr type, IntPtr name, ushort language, IntPtr parameter);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumResNameProc callback, IntPtr parameter);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EnumResourceLanguages(IntPtr module, IntPtr type, IntPtr name, EnumResLangProc callback, IntPtr parameter);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr FindResourceEx(IntPtr module, IntPtr type, IntPtr name, ushort language);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LockResource(IntPtr resource);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr BeginUpdateResource(string fileName, bool deleteExistingResources);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateResource(IntPtr update, IntPtr type, IntPtr name, ushort language, byte[] data, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EndUpdateResource(IntPtr update, bool discard);
    }
}
