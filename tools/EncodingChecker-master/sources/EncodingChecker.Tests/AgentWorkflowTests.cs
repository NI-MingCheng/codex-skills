using System.Text;
using System.Text.Json;

namespace EncodingChecker.Tests;

public sealed class AgentWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "encodingchecker-agent-tests-" + Guid.NewGuid().ToString("N"));
    private string Snapshot => Path.Combine(_root, "snapshot.json");
    private string Journal => Path.Combine(_root, "journal.json");
    public AgentWorkflowTests()
    {
        Directory.CreateDirectory(_root);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static (int Exit, JsonElement Json) Run(params string[] args)
    {
        TextWriter stdout = Console.Out, stderr = Console.Error;
        using var output = new StringWriter();
        using var errors = new StringWriter();
        try
        {
            Console.SetOut(output); Console.SetError(errors);
            int exit = AgentCli.Run(args);
            using var json = JsonDocument.Parse(output.ToString());
            return (exit, json.RootElement.Clone());
        }
        finally { Console.SetOut(stdout); Console.SetError(stderr); }
    }

    private string Corpus(params (string Name, byte[] Bytes)[] files)
    {
        string path = Path.Combine(_root, "corpus");
        Directory.CreateDirectory(path);
        foreach (var file in files) File.WriteAllBytes(Path.Combine(path, file.Name), file.Bytes);
        return path;
    }

    public static IEnumerable<object[]> Collisions => new[]
    { "C2A9", "C2A2", "C2A3", "C2A5", "C2B0", "C2B1", "C2B5", "C2B7", "C380", "C381", "C382", "C383", "C389", "C3A9", "C3B1", "C3B6" }
        .Select(hex => new object[] { hex });

    [Theory, MemberData(nameof(Collisions))]
    public void ConfirmedGbkCollision_RetainsAuthorTextAndRollsBackBytes(string hex)
    {
        byte[] original = [.. Encoding.ASCII.GetBytes("// "), .. Convert.FromHexString(hex), .. Encoding.ASCII.GetBytes("\r\nint n=1;\r\n")];
        string root = Corpus(("main.txt", original));
        string path = Path.Combine(root, "main.txt");
        var probe = Run("probe", "--path", path);
        Assert.Equal(2, probe.Exit);
        Assert.Equal("ambiguous", probe.Json.GetProperty("status").GetString());
        Assert.Equal(0, Run("scan", "--root", root, "--default-encoding", "gbk", "--output", Snapshot).Exit);
        Assert.Equal(0, Run("convert", "--snapshot", Snapshot).Exit);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(0, Run("convert", "--snapshot", Snapshot, "--apply", "--log", Journal).Exit);
        Assert.Equal(Encoding.GetEncoding(936).GetString(original), File.ReadAllText(path, Encoding.UTF8));
        Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
        Assert.Equal(0, Run("rollback", "--log", Journal).Exit);
        Assert.NotEqual(original, File.ReadAllBytes(path));
        Assert.Equal(0, Run("rollback", "--log", Journal, "--apply").Exit);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(0, Run("rollback", "--log", Journal, "--apply").Exit);
    }

    [Fact]
    public void UnknownSource_CannotConvertUntilConfirmed()
    {
        byte[] raw = Convert.FromHexString("C2A9");
        string root = Corpus(("a.txt", raw));
        Assert.Equal(2, Run("scan", "--root", root, "--output", Snapshot).Exit);
        Assert.Equal(2, Run("convert", "--snapshot", Snapshot, "--apply", "--log", Journal).Exit);
        Assert.False(File.Exists(Journal));
        Assert.Equal(raw, File.ReadAllBytes(Path.Combine(root, "a.txt")));
        Assert.Equal(0, Run("confirm", "--snapshot", Snapshot, "--path", "a.txt", "--encoding", "gbk", "--reason", "confirmed project codec").Exit);
        Assert.Equal(0, Run("convert", "--snapshot", Snapshot, "--apply", "--log", Journal).Exit);
        Assert.Equal("漏", File.ReadAllText(Path.Combine(root, "a.txt")));
    }

    [Theory]
    [InlineData("utf-16-le", 1200)] [InlineData("utf-16-be", 1201)]
    [InlineData("utf-32-le", 12000)] [InlineData("utf-32-be", 12001)]
    public void ConfirmedBomlessUnicode_ConvertsWithoutBinaryMisclassification(string name, int id)
    {
        const string text = "// 中文\r\nvalue=1;\n";
        byte[] raw = Encoding.GetEncoding(id).GetBytes(text);
        string root = Corpus(("a.txt", raw));
        Assert.Equal(0, Run("scan", "--root", root, "--default-encoding", name, "--output", Snapshot).Exit);
        Assert.Equal(0, Run("convert", "--snapshot", Snapshot, "--apply", "--log", Journal).Exit);
        Assert.Equal(text, File.ReadAllText(Path.Combine(root, "a.txt")));
        Assert.Equal(0, Run("rollback", "--log", Journal, "--apply").Exit);
        Assert.Equal(raw, File.ReadAllBytes(Path.Combine(root, "a.txt")));
    }

    [Fact]
    public void BomWinsOverProjectDefault_ButConflictingExplicitChoiceIsRejected()
    {
        byte[] raw = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("中文\r\n")];
        string root = Corpus(("a.txt", raw));
        var scan = Run("scan", "--root", root, "--default-encoding", "gbk", "--output", Snapshot);
        Assert.Equal(0, scan.Exit);
        Assert.Equal("utf-8", scan.Json.GetProperty("files")[0].GetProperty("encoding").GetString());
        Assert.Equal(3, Run("confirm", "--snapshot", Snapshot, "--path", "a.txt", "--encoding", "gbk", "--reason", "test").Exit);
        Assert.Equal(raw, File.ReadAllBytes(Path.Combine(root, "a.txt")));
    }

    [Fact]
    public void InvalidTailAfter64KiB_IsNotConfirmed()
    {
        byte[] raw = [.. Encoding.ASCII.GetBytes(new string('a', 100000)), 0xff];
        string root = Corpus(("a.txt", raw));
        Assert.Equal(3, Run("scan", "--root", root, "--default-encoding", "utf-8", "--output", Snapshot).Exit);
        Assert.Equal(3, Run("convert", "--snapshot", Snapshot, "--apply", "--log", Journal).Exit);
        Assert.Equal(raw, File.ReadAllBytes(Path.Combine(root, "a.txt")));
    }

    [Fact]
    public void StaleSnapshotAndStalePlan_RefuseAllSourceWrites()
    {
        byte[] raw = Encoding.GetEncoding(936).GetBytes("中文测试\r\n");
        string root = Corpus(("a.txt", raw), ("b.txt", raw));
        string plan = Path.Combine(_root, "plan.json");
        Assert.Equal(0, Run("scan", "--root", root, "--default-encoding", "gbk", "--output", Snapshot).Exit);
        Assert.Equal(0, Run("convert", "--snapshot", Snapshot, "--plan", plan).Exit);
        File.AppendAllText(Path.Combine(root, "b.txt"), "changed", Encoding.ASCII);
        Assert.Equal(3, Run("convert", "--snapshot", Snapshot, "--apply", "--log", Journal).Exit);
        Assert.Equal(3, Run("apply", "--plan", plan, "--log", Journal).Exit);
        Assert.Equal(raw, File.ReadAllBytes(Path.Combine(root, "a.txt")));
        Assert.False(File.Exists(Path.Combine(root, "a.txt.bak")));
    }

    [Fact]
    public void TamperedBackup_BlocksEntireRollback()
    {
        byte[] raw = Encoding.GetEncoding(936).GetBytes("中文测试\r\n");
        string root = Corpus(("a.txt", raw), ("b.txt", raw));
        Assert.Equal(0, Run("scan", "--root", root, "--default-encoding", "gbk", "--output", Snapshot).Exit);
        Assert.Equal(0, Run("convert", "--snapshot", Snapshot, "--apply", "--log", Journal).Exit);
        byte[] converted = File.ReadAllBytes(Path.Combine(root, "a.txt"));
        File.AppendAllText(Path.Combine(root, "b.txt.bak"), "tampered", Encoding.ASCII);
        Assert.Equal(3, Run("rollback", "--log", Journal, "--apply").Exit);
        Assert.Equal(converted, File.ReadAllBytes(Path.Combine(root, "a.txt")));
    }

    [Fact]
    public void ExistingBackup_IsNeverOverwrittenByAgentApply()
    {
        byte[] raw = Encoding.GetEncoding(936).GetBytes("中文测试\r\n");
        string root = Corpus(("a.txt", raw));
        File.WriteAllText(Path.Combine(root, "a.txt.bak"), "previous history");
        Assert.Equal(0, Run("scan", "--root", root, "--default-encoding", "gbk", "--output", Snapshot).Exit);
        Assert.Equal(3, Run("convert", "--snapshot", Snapshot, "--apply", "--log", Journal).Exit);
        Assert.Equal("previous history", File.ReadAllText(Path.Combine(root, "a.txt.bak")));
        Assert.Equal(raw, File.ReadAllBytes(Path.Combine(root, "a.txt")));
    }

    [Fact]
    public void MixedLineEndingsAndMissingFinalNewline_AreReportedAndPreserved()
    {
        const string text = "甲\r\n乙\n丙\r丁";
        string root = Corpus(("a.txt", Encoding.GetEncoding(936).GetBytes(text)));
        var scan = Run("scan", "--root", root, "--default-encoding", "gbk", "--output", Snapshot);
        JsonElement eol = scan.Json.GetProperty("files")[0].GetProperty("eol");
        Assert.Equal("mixed", eol.GetProperty("kind").GetString());
        Assert.False(eol.GetProperty("final_newline").GetBoolean());
        Assert.Equal(0, Run("convert", "--snapshot", Snapshot, "--apply", "--log", Journal).Exit);
        Assert.Equal(text, File.ReadAllText(Path.Combine(root, "a.txt")));
    }

    [Theory]
    [InlineData("../outside.txt")] [InlineData("..\\outside.txt")] [InlineData("C:\\outside.txt")]
    public void SnapshotPathEscape_IsRefused(string relative)
    {
        string root = Corpus(("a.txt", Encoding.ASCII.GetBytes("a")));
        Assert.Equal(0, Run("scan", "--root", root, "--output", Snapshot).Exit);
        AgentSnapshot snapshot = AgentCli.ReadSnapshot(Snapshot);
        snapshot.Files[0].Path = relative;
        AgentCli.WriteJson(Snapshot, snapshot);
        Assert.Equal(3, Run("convert", "--snapshot", Snapshot, "--apply", "--log", Journal).Exit);
        Assert.Equal("a", File.ReadAllText(Path.Combine(root, "a.txt")));
    }

    [Theory]
    [InlineData("--include")] [InlineData("--exclude")]
    public void EmptyMask_CannotWidenScan(string option)
    {
        string root = Corpus(("a.txt", Encoding.ASCII.GetBytes("a")));
        Assert.Equal(3, Run("scan", "--root", root, option, ",,,").Exit);
    }

    [Fact]
    public void RollbackReport_CannotReplaceAnUnchangedSource()
    {
        string root = Corpus(("a.txt", Encoding.GetEncoding(936).GetBytes("中文")), ("plain.txt", Encoding.ASCII.GetBytes("preserve")));
        Assert.Equal(0, Run("scan", "--root", root, "--default-encoding", "gbk", "--output", Snapshot).Exit);
        Assert.Equal(0, Run("convert", "--snapshot", Snapshot, "--apply", "--log", Journal).Exit);
        Assert.Equal(3, Run("rollback", "--log", Journal, "--apply", "--output", Path.Combine(root, "plain.txt")).Exit);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(root, "plain.txt")));
    }

    [Fact]
    public void FilePolicyOverridesProjectDefault_AndSnapshotOutputIsExcluded()
    {
        string root = Corpus(("legacy.txt", Convert.FromHexString("C2A9")), ("modern.txt", Encoding.UTF8.GetBytes("中文")));
        string policy = Path.Combine(_root, "policy.json");
        AgentCli.WriteJson(policy, new AgentPolicy { DefaultEncoding = "utf-8", Files = new() { ["legacy.txt"] = "gbk" } });
        string inside = Path.Combine(root, "snapshot.json");
        File.WriteAllText(inside, "old report");
        var scan = Run("scan", "--root", root, "--policy", policy, "--output", inside);
        Assert.Equal(0, scan.Exit);
        Assert.Equal(2, scan.Json.GetProperty("files").GetArrayLength());
        Assert.Equal("gbk", scan.Json.GetProperty("files")[0].GetProperty("encoding").GetString());
    }

    [Fact]
    public void NativeMain_RedirectedPipesProduceVersionWithoutAConsole()
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "EncodingChecker.exe"))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("--version");
        using var process = System.Diagnostics.Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10000));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(Program.GetDisplayVersion(), stdout.Trim());
        Assert.Equal("", stderr);
    }
}
