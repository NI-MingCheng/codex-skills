using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace EncodingChecker;

public static partial class AgentCli
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    internal static Encoding Resolve(string name)
    {
        string normalized = name.Trim().ToLowerInvariant();
        int? id = normalized switch
        {
            "utf-8" or "utf8" => 65001,
            "ascii" => 20127,
            "gbk" or "cp936" => 936,
            "gb18030" => 54936,
            "utf-16-le" or "utf-16le" => 1200,
            "utf-16-be" or "utf-16be" => 1201,
            "utf-32-le" or "utf-32le" => 12000,
            "utf-32-be" or "utf-32be" => 12001,
            _ => int.TryParse(normalized, out int codePage) ? codePage : null,
        };
        Encoding encoding = id is { } code ? Encoding.GetEncoding(code) : Encoding.GetEncoding(normalized);
        if (encoding.CodePage == 65000)
            throw new ArgumentException("UTF-7 is not supported.");
        return TextEncoding.Strict(encoding);
    }

    internal static string CodecName(Encoding encoding) => encoding.CodePage switch
    {
        65001 => "utf-8", 20127 => "ascii", 936 => "gbk", 54936 => "gb18030",
        1200 => "utf-16-le", 1201 => "utf-16-be", 12000 => "utf-32-le", 12001 => "utf-32-be",
        _ => encoding.WebName,
    };

    internal static string Full(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        for (string? current = full; current is not null; current = System.IO.Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Linked paths are not allowed: {current}");
        }
        return full;
    }

    internal static string Within(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || System.IO.Path.IsPathRooted(relative) ||
            relative.Replace('\\', '/').Split('/').Contains(".."))
            throw new IOException($"Invalid relative path: {relative}");
        string full = Full(System.IO.Path.Combine(root, relative));
        string prefix = System.IO.Path.TrimEndingDirectorySeparator(Full(root));
        if (!prefix.EndsWith(System.IO.Path.DirectorySeparatorChar)) prefix += System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Path escapes the snapshot root: {relative}");
        return full;
    }

    internal static void WriteJson(string path, object value)
    {
        string full = Full(path);
        if (AtomicArtifactFile.RefusalForExistingDestination(full) is { } refusal)
            throw new IOException(refusal);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        string? error = AtomicArtifactFile.WriteText(full, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
        if (error is not null) throw new IOException(error);
    }

    internal static T ReadJson<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(Full(path)), Json)
        ?? throw new IOException("The JSON document is empty.");

    internal static AgentSnapshot ReadSnapshot(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Full(path)));
        if (!document.RootElement.TryGetProperty("schema_version", out var schema) || schema.GetInt32() != 2 ||
            !document.RootElement.TryGetProperty("kind", out var kind) || kind.GetString() != "encodingchecker-agent-snapshot")
            throw new IOException("Unsupported snapshot. Regenerate old CLI 1.x snapshots with agent scan.");
        AgentSnapshot snapshot = ReadJson<AgentSnapshot>(path);
        if (snapshot.SchemaVersion != 2 || snapshot.Kind != "encodingchecker-agent-snapshot")
            throw new IOException("Unsupported snapshot. Regenerate old CLI 1.x snapshots with agent scan.");
        snapshot.Root = Full(snapshot.Root);
        if (!Directory.Exists(snapshot.Root)) throw new IOException("Snapshot root is missing.");
        if (snapshot.Files is null || snapshot.Coverage is null) throw new IOException("Snapshot files/coverage are missing.");
        var paths = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AgentFile row in snapshot.Files)
            if (!paths.Add(Within(snapshot.Root, row.Path))) throw new IOException("Duplicate snapshot path.");
        return snapshot;
    }

    internal static void CheckHash(string path, string? expected)
    {
        if (expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit) ||
            !string.Equals(ConversionMetadataStore.ComputeSha256(Full(path)), expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Source changed or snapshot hash is invalid: {path}");
    }

    private static string? ReadBom(Stream stream, out int length)
    {
        byte[] prefix = new byte[4];
        stream.Position = 0;
        int count = stream.ReadAtLeast(prefix, 4, throwOnEndOfStream: false);
        stream.Position = 0;
        length = 0;
        if (count >= 4 && prefix.AsSpan().SequenceEqual(new byte[] { 255, 254, 0, 0 })) { length = 4; return "utf-32-le"; }
        if (count >= 4 && prefix.AsSpan().SequenceEqual(new byte[] { 0, 0, 254, 255 })) { length = 4; return "utf-32-be"; }
        if (count >= 3 && prefix[0] == 239 && prefix[1] == 187 && prefix[2] == 191) { length = 3; return "utf-8"; }
        if (count >= 2 && prefix[0] == 255 && prefix[1] == 254) { length = 2; return "utf-16-le"; }
        if (count >= 2 && prefix[0] == 254 && prefix[1] == 255) { length = 2; return "utf-16-be"; }
        return null;
    }

    // Stream both decoding and round-trip hashing; never buffer a whole file or batch.
    private static (AgentEol Eol, string Preview)? InspectCodec(Stream stream, Encoding codec, string hash, int bomLength, bool preview)
    {
        try
        {
            stream.Position = 0;
            byte[] prefix = new byte[bomLength];
            stream.ReadExactly(prefix);
            using var roundtrip = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            roundtrip.AppendData(prefix);
            using var reader = new StreamReader(stream, codec, false, 32768, leaveOpen: true);
            char[] chars = new char[32768];
            byte[] encoded = new byte[codec.GetMaxByteCount(chars.Length)];
            Encoder encoder = codec.GetEncoder();
            var eol = new AgentEol();
            var sample = new StringBuilder();
            char last = '\0';
            int read;
            while ((read = reader.Read(chars, 0, chars.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    char c = chars[i];
                    if (char.IsControl(c) && c is not ('\r' or '\n' or '\t' or '\f')) return null;
                    if (c == '\r') eol.Cr++;
                    else if (c == '\n') { if (last == '\r') { eol.Cr--; eol.Crlf++; } else eol.Lf++; }
                    last = c;
                }
                if (preview && sample.Length < 160) sample.Append(chars, 0, Math.Min(read, 160 - sample.Length));
                int count = encoder.GetBytes(chars, 0, read, encoded, 0, flush: false);
                roundtrip.AppendData(encoded.AsSpan(0, count));
            }
            int final = encoder.GetBytes([], 0, 0, encoded, 0, flush: true);
            roundtrip.AppendData(encoded.AsSpan(0, final));
            if (!string.Equals(Convert.ToHexStringLower(roundtrip.GetHashAndReset()), hash, StringComparison.OrdinalIgnoreCase)) return null;
            int kinds = (eol.Crlf > 0 ? 1 : 0) + (eol.Lf > 0 ? 1 : 0) + (eol.Cr > 0 ? 1 : 0);
            eol.Kind = kinds > 1 ? "mixed" : eol.Crlf > 0 ? "crlf" : eol.Lf > 0 ? "lf" : eol.Cr > 0 ? "cr" : "none";
            eol.FinalNewline = last is '\r' or '\n';
            if (sample.Length > 0 && char.IsHighSurrogate(sample[^1])) sample.Length--;
            return (eol, sample.ToString());
        }
        catch (Exception error) when (error is DecoderFallbackException or EncoderFallbackException) { return null; }
        finally { stream.Position = 0; }
    }
}
