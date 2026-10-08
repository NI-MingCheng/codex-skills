using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EncodingChecker.Cli
{
    internal static class Program
    {
        private const string Version = "1.0.0";
        internal static int Main(string[] args)
        {
            // Per-process .NET switches; do not change machine registry or console code pages.
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Detection.Utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), Detection.Utf8) { AutoFlush = true });
            try
            {
                object output;
                int exitCode;
                if (args.Length == 1 && args[0] == "--version") { output = new { version = Version, schema_version = 1 }; exitCode = 0; }
                else if (args.Length == 2 && args[1] == "--help")
                {
                    output = new { version = Version, command = args[0], usage = Usage(args[0]),
                        encodings = new[] { "utf-8", "ascii", "gbk", "gb18030", "windows-1252", "big5", "shift-jis", "utf-16-le", "utf-16-be", "utf-32-le", "utf-32-be" },
                        exit_codes = new { success = 0, error = 1, unresolved = 2 } };
                    exitCode = 0;
                }
                else if (args.Length == 0 || (args.Length == 1 && (args[0] == "--help" || args[0] == "help")))
                {
                    output = new { version = Version, commands = new[] {
                        "probe --path FILE [--encoding CODEC]",
                        "scan --root DIR [--output JSON] [--default-encoding CODEC] [--policy JSON] [--exclude dir1,dir2]",
                        "confirm --snapshot JSON --path RELATIVE --encoding CODEC [--reason TEXT]",
                        "convert --snapshot JSON [--backup-dir DIR] [--apply]",
                        "rollback --log JSON --apply" },
                        encodings = new[] { "utf-8", "ascii", "gbk", "gb18030", "windows-1252", "big5", "shift-jis", "utf-16-le", "utf-16-be", "utf-32-le", "utf-32-be" },
                        exit_codes = new { success = 0, error = 1, unresolved = 2 },
                        note = "Heuristics are suggestions only. convert defaults to dry-run. Old snapshots are stale after conversion." };
                    exitCode = 0;
                }
                else
                {
                    var options = new Options(args.Skip(1).ToArray());
                    switch (args[0])
                    {
                        case "probe":
                            options.Allow("path", "encoding");
                            string path = SafeFiles.Full(options.Require("path"));
                            var probe = Detection.Probe(path, path, options.Get("encoding"), null, true);
                            output = probe;
                            exitCode = probe.status == "error" ? 1 : IsUnresolved(probe) ? 2 : 0;
                            break;
                        case "scan":
                            options.Allow("root", "output", "default-encoding", "policy", "exclude");
                            Snapshot snapshot = Scan(options);
                            output = snapshot;
                            exitCode = snapshot.files.Any(f => f.status == "error") ? 1 : snapshot.files.Any(IsUnresolved) ? 2 : 0;
                            break;
                        case "confirm":
                            options.Allow("snapshot", "path", "encoding", "reason");
                            output = Confirm(options);
                            exitCode = 0;
                            break;
                        case "convert":
                            options.Allow("snapshot", "backup-dir", "apply");
                            output = Transactions.Convert(options, out exitCode);
                            break;
                        case "rollback":
                            options.Allow("log", "apply");
                            if (!options.Has("apply")) throw new ArgumentException("rollback requires --apply.");
                            output = Transactions.Rollback(options.Require("log"), out exitCode);
                            break;
                        default: throw new ArgumentException("Unknown command: " + args[0] + ". Use --help.");
                    }
                }
                Console.WriteLine(SafeFiles.Json.Serialize(output));
                return exitCode;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
                Console.WriteLine(SafeFiles.Json.Serialize(new { schema_version = 1, success = false, error = error.Message, error_type = error.GetType().Name }));
                return 1;
            }
        }

        private static string Usage(string command)
        {
            switch (command)
            {
                case "probe": return "probe --path FILE [--encoding CODEC]";
                case "scan": return "scan --root DIR [--output JSON] [--default-encoding CODEC] [--policy JSON] [--exclude dir1,dir2]";
                case "confirm": return "confirm --snapshot JSON --path RELATIVE --encoding CODEC [--reason TEXT]";
                case "convert": return "convert --snapshot JSON [--backup-dir DIR] [--apply]";
                case "rollback": return "rollback --log JSON --apply";
                default: throw new ArgumentException("Unknown command: " + command);
            }
        }

        private static bool IsUnresolved(FileRecord record)
        {
            return record.status == "ambiguous" || record.status == "needs-confirmation" || record.status == "unknown" || record.status == "error";
        }

        private static Snapshot Scan(Options options)
        {
            string root = SafeFiles.Root(options.Require("root"));
            string output = options.Get("output") == null ? null : SafeFiles.Full(options.Get("output"));
            if (output != null) SafeFiles.CheckWritable(output);
            string policyPath = options.Get("policy") == null ? null : SafeFiles.Full(options.Get("policy"));
            if (output != null && String.Equals(output, policyPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--output cannot overwrite the policy input.");
            Policy policy = policyPath == null ? new Policy() : SafeFiles.ReadJson<Policy>(policyPath);
            string fallback = options.Get("default-encoding") ?? policy.default_encoding;
            if (fallback != null) fallback = Detection.Canonical(fallback);
            var explicitFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in policy.files ?? new Dictionary<string, string>())
            {
                string path = SafeFiles.Within(root, entry.Key);
                if (explicitFiles.ContainsKey(path)) throw new InvalidDataException("Duplicate policy path: " + entry.Key);
                explicitFiles.Add(path, Detection.Canonical(entry.Value));
            }
            string exclusions = options.Get("exclude") ?? ".git,.svn,bin,obj,build,dist,node_modules,.venv,venv,vendor,third_party,generated";
            var excludedNames = new HashSet<string>(exclusions.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            if (excludedNames.Any(n => n.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || n == "." || n == ".."))
                throw new ArgumentException("--exclude accepts directory names, not paths.");
            var snapshot = new Snapshot { schema_version = 1, root = root, files = new List<FileRecord>() };
            Walk(root, root, output, policyPath, fallback, explicitFiles, excludedNames, snapshot.files);
            snapshot.files = snapshot.files.OrderBy(f => f.path, StringComparer.OrdinalIgnoreCase).ToList();
            if (output != null)
            {
                SafeFiles.CreateDirectory(Path.GetDirectoryName(output));
                SafeFiles.WriteJson(output, snapshot);
            }
            return snapshot;
        }

        private static void Walk(string root, string directory, string output, string policyPath, string fallback,
            Dictionary<string, string> explicitFiles, HashSet<string> excludedNames, List<FileRecord> files)
        {
            string[] entries;
            try { SafeFiles.NoReparse(directory); entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception error)
            {
                string relative = String.Equals(directory, root, StringComparison.OrdinalIgnoreCase) ? "scan-root-error" : SafeFiles.Relative(root, directory);
                files.Add(new FileRecord { path = relative, status = "error", evidence = new List<string> { "directory-read-error: " + error.Message } });
                Console.Error.WriteLine("scan: " + directory + ": " + error.Message);
                return;
            }
            foreach (string path in entries)
            {
                string relative = SafeFiles.Relative(root, path);
                try
                {
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        files.Add(new FileRecord { path = relative, status = "excluded", evidence = new List<string> { "reparse-point-not-followed" } });
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (excludedNames.Contains(Path.GetFileName(path)) ||
                            String.Equals(relative, "docs/encoding-backups", StringComparison.OrdinalIgnoreCase) || IsBackupDirectory(path))
                        {
                            files.Add(new FileRecord { path = relative, status = "excluded", evidence = new List<string> { "directory-excluded" } });
                            continue;
                        }
                        Walk(root, path, output, policyPath, fallback, explicitFiles, excludedNames, files);
                    }
                    else if (String.Equals(path, output, StringComparison.OrdinalIgnoreCase) || String.Equals(path, policyPath, StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(new FileRecord { path = relative, status = "excluded", evidence = new List<string> { "snapshot-output-or-policy-input" } });
                    }
                    else
                    {
                        string encoding;
                        explicitFiles.TryGetValue(path, out encoding);
                        files.Add(Detection.Probe(path, relative, encoding, fallback, false));
                    }
                }
                catch (Exception error)
                {
                    files.Add(new FileRecord { path = relative, status = "error", evidence = new List<string> { error.GetType().Name + ": " + error.Message } });
                    Console.Error.WriteLine("scan: " + relative + ": " + error.Message);
                }
            }
        }

        private static bool IsBackupDirectory(string path)
        {
            string marker = Path.Combine(path, SafeFiles.BackupMarker);
            if (File.Exists(marker))
            {
                try
                {
                    SafeFiles.NoReparse(marker);
                    byte[] expected = Detection.Utf8.GetBytes(SafeFiles.BackupMarkerText);
                    if (new FileInfo(marker).Length == expected.Length && File.ReadAllBytes(marker).SequenceEqual(expected)) return true;
                }
                catch (Exception error) { Console.Error.WriteLine("Backup marker could not be verified: " + marker + ": " + error.Message); }
            }
            string log = Path.Combine(path, "conversion-log.json");
            if (!File.Exists(log)) return false;
            try
            {
                ConversionLog value = SafeFiles.ReadJson<ConversionLog>(log);
                return value.kind == "encodingchecker-conversion-log" && value.schema_version == 1 &&
                    String.Equals(SafeFiles.Full(value.backup_dir).TrimEnd('\\', '/'), SafeFiles.Full(path).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Backup-directory marker was not accepted: " + log + ": " + error.Message);
                return false;
            }
        }

        private static object Confirm(Options options)
        {
            string snapshotPath = SafeFiles.Full(options.Require("snapshot"));
            byte[] snapshotBytes = File.ReadAllBytes(snapshotPath);
            Snapshot snapshot = SafeFiles.ReadSnapshot(snapshotPath);
            string path = SafeFiles.Within(snapshot.root, options.Require("path"));
            string relative = SafeFiles.Relative(snapshot.root, path);
            FileRecord record = snapshot.files.SingleOrDefault(f => String.Equals(f.path, relative, StringComparison.OrdinalIgnoreCase));
            if (record == null) throw new ArgumentException("Path is not present in snapshot.");
            if (record.status == "binary" || record.status == "excluded") throw new ArgumentException("Binary or excluded entries cannot be confirmed.");
            byte[] bytes = SafeFiles.ReadChecked(path, record.sha256);
            string canonical = Detection.Canonical(options.Require("encoding"));
            string text = Detection.Decode(bytes, canonical);
            if (text.Any(c => Char.IsControl(c) && c != '\t' && c != '\r' && c != '\n' && c != '\f'))
                throw new InvalidDataException("Selected encoding produces non-text control characters.");
            record.encoding = canonical;
            record.status = "confirmed";
            record.eol = Detection.Eol(text);
            record.suggested_encoding = null;
            record.confidence = null;
            int size;
            record.bom = Detection.Bom(bytes, out size);
            record.evidence.Add("explicit-snapshot-confirmation");
            if (options.Get("reason") != null) record.evidence.Add("reason: " + options.Get("reason"));
            SafeFiles.ReadChecked(path, record.sha256);
            SafeFiles.WriteJson(snapshotPath, snapshot, SafeFiles.Hash(snapshotBytes));
            return new { schema_version = 1, operation = "confirm", success = true, snapshot = snapshotPath, file = record, source_modified = false };
        }
    }

    internal sealed class Options
    {
        private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
        internal Options(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected option, found: " + args[i]);
                string key = args[i].Substring(2);
                if (values.ContainsKey(key)) throw new ArgumentException("Duplicate option: --" + key);
                if (key == "apply") values.Add(key, "true");
                else
                {
                    if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Missing value for --" + key);
                    values.Add(key, args[i]);
                }
            }
        }
        internal void Allow(params string[] names)
        {
            foreach (string key in values.Keys) if (!names.Contains(key)) throw new ArgumentException("Unknown option: --" + key);
        }
        internal string Get(string key) { string value; return values.TryGetValue(key, out value) ? value : null; }
        internal string Require(string key) { return Get(key) ?? throw new ArgumentException("Missing --" + key); }
        internal bool Has(string key) { return values.ContainsKey(key); }
    }
}
