using System;
using System.IO;
using System.Reflection;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
#if !NETFRAMEWORK
using System.Runtime.Loader;
#endif

namespace PipeTagger
{
    /// <summary>
    /// Loads PipeTagger.Core.dll from disk without locking it.
    /// - The file is read into memory, so you can rebuild it while Revit is open.
    /// - Every click checks the file's timestamp; if it changed, the new version is loaded.
    /// - The "Reload" button forces a fresh load.
    ///
    /// Revit 2025+ (.NET 8/10): each version goes into its own unloadable AssemblyLoadContext.
    /// Revit 2021-2024 (.NET Framework 4.8): Assembly.Load(bytes) — old versions stay in memory
    /// until Revit closes (a few KB each), but the newest one is always the one used.
    /// </summary>
    internal static class HotLoader
    {
        private static Assembly _core;
        private static DateTime _loadedStamp;
#if !NETFRAMEWORK
        private static AssemblyLoadContext _context;
#endif

        public static int LoadCount { get; private set; }
        public static DateTime LoadedStamp => _loadedStamp.ToLocalTime();

        public static string CoreDir =>
            Path.Combine(Path.GetDirectoryName(typeof(HotLoader).Assembly.Location), "Core");

        public static string CorePath => Path.Combine(CoreDir, "PipeTagger.Core.dll");

        /// <summary>Returns the Core assembly, reloading it if the file on disk is newer.</summary>
        public static Assembly GetCore(bool force = false)
        {
            if (!File.Exists(CorePath))
                throw new FileNotFoundException("PipeTagger.Core.dll was not found.\nExpected at:\n" + CorePath);

            DateTime stamp = File.GetLastWriteTimeUtc(CorePath);
            if (!force && _core != null && stamp == _loadedStamp)
                return _core;

            _core = LoadWithRetry(CorePath);
            _loadedStamp = stamp;
            LoadCount++;
            return _core;
        }

        /// <summary>Creates the command class from Core and runs it.</summary>
        public static Result Run(string typeName, ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                Assembly core = GetCore();
                Type type = core.GetType(typeName, true);
                var cmd = (IExternalCommand)Activator.CreateInstance(type);
                return cmd.Execute(data, ref message, elements);
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                TaskDialog.Show("Pipe Tagger", "Could not run the tool:\n\n" + inner.Message);
                message = inner.Message;
                return Result.Failed;
            }
        }

        /// <summary>The build may still be writing the file: retry a few times.</summary>
        private static Assembly LoadWithRetry(string path)
        {
            string pdbPath = Path.ChangeExtension(path, ".pdb");
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    byte[] dll = File.ReadAllBytes(path);
                    byte[] pdb = File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : null;
                    return LoadBytes(dll, pdb);
                }
                catch (Exception) when (attempt < 5)
                {
                    Thread.Sleep(300);
                }
            }
        }

#if NETFRAMEWORK
        // ---------- Revit 2021-2024 ----------
        private static Assembly LoadBytes(byte[] dll, byte[] pdb) =>
            pdb != null ? Assembly.Load(dll, pdb) : Assembly.Load(dll);
#else
        // ---------- Revit 2025+ ----------
        private static Assembly LoadBytes(byte[] dll, byte[] pdb)
        {
            _context?.Unload();   // drop the previous version
            _context = new CoreLoadContext(CoreDir);
            using (var dllStream = new MemoryStream(dll))
            using (var pdbStream = pdb != null ? new MemoryStream(pdb) : null)
                return _context.LoadFromStream(dllStream, pdbStream);
        }

        /// <summary>
        /// Unloadable context for Core. Revit / .NET assemblies are NOT loaded here:
        /// returning null makes them resolve to the copies Revit already has.
        /// </summary>
        private sealed class CoreLoadContext : AssemblyLoadContext
        {
            private readonly string _dir;

            public CoreLoadContext(string dir) : base("PipeTagger.Core", isCollectible: true)
            {
                _dir = dir;
            }

            protected override Assembly Load(AssemblyName name)
            {
                string candidate = Path.Combine(_dir, name.Name + ".dll");
                if (File.Exists(candidate))
                    using (var s = new MemoryStream(File.ReadAllBytes(candidate)))
                        return LoadFromStream(s);
                return null;
            }
        }
#endif
    }
}
