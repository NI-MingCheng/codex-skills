using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EncodingChecker;

public static partial class AgentCli
{
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".exe", ".dll", ".pdb", ".lib", ".a", ".obj", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".zip", ".7z", ".pdf", ".docx", ".xlsx", ".pptx" };

    internal static AgentFile Probe(string path, string relative, string? explicitCodec, string? fallback, bool previews)
    {
        var row = new AgentFile { Path = relative };
        try
        {
            path = Full(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            row.Bytes = stream.Length;
            row.Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
            row.Bom = ReadBom(stream, out int bomLength);
            if (BinaryExtensions.Contains(System.IO.Path.GetExtension(path))) { row.Status = "binary"; return row; }
            Encoding? detected = TextEncoding.DetectFromStream(stream);
            row.SuggestedEncoding = detected is null ? null : CodecName(detected);
            string? selected = explicitCodec ?? row.Bom ?? fallback;
            if (explicitCodec is not null && row.Bom is not null && Resolve(explicitCodec).CodePage != Resolve(row.Bom).CodePage)
                throw new IOException("Explicit codec conflicts with BOM.");
            if (selected is not null)
            {
                Encoding codec = Resolve(selected);
                var result = InspectCodec(stream, codec, row.Sha256, bomLength, previews)
                    ?? throw new IOException("Complete file does not strictly round-trip as the selected text codec.");
                row.Encoding = CodecName(codec);
                row.Status = "confirmed";
                row.Eol = result.Eol;
                row.Evidence.Add(explicitCodec is not null ? "explicit-file-policy" : row.Bom is not null ? "bom" : "confirmed-project-default");
                if (previews) row.Previews[row.Encoding] = result.Preview;
                return row;
            }

            var interpretations = new Dictionary<string, (AgentEol Eol, string Preview)>();
            foreach (string candidate in new[] { "ascii", "utf-8", "gbk", "gb18030", row.SuggestedEncoding }.OfType<string>().Distinct())
            {
                Encoding codec = Resolve(candidate);
                if (InspectCodec(stream, codec, row.Sha256, 0, previews) is { } result)
                {
                    interpretations[CodecName(codec)] = result;
                    if (previews) row.Previews[CodecName(codec)] = result.Preview;
                }
            }
            row.Candidates = [.. interpretations.Keys];
            if (interpretations.TryGetValue("ascii", out var ascii))
            {
                row.Status = "ascii-compatible";
                row.Encoding = "ascii";
                row.Eol = ascii.Eol;
                row.Evidence.Add("ASCII bytes are compatible with multiple codecs; future non-ASCII edits use project conventions.");
            }
            else
            {
                row.Status = interpretations.ContainsKey("utf-8") && interpretations.ContainsKey("gbk") ? "ambiguous" : interpretations.Count > 0 ? "needs-confirmation" : "unknown";
                row.Evidence.Add("Detection and full-file compatibility are candidates, not author-intent confirmation.");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { row.Status = "error"; row.Evidence.Add(error.Message); }
        return row;
    }

    internal static AgentSnapshot Scan(Arguments options, CancellationToken token)
    {
        string root = Full(options.Require("root"));
        if (!Directory.Exists(root)) throw new IOException("Scan root does not exist.");
        string? output = options.Get("output") is { } outPath ? Full(outPath) : null;
        string? policyPath = options.Get("policy") is { } policyInput ? Full(policyInput) : null;
        if (output is not null && string.Equals(output, policyPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Snapshot output cannot overwrite its policy input.");
        AgentPolicy policy = policyPath is null ? new() : ReadJson<AgentPolicy>(policyPath);
        if (policy.Files is null) throw new IOException("Policy files must be an object.");
        string? fallback = options.Get("default-encoding") ?? policy.DefaultEncoding;
        if (fallback is not null) _ = Resolve(fallback);
        var policies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in policy.Files) policies.Add(Within(root, pair.Key), CodecName(Resolve(pair.Value)));
        var snapshot = new AgentSnapshot { Root = root };
        var counters = new DirectoryTraversal.TraversalCounters();
        List<string> exclusions = ["vendor/*", "*/vendor/*", "third_party/*", "*/third_party/*", "generated/*", "*/generated/*"];
        exclusions.AddRange(options.List("exclude"));
        var paths = DirectoryTraversal.EnumerateFiles(root, true,
            DirectoryTraversal.CompilePatterns(options.List("include"), true),
            DirectoryTraversal.CompilePatterns(exclusions, false),
            new[] { output, policyPath }.OfType<string>().ToArray(),
            onWarning: message => { lock (snapshot.Warnings) snapshot.Warnings.Add(message); }, counters);
        var files = new ConcurrentBag<AgentFile>();
        Parallel.ForEach(paths, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = options.Parallelism }, path =>
        {
            policies.TryGetValue(path, out string? chosen);
            files.Add(Probe(path, System.IO.Path.GetRelativePath(root, path).Replace('\\', '/'), chosen, fallback, false));
        });
        snapshot.Files = [.. files.OrderBy(row => row.Path, StringComparer.OrdinalIgnoreCase)];
        snapshot.Coverage = new()
        {
            FilesExcludedByAttribute = counters.FilesExcludedByAttribute,
            DirectoriesExcludedByAttribute = counters.DirectoriesExcludedByAttribute,
            DirectoriesExcludedByName = counters.DirectoriesExcludedByName,
            DirectoriesUnreadable = counters.DirectoriesUnreadable,
            FilesExcludedAsEcArtifact = counters.FilesExcludedAsEcArtifact,
        };
        snapshot.Complete = counters.DirectoriesUnreadable == 0 && snapshot.Files.All(row => row.Status != "error");
        if (output is not null) WriteJson(output, snapshot);
        return snapshot;
    }

    internal static AgentFile Confirm(Arguments options)
    {
        string snapshotPath = Full(options.Require("snapshot"));
        AgentSnapshot snapshot = ReadSnapshot(snapshotPath);
        string file = Within(snapshot.Root, options.Require("path"));
        AgentFile row = snapshot.Files.SingleOrDefault(r => string.Equals(Within(snapshot.Root, r.Path), file, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("File is not in the snapshot.");
        if (row.Status == "binary") throw new IOException("Binary files cannot be confirmed.");
        CheckHash(file, row.Sha256);
        AgentFile replacement = Probe(file, row.Path, options.Require("encoding"), null, false);
        if (replacement.Status != "confirmed" || replacement.Sha256 != row.Sha256)
            throw new IOException("Confirmation failed: " + string.Join("; ", replacement.Evidence));
        replacement.Evidence.Add("confirmation: " + options.Require("reason"));
        snapshot.Files[snapshot.Files.IndexOf(row)] = replacement;
        snapshot.Complete = snapshot.Coverage.DirectoriesUnreadable == 0 && snapshot.Files.All(r => r.Status != "error");
        WriteJson(snapshotPath, snapshot);
        return replacement;
    }
}
