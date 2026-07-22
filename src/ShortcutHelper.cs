using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace CodexPortableManager
{
    internal static class ShortcutHelper
    {
        private const int MaximumPathCharacters = 32768;
        private const int StgmRead = 0;
        private const int SlgpRawPath = 0x00000004;
        private const int MoveFileReplaceExisting = 0x00000001;
        private const int MoveFileWriteThrough = 0x00000008;

        private static readonly PropertyKey AppUserModelId = new PropertyKey(
            new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            5);

        public static void Create(
            string shortcutPath,
            string target,
            string arguments,
            string workingDirectory,
            string iconPath,
            string description,
            string appUserModelId)
        {
            if (string.IsNullOrWhiteSpace(shortcutPath))
            {
                throw new ArgumentException("快捷方式路径不能为空。", "shortcutPath");
            }
            if (string.IsNullOrWhiteSpace(target))
            {
                throw new ArgumentException("快捷方式目标不能为空。", "target");
            }

            string fullShortcutPath = Path.GetFullPath(shortcutPath);
            string fullTarget = Path.GetFullPath(target);
            string shortcutDirectory = Path.GetDirectoryName(fullShortcutPath);
            if (string.IsNullOrWhiteSpace(shortcutDirectory))
            {
                throw new InvalidDataException("无法确定快捷方式所在目录：" + shortcutPath);
            }

            Directory.CreateDirectory(shortcutDirectory);
            string temporaryPath = Path.Combine(
                shortcutDirectory,
                "." + Path.GetFileName(fullShortcutPath) + "." + Guid.NewGuid().ToString("N") + ".tmp.lnk");

            try
            {
                SaveShortcut(
                    temporaryPath,
                    fullTarget,
                    arguments,
                    workingDirectory,
                    iconPath,
                    description,
                    appUserModelId);

                // 同目录 MoveFileEx 能在替换失败时保留旧快捷方式，避免留下半写入的 .lnk。
                if (!MoveFileEx(
                    temporaryPath,
                    fullShortcutPath,
                    MoveFileReplaceExisting | MoveFileWriteThrough))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "无法原子替换快捷方式：" + fullShortcutPath);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        NativeFileSystem.DeleteFile(temporaryPath);
                    }
                }
                catch
                {
                    // 临时文件清理失败不能覆盖创建快捷方式时的原始异常。
                }
            }
        }

        public static string GetTarget(string shortcutPath)
        {
            if (string.IsNullOrWhiteSpace(shortcutPath))
            {
                throw new ArgumentException("快捷方式路径不能为空。", "shortcutPath");
            }

            object shellLinkObject = LoadShortcut(shortcutPath);
            IntPtr buffer = IntPtr.Zero;
            try
            {
                buffer = Marshal.AllocHGlobal(MaximumPathCharacters * sizeof(char));
                for (int index = 0; index < MaximumPathCharacters; index++)
                {
                    Marshal.WriteInt16(buffer, index * sizeof(char), 0);
                }

                ((IShellLinkW)shellLinkObject).GetPath(
                    buffer,
                    MaximumPathCharacters,
                    IntPtr.Zero,
                    SlgpRawPath);
                string target = Marshal.PtrToStringUni(buffer);
                if (string.IsNullOrWhiteSpace(target))
                {
                    throw new InvalidDataException("快捷方式没有可解析的文件目标：" + shortcutPath);
                }
                return target;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
                Marshal.FinalReleaseComObject(shellLinkObject);
            }
        }

        public static bool TryGetTarget(string shortcutPath, out string target, out string error)
        {
            try
            {
                target = GetTarget(shortcutPath);
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                target = null;
                error = exception.Message;
                return false;
            }
        }

        public static string GetAppUserModelId(string shortcutPath)
        {
            if (string.IsNullOrWhiteSpace(shortcutPath))
            {
                throw new ArgumentException("快捷方式路径不能为空。", "shortcutPath");
            }

            object shellLinkObject = LoadShortcut(shortcutPath);
            PropertyVariant value = new PropertyVariant();
            try
            {
                PropertyKey appIdKey = AppUserModelId;
                ((IPropertyStore)shellLinkObject).GetValue(ref appIdKey, out value);
                return value.GetString();
            }
            finally
            {
                value.Clear();
                Marshal.FinalReleaseComObject(shellLinkObject);
            }
        }

        private static void SaveShortcut(
            string shortcutPath,
            string target,
            string arguments,
            string workingDirectory,
            string iconPath,
            string description,
            string appUserModelId)
        {
            object shellLinkObject = new ShellLink();
            try
            {
                IShellLinkW shellLink = (IShellLinkW)shellLinkObject;
                shellLink.SetPath(target);
                shellLink.SetArguments(arguments ?? string.Empty);
                shellLink.SetWorkingDirectory(
                    string.IsNullOrWhiteSpace(workingDirectory)
                        ? string.Empty
                        : Path.GetFullPath(workingDirectory));
                shellLink.SetIconLocation(
                    string.IsNullOrWhiteSpace(iconPath) ? target : Path.GetFullPath(iconPath),
                    0);
                shellLink.SetDescription(description ?? string.Empty);

                if (!string.IsNullOrWhiteSpace(appUserModelId))
                {
                    IPropertyStore propertyStore = (IPropertyStore)shellLinkObject;
                    PropertyVariant value = PropertyVariant.FromString(appUserModelId);
                    try
                    {
                        PropertyKey appIdKey = AppUserModelId;
                        propertyStore.SetValue(ref appIdKey, ref value);
                        propertyStore.Commit();
                    }
                    finally
                    {
                        value.Clear();
                    }
                }

                ((IPersistFile)shellLinkObject).Save(shortcutPath, true);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shellLinkObject);
            }
        }

        private static object LoadShortcut(string shortcutPath)
        {
            string fullPath = Path.GetFullPath(shortcutPath);
            object shellLinkObject = new ShellLink();
            try
            {
                ((IPersistFile)shellLinkObject).Load(fullPath, StgmRead);
                return shellLinkObject;
            }
            catch
            {
                Marshal.FinalReleaseComObject(shellLinkObject);
                throw;
            }
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private sealed class ShellLink
        {
        }

        [ComImport]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath(IntPtr file, int maximumPath, IntPtr findData, uint flags);
            void GetIDList(out IntPtr itemIdList);
            void SetIDList(IntPtr itemIdList);
            void GetDescription(IntPtr name, int maximumName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory(IntPtr directory, int maximumPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
            void GetArguments(IntPtr arguments, int maximumArguments);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCommand);
            void SetShowCmd(int showCommand);
            void GetIconLocation(IntPtr iconPath, int iconPathLength, out int iconIndex);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        }

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropertyVariant value);
            void SetValue(ref PropertyKey key, ref PropertyVariant value);
            void Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey
        {
            public PropertyKey(Guid formatId, uint propertyId)
            {
                FormatId = formatId;
                PropertyId = propertyId;
            }

            public Guid FormatId;
            public uint PropertyId;
        }

        // PROPVARIANT 在 64 位进程中为 24 字节；显式保留完整联合体，避免 COM 写越界。
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropertyVariant
        {
            [FieldOffset(0)]
            private ushort valueType;

            [FieldOffset(8)]
            private IntPtr pointerValue;

            [FieldOffset(16)]
            private IntPtr reservedValue;

            public static PropertyVariant FromString(string value)
            {
                return new PropertyVariant
                {
                    valueType = 31,
                    pointerValue = Marshal.StringToCoTaskMemUni(value)
                };
            }

            public void Clear()
            {
                if (valueType != 0)
                {
                    PropVariantClear(ref this);
                }
            }

            public string GetString()
            {
                return valueType == 31 && pointerValue != IntPtr.Zero
                    ? Marshal.PtrToStringUni(pointerValue)
                    : null;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(
            string existingFileName,
            string newFileName,
            int flags);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropertyVariant value);
    }
}
