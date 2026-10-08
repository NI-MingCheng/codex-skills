using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace EncodingChecker.Cli
{
    internal static class SafeFiles
    {
        internal const string BackupMarker = ".encodingchecker-backup";
        internal const string BackupMarkerText = "EncodingChecker.Cli backup directory v1\n";
        internal static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 100 };

        internal static string Full(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.");
            if (path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal))
                throw new ArgumentException("Device and extended-length paths are unsupported.");
            return Path.GetFullPath(path);
        }

        internal static string Root(string path)
        {
            string full = Full(path);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
            NoReparse(full);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        internal static void NoReparse(string path)
        {
            string current = Full(path);
            while (!String.IsNullOrEmpty(current))
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Reparse points are not allowed: " + current);
                }
                string parent = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (String.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
        }

        internal static string Relative(string root, string full)
        {
            string prefix = Root(root);
            string path = Full(full);
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path escapes root: " + full);
            return path.Substring(prefix.Length).Replace('\\', '/');
        }

        internal static string Within(string root, string relative, bool checkReparse = true)
        {
            if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(":"))
                throw new InvalidDataException("Expected a relative file path: " + relative);
            string normalized = relative.Replace('\\', '/');
            if (normalized.Split('/').Any(part => part == ".." || part == "." || part.Length == 0 || part.EndsWith(" ", StringComparison.Ordinal) || part.EndsWith(".", StringComparison.Ordinal)))
                throw new InvalidDataException("Unsafe relative path: " + relative);
            string prefix = Root(root);
            string full = Full(Path.Combine(prefix, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path escapes root: " + relative);
            if (checkReparse) NoReparse(full);
            return full;
        }

        internal static string Hash(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        internal static void CheckHash(string value)
        {
            if (value == null || value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("Invalid SHA-256 in manifest.");
        }

        internal static byte[] ReadChecked(string path, string expected)
        {
            NoReparse(path);
            CheckHash(expected);
            if (!File.Exists(path)) throw new FileNotFoundException("Source file is missing.", path);
            byte[] bytes = File.ReadAllBytes(path);
            if (!String.Equals(Hash(bytes), expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException("SHA-256 changed: " + path);
            return bytes;
        }

        internal static T ReadJson<T>(string path)
        {
            path = Full(path);
            NoReparse(path);
            byte[] bytes = File.ReadAllBytes(path);
            int offset;
            string bom = Detection.Bom(bytes, out offset);
            if (bom != null && bom != "utf-8") throw new InvalidDataException("JSON must be UTF-8: " + path);
            string text = Detection.Utf8.GetString(bytes, offset, bytes.Length - offset);
            T value = Json.Deserialize<T>(text);
            if (value == null) throw new InvalidDataException("Empty JSON object: " + path);
            return value;
        }

        internal static void CheckWritable(string path)
        {
            path = Full(path);
            NoReparse(path);
            if (Directory.Exists(path)) throw new IOException("Expected file, found directory: " + path);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                throw new IOException("Read-only file is not writable; its attribute will not be cleared: " + path);
        }

        internal static void CreateDirectory(string path)
        {
            path = Full(path);
            NoReparse(path);
            if (File.Exists(path)) throw new IOException("Expected directory, found file: " + path);
            Directory.CreateDirectory(path);
            NoReparse(path);
        }

        internal static void AtomicWrite(string path, byte[] bytes, string expectedHash = null)
        {
            path = Full(path);
            CheckWritable(path);
            string parent = Path.GetDirectoryName(path);
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
            string temporary = Path.Combine(parent, ".encodingchecker-" + Guid.NewGuid().ToString("N") + ".tmp");
            FileAttributes? attributes = File.Exists(path) ? (FileAttributes?)File.GetAttributes(path) : null;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (attributes.HasValue) File.SetAttributes(temporary, attributes.Value);
                CheckWritable(path);
                if (expectedHash != null) ReadChecked(path, expectedHash);
                // Do not recreate a disappeared source or replace an unexpectedly appeared new target.
                if (attributes.HasValue || expectedHash != null) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                if (!File.ReadAllBytes(path).SequenceEqual(bytes)) throw new IOException("Post-write byte verification failed: " + path);
                if (attributes.HasValue)
                {
                    // File.Replace may set Archive. Restore the original metadata only after the byte write.
                    if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != (attributes.Value & FileAttributes.ReadOnly))
                        throw new IOException("Read-only attribute changed during replacement: " + path);
                    if (File.GetAttributes(path) != attributes.Value) File.SetAttributes(path, attributes.Value);
                    if (File.GetAttributes(path) != attributes.Value) throw new IOException("Post-write attribute verification failed: " + path);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch (Exception cleanup) { Console.Error.WriteLine("Temporary-file cleanup failed: " + temporary + ": " + cleanup.Message); }
                }
            }
        }

        internal static void WriteJson(string path, object value, string expectedHash = null)
        {
            AtomicWrite(path, Detection.Utf8.GetBytes(Json.Serialize(value) + "\n"), expectedHash);
        }

        internal static Snapshot ReadSnapshot(string path)
        {
            Snapshot snapshot = ReadJson<Snapshot>(path);
            if (snapshot.schema_version != 1 || snapshot.files == null || snapshot.kind != "encodingchecker-snapshot")
                throw new InvalidDataException("Unsupported or incomplete snapshot.");
            if (snapshot.state == "converted" || snapshot.state == "stale") throw new InvalidDataException("Snapshot is stale; scan again.");
            snapshot.root = Root(snapshot.root);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FileRecord file in snapshot.files)
            {
                if (file == null) throw new InvalidDataException("Null file entry in snapshot.");
                string full = Within(snapshot.root, file.path, file.status != "excluded");
                if (!seen.Add(full)) throw new InvalidDataException("Duplicate path in snapshot: " + file.path);
                file.path = Relative(snapshot.root, full);
                if (file.evidence == null) file.evidence = new List<string>();
            }
            return snapshot;
        }
    }
}
