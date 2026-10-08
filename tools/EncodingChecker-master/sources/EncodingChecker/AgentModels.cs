using System;
using System.Collections.Generic;

namespace EncodingChecker;

internal sealed class AgentSnapshot
{
    public int SchemaVersion { get; set; } = 2;
    public string Kind { get; set; } = "encodingchecker-agent-snapshot";
    public string Root { get; set; } = "";
    public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public bool Complete { get; set; }
    public List<AgentFile> Files { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public AgentCoverage Coverage { get; set; } = new();
}

internal sealed class AgentFile
{
    public string Path { get; set; } = "";
    public string Status { get; set; } = "unknown";
    public string? Encoding { get; set; }
    public string? SuggestedEncoding { get; set; }
    public string? Sha256 { get; set; }
    public long Bytes { get; set; }
    public string? Bom { get; set; }
    public AgentEol? Eol { get; set; }
    public List<string> Candidates { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
    public Dictionary<string, string> Previews { get; set; } = [];
}

internal sealed class AgentEol
{
    public string Kind { get; set; } = "none";
    public long Crlf { get; set; }
    public long Lf { get; set; }
    public long Cr { get; set; }
    public bool FinalNewline { get; set; }
}

internal sealed class AgentCoverage
{
    public int FilesExcludedByAttribute { get; set; }
    public int DirectoriesExcludedByAttribute { get; set; }
    public int DirectoriesExcludedByName { get; set; }
    public int DirectoriesUnreadable { get; set; }
    public int FilesExcludedAsEcArtifact { get; set; }
}

internal sealed class AgentPolicy
{
    public string? DefaultEncoding { get; set; }
    public Dictionary<string, string> Files { get; set; } = [];
}
