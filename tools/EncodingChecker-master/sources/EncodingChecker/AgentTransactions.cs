using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace EncodingChecker;

public static partial class AgentCli
{
    private static readonly JsonSerializerOptions NativeJson = new() { PropertyNameCaseInsensitive = true };

    private static (object Result, int Exit) ConvertSnapshot(Arguments options, CancellationToken token)
    {
        string snapshotPath = Full(options.Require("snapshot"));
        AgentSnapshot snapshot = ReadSnapshot(snapshotPath);
        if (!snapshot.Complete) throw new IOException("Incomplete snapshot: resolve file/directory errors before conversion.");
        var blocked = snapshot.Files.Where(row => row.Status is not ("confirmed" or "ascii-compatible" or "binary")).Select(row => row.Path).ToList();
        if (blocked.Count > 0) return (new { schema_version = 2, success = false, apply = options.Has("apply"), blocked }, 2);
        var entries = new List<ConversionReportEntry>();
        foreach (AgentFile row in snapshot.Files)
        {
            string path = Within(snapshot.Root, row.Path);
            if (string.Equals(path, snapshotPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Snapshot cannot also be a source file.");
            CheckHash(path, row.Sha256);
            if (row.Status == "binary") continue;
            Encoding codec = Resolve(row.Encoding ?? throw new IOException("Confirmed codec is missing."));
            AgentFile verified = Probe(path, row.Path, CodecName(codec), null, false);
            if (verified.Status != "confirmed" || verified.Sha256 != row.Sha256 || verified.Bytes != row.Bytes || verified.Bom != row.Bom)
                throw new IOException("Snapshot codec/BOM/bytes failed full-file verification: " + row.Path);
            entries.Add(new ConversionReportEntry
            {
                FilePath = path, SourceEncoding = codec.WebName, SourceHasBom = row.Bom is not null,
                TargetEncoding = "utf-8", TargetHasBom = false, SourceEncodingWasSpecified = true,
                DetectedEncodingLabel = row.SuggestedEncoding is null ? null : Resolve(row.SuggestedEncoding).WebName,
                DetectedEncodingHasBom = row.Bom is not null,
                // A confirmed snapshot establishes author intent. BOM-less byte validity
                // is only compatibility and must not veto that explicit source choice.
                HasReliableUnicodeDetection = row.Bom is not null,
                ExpectedSourceSha256 = row.Sha256, ExpectedSourceSize = row.Bytes,
            });
        }
        ScanEngine.ConvertFiles(entries, "utf-8", false, options.Parallelism, true, true, _ => { }, token);
        if (entries.Any(e => e.Result is ConversionRowResult.Error or ConversionRowResult.Refused))
            return (new { schema_version = 2, success = false, apply = false, files = entries.Select(e => new { path = e.FilePath, result = e.Result.ToString(), e.ReasonCode, e.Diagnostic }) }, 3);
        ConversionPlan plan = ConversionPlan.FromEntries(entries, snapshot.Root, "utf-8", false, true, null);
        string? requestedPlan = options.Get("plan");
        string? logPath = options.Get("log");
        if (!options.Has("apply") && logPath is not null) throw new ArgumentException("--log requires --apply; --plan saves a preview.");
        string? planPath = requestedPlan is null ? null : Full(requestedPlan);
        if (options.Has("apply"))
        {
            logPath = Full(logPath ?? snapshotPath + ".conversion-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".json");
            planPath ??= logPath + ".plan.json";
        }
        foreach (string output in new[] { planPath, logPath }.OfType<string>())
        {
            if (string.Equals(output, snapshotPath, StringComparison.OrdinalIgnoreCase) || entries.Any(e => string.Equals(e.FilePath, output, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Plan/log output cannot replace a source or snapshot.");
            if (DirectoryTraversal.HasReservedArtifactSuffix(output)) throw new IOException("Plan/log cannot use a reserved recovery suffix.");
        }
        if (planPath is not null && logPath is not null && string.Equals(planPath, logPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Plan and log paths must differ.");
        if (planPath is not null)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(planPath)!);
            if (plan.Save(planPath) is { } error) throw new IOException(error);
        }
        if (!options.Has("apply")) return (new { schema_version = 2, success = true, apply = false, plan = planPath, files = plan.Files }, 0);
        return ApplyAgentPlan(planPath!, logPath!, options.Parallelism, token);
    }

    private static (object Result, int Exit) ApplyAgentPlan(string planInput, string logOutput, int parallelism, CancellationToken token)
    {
        string planPath = Full(planInput), logPath = Full(logOutput);
        ConversionPlan plan = ConversionPlan.Load(planPath, out string? error) ?? throw new IOException(error);
        if (plan.TargetEncoding != "utf-8" || plan.TargetHasBom || !plan.BackupEnabled) throw new IOException("Agent apply requires a backed-up UTF-8 no-BOM plan.");
        if (plan.FindStaleFiles().Count > 0) throw new IOException("Plan is stale; no files were changed.");
        if (string.Equals(planPath, logPath, StringComparison.OrdinalIgnoreCase) || DirectoryTraversal.HasReservedArtifactSuffix(logPath)) throw new IOException("Invalid journal path.");
        if (File.Exists(logPath)) throw new IOException("Journal output already exists; use a new path to preserve recovery history.");
        if (AtomicArtifactFile.RefusalForExistingDestination(logPath) is { } refusal) throw new IOException(refusal);
        foreach (PlannedFile row in plan.Files)
        {
            string path = Full(plan.ResolvePath(row) ?? throw new IOException("Plan escapes its root."));
            if (string.Equals(path, logPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Journal cannot replace a source.");
            if (row.Action != PlannedAction.Convert) continue;
            if (File.Exists(path + ".bak") || File.Exists(path + ConversionMetadataStore.Suffix))
                throw new IOException("Existing recovery artifacts would be replaced; archive them before another conversion: " + path);
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath)!);
        TextWriter previous = Console.Out;
        int exit;
        try
        {
            // Upstream owns plan validation, conversion, metadata and result journaling.
            Console.SetOut(TextWriter.Null);
            exit = Program.RunConsoleMode(["-Apply", planPath, "-Journal", logPath, "-MaxParallelism", parallelism.ToString()], token);
        }
        finally { Console.SetOut(previous); }
        ConversionJournal? journal = File.Exists(logPath) ? JsonSerializer.Deserialize<ConversionJournal>(File.ReadAllText(logPath), NativeJson) : null;
        return (new { schema_version = 2, success = exit == 0, apply = true, plan = planPath, log = logPath, journal }, exit);
    }

    private static (object Result, int Exit) Rollback(Arguments options, CancellationToken token)
    {
        string logPath = Full(options.Require("log"));
        ConversionJournal journal = JsonSerializer.Deserialize<ConversionJournal>(File.ReadAllText(logPath), NativeJson) ?? throw new IOException("Empty journal.");
        if (journal.JournalVersion != ConversionJournal.CurrentJournalVersion || journal.Preview || !journal.BackupEnabled)
            throw new IOException("Journal is not a supported backed-up conversion.");
        string root = Full(journal.BaseDirectory);
        var restore = new List<(string Path, string Backup, string Before, string After)>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JournalEntry row in journal.Entries)
        {
            if (row.Status == ConversionStatus.InstallationUnknown) throw new IOException("Unknown installation requires inspection before rollback.");
            if (row.Status is not (ConversionStatus.Converted or ConversionStatus.ConvertedWithWarning)) continue;
            string path = Within(root, row.RelativePath);
            if (!paths.Add(path)) throw new IOException("Duplicate restore path.");
            string backup = Within(root, row.BackupPath ?? throw new IOException("Backup path missing."));
            string metadataPath = Within(root, row.RecoveryMetadataPath ?? throw new IOException("Recovery metadata missing."));
            if (backup != path + ".bak" || metadataPath != path + ConversionMetadataStore.Suffix) throw new IOException("Unexpected recovery paths.");
            if (AtomicArtifactFile.RefusalForExistingDestination(path) is { } refusal) throw new IOException(refusal);
            CheckHash(backup, row.Sha256Before);
            string current = ConversionMetadataStore.ComputeSha256(path);
            if (current == row.Sha256Before) continue; // Idempotent byte-verified repeat.
            CheckHash(path, row.Sha256After);
            ConversionMetadata metadata = JsonSerializer.Deserialize<ConversionMetadata>(File.ReadAllText(metadataPath), NativeJson) ?? throw new IOException("Empty recovery metadata.");
            if (metadata.MetadataVersion != ConversionMetadata.CurrentMetadataVersion || metadata.OriginalSha256 != row.Sha256Before || metadata.BackupSha256 != row.Sha256Before || metadata.ExpectedOutputSha256 != row.Sha256After)
                throw new IOException("Recovery metadata and journal disagree.");
            restore.Add((path, backup, row.Sha256Before, row.Sha256After!));
        }
        string output = Full(options.Get("output") ?? logPath + ".rollback.json");
        bool Collides(string path) => string.Equals(output, path, StringComparison.OrdinalIgnoreCase);
        if (options.Has("apply") && (Collides(logPath) || DirectoryTraversal.HasReservedArtifactSuffix(output) || journal.Entries.Any(row =>
                Collides(Within(root, row.RelativePath)) || row.BackupPath is not null && Collides(Within(root, row.BackupPath)) ||
                row.RecoveryMetadataPath is not null && Collides(Within(root, row.RecoveryMetadataPath)))))
            throw new IOException("Rollback report cannot replace input or recovery files.");
        if (options.Has("apply") && AtomicArtifactFile.RefusalForExistingDestination(output) is { } reportRefusal) throw new IOException(reportRefusal);
        var restored = new List<string>();
        object Report(bool success, string state, string? error = null) => new { schema_version = 2, success, operation = "rollback", state,
            apply = options.Has("apply"), root, log = logPath, files = restore.Select(r => new { path = System.IO.Path.GetRelativePath(root, r.Path), original_sha256 = r.Before, converted_sha256 = r.After }).ToArray(), restored, backups_retained = true, error };
        if (options.Has("apply"))
        {
            WriteJson(output, Report(false, "prepared"));
            // All entries preflight before any write. Restore originals, without re-encoding.
            try
            {
                foreach (var item in restore)
                {
                    token.ThrowIfCancellationRequested();
                    string temp = item.Path + "." + Guid.NewGuid().ToString("N") + "." + EncodingConverter.TempFileSuffix;
                    try
                    {
                        using (var source = new FileStream(item.Backup, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        { source.CopyToAsync(target, token).GetAwaiter().GetResult(); target.Flush(true); }
                        CheckHash(temp, item.Before);
                        CheckHash(item.Path, item.After);
                        EncodingConverter.AtomicReplaceForBackup(temp, item.Path);
                        CheckHash(item.Path, item.Before);
                        restored.Add(System.IO.Path.GetRelativePath(root, item.Path));
                        WriteJson(output, Report(false, "restoring"));
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                object failed = Report(false, "incomplete", error.Message);
                WriteJson(output, failed);
                return (failed, error is OperationCanceledException ? 4 : 3);
            }
        }
        object result = Report(true, options.Has("apply") ? "complete" : "preview");
        if (options.Has("apply")) WriteJson(output, result);
        return (result, 0);
    }
}
