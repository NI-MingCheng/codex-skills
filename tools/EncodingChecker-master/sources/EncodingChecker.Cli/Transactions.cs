using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EncodingChecker.Cli
{
    internal static class Transactions
    {
        internal static ConversionResult Convert(Options options, out int exitCode)
        {
            string snapshotPath = SafeFiles.Full(options.Require("snapshot"));
            Snapshot snapshot = SafeFiles.ReadSnapshot(snapshotPath);
            bool apply = options.Has("apply");
            string backup = SafeFiles.Full(options.Get("backup-dir") ?? Path.Combine(snapshot.root, "docs", "encoding-backups",
                DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N")));
            SafeFiles.NoReparse(backup);
            string logPath = Path.Combine(backup, "conversion-log.json");
            var result = new ConversionResult { operation = "convert", root = snapshot.root, apply = apply, backup_dir = backup, log = logPath };
            foreach (FileRecord record in snapshot.files)
            {
                if (record.status == "excluded")
                {
                    result.files.Add(new PlanEntry { path = record.path, state = "skipped-excluded" });
                    continue;
                }
                string path = SafeFiles.Within(snapshot.root, record.path);
                if (record.status == "error") { result.blocked.Add(record.path + ": error or incomplete scan"); continue; }
                if (record.status == "binary")
                {
                    SafeFiles.ReadChecked(path, record.sha256);
                    result.files.Add(new PlanEntry { path = record.path, state = "skipped-binary", original_sha256 = record.sha256 });
                    continue;
                }
                if (record.status != "confirmed" && record.status != "ascii-compatible")
                {
                    if (record.status != "ambiguous" && record.status != "needs-confirmation" && record.status != "unknown")
                        throw new InvalidDataException("Unsupported file status: " + record.status);
                    byte[] unresolved = SafeFiles.ReadChecked(path, record.sha256);
                    if (unresolved.Length != 0) { result.blocked.Add(record.path + ": " + record.status); continue; }
                    // An empty file is safe under ASCII compatibility regardless of an older unresolved label.
                }
                if (String.Equals(path, snapshotPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The input snapshot cannot also be a conversion source.");
                if (path.StartsWith(backup.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(path, backup, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Backup directory contains a conversion source: " + record.path);
                byte[] original = SafeFiles.ReadChecked(path, record.sha256);
                if (record.bytes != original.LongLength) throw new IOException("Snapshot byte count changed: " + record.path);
                string encoding = original.Length == 0 ? "ascii" : record.status == "ascii-compatible" ? "ascii" : Detection.Canonical(record.encoding);
                string text = Detection.Decode(original, encoding);
                int offset;
                string bom = Detection.Bom(original, out offset);
                if (bom != record.bom) throw new InvalidDataException("Snapshot BOM differs from source: " + record.path);
                byte[] converted = Detection.Utf8.GetBytes(text);
                SafeFiles.CheckWritable(path);
                result.files.Add(new PlanEntry { path = record.path, original_encoding = encoding, original_bom = bom,
                    original_sha256 = SafeFiles.Hash(original), converted_sha256 = SafeFiles.Hash(converted), original = original,
                    converted = converted, text = text, attributes = File.GetAttributes(path),
                    state = original.SequenceEqual(converted) ? "unchanged" : "planned" });
            }
            if (result.blocked.Count != 0)
            {
                result.success = false;
                exitCode = 2;
                Console.Error.WriteLine("Conversion refused: resolve every blocked entry before applying the batch.");
                return result;
            }
            if (!apply) { result.success = true; exitCode = 0; return result; }
            if (File.Exists(backup) || (Directory.Exists(backup) && Directory.GetFileSystemEntries(backup).Length != 0))
                throw new IOException("Backup directory must be new or empty: " + backup);
            SafeFiles.CheckWritable(logPath);
            var log = new ConversionLog { schema_version = 1, root = snapshot.root, snapshot = snapshotPath, backup_dir = backup, state = "preparing",
                files = result.files.Where(f => f.original != null).ToList() };
            string logHash = null;
            try
            {
                // Validate the whole batch again before creating backups or modifying any source.
                foreach (PlanEntry entry in log.files)
                {
                    string source = SafeFiles.Within(log.root, entry.path);
                    SafeFiles.ReadChecked(source, entry.original_sha256);
                    SafeFiles.CheckWritable(source);
                }
                SafeFiles.CreateDirectory(backup);
                SafeFiles.AtomicWrite(Path.Combine(backup, SafeFiles.BackupMarker), Detection.Utf8.GetBytes(SafeFiles.BackupMarkerText));
                foreach (PlanEntry entry in log.files.Where(e => e.state != "unchanged"))
                {
                    string destination = SafeFiles.Within(backup, "original/" + entry.path);
                    SafeFiles.CreateDirectory(Path.GetDirectoryName(destination));
                    if (File.Exists(destination)) throw new IOException("Backup already exists: " + destination);
                    using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                    {
                        stream.Write(entry.original, 0, entry.original.Length);
                        stream.Flush(true);
                    }
                    SafeFiles.ReadChecked(destination, entry.original_sha256);
                    entry.backup_path = destination;
                    entry.state = "backed-up";
                }
                foreach (PlanEntry entry in log.files) SafeFiles.ReadChecked(SafeFiles.Within(log.root, entry.path), entry.original_sha256);
                log.state = "prepared";
                SaveLog(logPath, log, ref logHash);
                foreach (PlanEntry entry in log.files.Where(e => e.state != "unchanged"))
                {
                    string source = SafeFiles.Within(log.root, entry.path);
                    SafeFiles.ReadChecked(source, entry.original_sha256);
                    SafeFiles.CheckWritable(source);
                    entry.state = "applying";
                    log.state = "applying";
                    SaveLog(logPath, log, ref logHash);
                    SafeFiles.AtomicWrite(source, entry.converted, entry.original_sha256);
                    byte[] written = SafeFiles.ReadChecked(source, entry.converted_sha256);
                    if (Detection.Decode(written, "utf-8") != entry.text) throw new IOException("Post-write Unicode text verification failed: " + source);
                    entry.state = "converted";
                    SaveLog(logPath, log, ref logHash);
                }
                log.state = "completed";
                SaveLog(logPath, log, ref logHash);
                result.success = true;
                result.source_snapshot_stale = true;
                exitCode = 0;
                return result;
            }
            catch (Exception error)
            {
                log.errors.Add(error.GetType().Name + ": " + error.Message);
                Console.Error.WriteLine("Conversion failed: " + error.Message);
                foreach (PlanEntry entry in log.files.Where(e => e.state == "applying" || e.state == "converted"))
                {
                    try { Restore(log, entry); }
                    catch (Exception restoreError)
                    {
                        entry.state = "rollback-failed";
                        entry.error = restoreError.Message;
                        log.errors.Add("Automatic rollback failed for " + entry.path + ": " + restoreError.Message);
                        Console.Error.WriteLine(log.errors.Last());
                    }
                }
                log.state = log.files.Any(e => e.state == "rollback-failed") ? "partial" : "failed-sources-restored";
                try { SaveLog(logPath, log, ref logHash); }
                catch (Exception logError)
                {
                    log.errors.Add("Log persistence failed: " + logError.Message);
                    Console.Error.WriteLine(log.errors.Last());
                }
                result.errors = log.errors;
                result.success = false;
                result.source_snapshot_stale = log.state == "partial";
                exitCode = 1;
                return result;
            }
        }

        private static void SaveLog(string path, ConversionLog log, ref string expected)
        {
            log.updated_utc = DateTime.UtcNow.ToString("o");
            if (expected == null && File.Exists(path)) throw new IOException("Refusing to replace an existing conversion log: " + path);
            byte[] bytes = Detection.Utf8.GetBytes(SafeFiles.Json.Serialize(log) + "\n");
            SafeFiles.AtomicWrite(path, bytes, expected);
            expected = SafeFiles.Hash(bytes);
        }

        internal static ConversionResult Rollback(string path, out int exitCode)
        {
            path = SafeFiles.Full(path);
            SafeFiles.CheckWritable(path);
            byte[] originalLog = File.ReadAllBytes(path);
            ConversionLog log = SafeFiles.ReadJson<ConversionLog>(path);
            if (log.schema_version != 1 || log.kind != "encodingchecker-conversion-log" || log.files == null)
                throw new InvalidDataException("Unsupported or incomplete conversion log.");
            log.root = SafeFiles.Root(log.root);
            log.backup_dir = SafeFiles.Root(log.backup_dir).TrimEnd('\\', '/');
            if (!String.Equals(path, Path.Combine(log.backup_dir, "conversion-log.json"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Log must remain in its recorded backup directory.");
            if (log.errors == null) log.errors = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // All source and backup hashes are checked before the first restoration.
            foreach (PlanEntry entry in log.files)
            {
                if (entry == null) throw new InvalidDataException("Null log entry.");
                string source = SafeFiles.Within(log.root, entry.path);
                if (!seen.Add(source)) throw new InvalidDataException("Duplicate path in log: " + entry.path);
                ValidateRestoration(log, entry);
            }
            var result = new ConversionResult { operation = "rollback", root = log.root, apply = true, backup_dir = log.backup_dir,
                log = path, target_encoding = "original-bytes", files = log.files };
            string logHash = SafeFiles.Hash(originalLog);
            try
            {
                foreach (PlanEntry entry in log.files)
                {
                    // Persist intent before each atomic restoration, so interrupted runs can be retried.
                    log.state = "rolling-back";
                    SaveLog(path, log, ref logHash);
                    Restore(log, entry);
                    SaveLog(path, log, ref logHash);
                }
                log.state = "rolled-back";
                SaveLog(path, log, ref logHash);
                result.success = true;
                result.source_snapshot_stale = true;
                exitCode = 0;
                return result;
            }
            catch (Exception error)
            {
                log.state = "rollback-partial";
                log.errors.Add(error.GetType().Name + ": " + error.Message);
                Console.Error.WriteLine("Rollback failed: " + error.Message);
                try { SaveLog(path, log, ref logHash); }
                catch (Exception logError) { log.errors.Add("Log persistence failed: " + logError.Message); Console.Error.WriteLine(log.errors.Last()); }
                result.errors = log.errors;
                result.success = false;
                result.source_snapshot_stale = true;
                exitCode = 1;
                return result;
            }
        }

        private static byte[] ValidateRestoration(ConversionLog log, PlanEntry entry)
        {
            if (!new[] { "unchanged", "backed-up", "applying", "converted", "rolled-back", "rollback-failed" }.Contains(entry.state))
                throw new InvalidDataException("Unsupported restoration state: " + entry.state);
            SafeFiles.CheckHash(entry.original_sha256);
            SafeFiles.CheckHash(entry.converted_sha256);
            string source = SafeFiles.Within(log.root, entry.path);
            byte[] current = File.ReadAllBytes(source);
            string hash = SafeFiles.Hash(current);
            if (!String.Equals(hash, entry.original_sha256, StringComparison.OrdinalIgnoreCase) && !String.Equals(hash, entry.converted_sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Refusing to overwrite an externally changed file: " + source);
            if (entry.backup_path == null)
            {
                if (entry.original_sha256 != entry.converted_sha256 || entry.state != "unchanged" && entry.state != "rolled-back")
                    throw new InvalidDataException("Missing backup for changed source: " + entry.path);
                return current;
            }
            string backup = SafeFiles.Full(entry.backup_path);
            string prefix = SafeFiles.Root(log.backup_dir);
            if (!backup.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Backup path escapes backup directory.");
            byte[] original = SafeFiles.ReadChecked(backup, entry.original_sha256);
            if (hash != entry.original_sha256) SafeFiles.CheckWritable(source);
            return original;
        }

        private static void Restore(ConversionLog log, PlanEntry entry)
        {
            byte[] original = ValidateRestoration(log, entry);
            string source = SafeFiles.Within(log.root, entry.path);
            string currentHash = SafeFiles.Hash(File.ReadAllBytes(source));
            if (!String.Equals(currentHash, entry.original_sha256, StringComparison.OrdinalIgnoreCase))
                SafeFiles.AtomicWrite(source, original, entry.converted_sha256);
            SafeFiles.ReadChecked(source, entry.original_sha256);
            entry.state = "rolled-back";
            entry.error = null;
        }
    }
}
