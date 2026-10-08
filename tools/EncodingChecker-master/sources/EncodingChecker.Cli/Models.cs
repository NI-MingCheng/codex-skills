using System;
using System.Collections.Generic;

namespace EncodingChecker.Cli
{
    public sealed class Snapshot
    {
        public int schema_version { get; set; }
        public string kind { get; set; } = "encodingchecker-snapshot";
        public string root { get; set; }
        public string created_utc { get; set; } = DateTime.UtcNow.ToString("o");
        public List<FileRecord> files { get; set; }
        public string state { get; set; } = "scanned";
    }

    public sealed class FileRecord
    {
        public string path { get; set; }
        public string encoding { get; set; }
        public string status { get; set; }
        public string sha256 { get; set; }
        public long bytes { get; set; }
        public string bom { get; set; }
        public EolInfo eol { get; set; }
        public List<Candidate> candidates { get; set; } = new List<Candidate>();
        public List<string> evidence { get; set; } = new List<string>();
        public string suggested_encoding { get; set; }
        public double? confidence { get; set; }
    }

    public sealed class EolInfo
    {
        public string kind { get; set; }
        public int crlf { get; set; }
        public int lf { get; set; }
        public int cr { get; set; }
        public bool final_newline { get; set; }
    }

    public sealed class Candidate
    {
        public string encoding { get; set; }
        public bool roundtrip { get; set; } = true;
        public string text_sha256 { get; set; }
        public string preview { get; set; }
    }

    public sealed class Policy
    {
        public string default_encoding { get; set; }
        public Dictionary<string, string> files { get; set; } = new Dictionary<string, string>();
    }

    public sealed class PlanEntry
    {
        public string path { get; set; }
        public string original_encoding { get; set; }
        public string original_bom { get; set; }
        public string original_sha256 { get; set; }
        public string converted_sha256 { get; set; }
        public string backup_path { get; set; }
        public string state { get; set; }
        public string error { get; set; }
        [System.Web.Script.Serialization.ScriptIgnore] public byte[] original { get; set; }
        [System.Web.Script.Serialization.ScriptIgnore] public byte[] converted { get; set; }
        [System.Web.Script.Serialization.ScriptIgnore] public string text { get; set; }
        [System.Web.Script.Serialization.ScriptIgnore] public System.IO.FileAttributes attributes { get; set; }
    }

    public sealed class ConversionLog
    {
        public int schema_version { get; set; }
        public string kind { get; set; } = "encodingchecker-conversion-log";
        public string root { get; set; }
        public string backup_dir { get; set; }
        public string snapshot { get; set; }
        public string created_utc { get; set; } = DateTime.UtcNow.ToString("o");
        public string updated_utc { get; set; }
        public string target_encoding { get; set; } = "utf-8";
        public string state { get; set; }
        public List<PlanEntry> files { get; set; }
        public List<string> errors { get; set; } = new List<string>();
    }

    public sealed class ConversionResult
    {
        public int schema_version { get; set; } = 1;
        public string operation { get; set; }
        public string root { get; set; }
        public bool apply { get; set; }
        public string target_encoding { get; set; } = "utf-8";
        public string backup_dir { get; set; }
        public string log { get; set; }
        public bool success { get; set; }
        public bool source_snapshot_stale { get; set; }
        public List<PlanEntry> files { get; set; } = new List<PlanEntry>();
        public List<string> blocked { get; set; } = new List<string>();
        public List<string> errors { get; set; } = new List<string>();
    }
}
