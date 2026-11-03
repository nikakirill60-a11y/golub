using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace BlitzLegacy.Sdk
{
    /// <summary>
    /// Files the tool writes before it knows whether to keep them: the package being
    /// packed, written next to its destination and moved into place once it is accepted,
    /// and the scratch package of <c>validate</c>. Whatever is still open when the tool
    /// ends or is interrupted with Ctrl+C is removed.
    /// </summary>
    internal static class TempFiles
    {
        private const string Extension = ".tmp";
        private static readonly object Gate = new object();
        private static readonly HashSet<string> Live = new HashSet<string>(StringComparer.Ordinal);
        private static bool _hooked;

        /// <summary>
        /// A new empty file in <paramref name="folder"/>, named <paramref name="prefix"/>, a
        /// random part and <c>.tmp</c>. It can be deleted while open, which is what an
        /// interruption does.
        /// </summary>
        public static FileStream Create(string folder, string prefix, bool deleteOnClose, out string path)
        {
            Hook();
            var options = deleteOnClose ? FileOptions.DeleteOnClose : FileOptions.None;
            for (var attempt = 0; ; attempt++)
            {
                path = Path.Combine(folder, prefix + RandomNumberGenerator.GetHexString(8, true) + Extension);
                try
                {
                    var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
                        FileShare.Read | FileShare.Delete, 81920, options);
                    lock (Gate)
                    {
                        Live.Add(path);
                    }

                    return stream;
                }
                catch (IOException) when (attempt < 4 && File.Exists(path))
                {
                    // Another name, then: CreateNew never takes over an existing file.
                }
            }
        }

        /// <summary>Removes a file that is not wanted after all.</summary>
        public static void Delete(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            Remove(path);
            lock (Gate)
            {
                Live.Remove(path);
            }
        }

        /// <summary>The file was moved into place: it is not temporary any more.</summary>
        public static void Keep(string path)
        {
            lock (Gate)
            {
                Live.Remove(path ?? string.Empty);
            }
        }

        public static void DeleteAll()
        {
            string[] paths;
            lock (Gate)
            {
                paths = new string[Live.Count];
                Live.CopyTo(paths);
                Live.Clear();
            }

            foreach (var path in paths)
            {
                Remove(path);
            }
        }

        private static void Remove(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception error) when (SourceFolder.IsFileError(error))
            {
                // Left for the system's temporary-file cleanup; nothing else can be done.
            }
        }

        private static void Hook()
        {
            lock (Gate)
            {
                if (_hooked)
                {
                    return;
                }

                _hooked = true;
            }

            Console.CancelKeyPress += delegate { DeleteAll(); };
            AppDomain.CurrentDomain.ProcessExit += delegate { DeleteAll(); };
        }
    }
}
