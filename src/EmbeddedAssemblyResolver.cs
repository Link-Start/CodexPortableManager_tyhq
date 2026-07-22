using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace CodexPortableManager
{
    internal static class EmbeddedAssemblyResolver
    {
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, EmbeddedAssembly> Dependencies =
            new Dictionary<string, EmbeddedAssembly>(StringComparer.OrdinalIgnoreCase)
            {
                { "Esprima", new EmbeddedAssembly("Esprima", "eb1d27fdf2f22394211c2120ddd9fb025f2928c62b3bf32d2da3654e8597cd1f") },
                { "System.Memory", new EmbeddedAssembly("System.Memory", "bf3fb84664f4097f1a8a9bc71a51dcf8cf1a905d4080a4d290da1730866e856f") },
                { "System.Buffers", new EmbeddedAssembly("System.Buffers", "accccfbe45d9f08ffeed9916e37b33e98c65be012cfff6e7fa7b67210ce1fefb") },
                { "System.Numerics.Vectors", new EmbeddedAssembly("System.Numerics.Vectors", "1d3ef8698281e7cf7371d1554afef5872b39f96c26da772210a33da041ba1183") },
                { "System.Runtime.CompilerServices.Unsafe", new EmbeddedAssembly("System.Runtime.CompilerServices.Unsafe", "66409f670315afe8610f17a4d3a1ee52d72b6a46c544cec97544e8385f90ad74") }
            };
        private static readonly Dictionary<string, Assembly> Loaded =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        private static bool initialized;

        internal static void Initialize()
        {
            lock (SyncRoot)
            {
                if (initialized) return;
                AppDomain.CurrentDomain.AssemblyResolve += Resolve;
                initialized = true;
            }
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string name;
            try { name = new AssemblyName(args.Name).Name; }
            catch { return null; }

            EmbeddedAssembly dependency;
            if (string.IsNullOrWhiteSpace(name) || !Dependencies.TryGetValue(name, out dependency))
            {
                return null;
            }

            lock (SyncRoot)
            {
                Assembly loaded;
                if (Loaded.TryGetValue(name, out loaded)) return loaded;
                foreach (Assembly candidate in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(candidate.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        Loaded[name] = candidate;
                        return candidate;
                    }
                }

                Assembly owner = typeof(EmbeddedAssemblyResolver).Assembly;
                string resourceName = "CodexPortableManager.Dependencies." + dependency.FileName + ".dll";
                using (Stream stream = owner.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        throw new FileNotFoundException("缺少嵌入式兼容解析器程序集资源。", resourceName);
                    }
                    if (stream.Length <= 0 || stream.Length > 4 * 1024 * 1024)
                    {
                        throw new InvalidDataException("嵌入式兼容解析器程序集大小异常：" + resourceName);
                    }
                    byte[] bytes = new byte[stream.Length];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read <= 0) throw new EndOfStreamException("嵌入式兼容解析器程序集读取不完整。");
                        offset += read;
                    }
                    string actual = Hash(bytes);
                    if (!string.Equals(actual, dependency.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "嵌入式兼容解析器程序集摘要异常：" + resourceName);
                    }
                    loaded = Assembly.Load(bytes);
                    Loaded[name] = loaded;
                    return loaded;
                }
            }
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(bytes)).Replace("-", string.Empty);
            }
        }

        private sealed class EmbeddedAssembly
        {
            internal EmbeddedAssembly(string fileName, string sha256)
            {
                FileName = fileName;
                Sha256 = sha256;
            }

            internal string FileName { get; private set; }
            internal string Sha256 { get; private set; }
        }
    }
}
