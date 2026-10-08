using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace EncodingChecker;

/// <summary>JSON entrypoint for agents; ordinary GUI/CLI behaviour remains upstream's.</summary>
public static partial class AgentCli
{
    public static int Run(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "help" or "--version")
            {
                Emit(new { version = Program.GetDisplayVersion(), agent_version = "1.0.0", schema_version = 2,
                    commands = new[] { "probe --path FILE [--encoding CODEC]", "scan --root DIR [--output JSON] [--default-encoding CODEC] [--policy JSON] [--include MASKS] [--exclude MASKS]", "confirm --snapshot JSON --path RELATIVE --encoding CODEC --reason TEXT", "convert --snapshot JSON [--plan JSON] [--apply --log JSON]", "apply --plan JSON --log JSON", "rollback --log JSON [--apply --output JSON]" },
                    note = "Scan is read-only. Compatibility is not confirmation. Conversion and rollback default to preview. Backups are retained and never silently overwritten." });
                return 0;
            }
            var options = new Arguments(args[1..]);
            object result;
            int exit;
            switch (args[0])
            {
                case "probe":
                    options.Allow("path", "encoding");
                    AgentFile file = Probe(options.Require("path"), options.Require("path"), options.Get("encoding"), null, true);
                    result = file;
                    exit = StatusExit(file);
                    break;
                case "scan":
                    options.Allow("root", "output", "default-encoding", "policy", "include", "exclude", "max-parallelism");
                    AgentSnapshot snapshot = Scan(options, cancellation.Token);
                    result = snapshot;
                    exit = !snapshot.Complete ? 3 : snapshot.Files.Any(row => StatusExit(row) == 2) ? 2 : 0;
                    break;
                case "confirm":
                    options.Allow("snapshot", "path", "encoding", "reason");
                    result = Confirm(options); exit = 0;
                    break;
                case "convert":
                    options.Allow("snapshot", "plan", "apply", "log", "max-parallelism");
                    (result, exit) = ConvertSnapshot(options, cancellation.Token);
                    break;
                case "apply":
                    options.Allow("plan", "log", "max-parallelism");
                    (result, exit) = ApplyAgentPlan(options.Require("plan"), options.Require("log"), options.Parallelism, cancellation.Token);
                    break;
                case "rollback":
                    options.Allow("log", "apply", "output");
                    (result, exit) = Rollback(options, cancellation.Token);
                    break;
                default: throw new ArgumentException("Unknown agent command. Use --help.");
            }
            Emit(result);
            return exit;
        }
        catch (OperationCanceledException)
        { Emit(new { schema_version = 2, success = false, cancelled = true }); return 4; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine(error.Message);
            Emit(new { schema_version = 2, success = false, error = error.Message, error_type = error.GetType().Name });
            return 3;
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static void Emit(object result) => Console.Out.WriteLine(JsonSerializer.Serialize(result, Json));
    private static int StatusExit(AgentFile file) => file.Status == "error" ? 3 : file.Status is "confirmed" or "ascii-compatible" or "binary" ? 0 : 2;

    internal sealed class Arguments
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);
        internal Arguments(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Options must start with --.");
                string key = args[i][2..];
                string? value = key == "apply" ? null : i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : throw new ArgumentException("Missing option value: " + key);
                if (!_values.TryAdd(key, value)) throw new ArgumentException("Duplicate option: " + key);
            }
        }
        internal void Allow(params string[] names)
        {
            foreach (string name in _values.Keys) if (!names.Contains(name)) throw new ArgumentException("Unknown option: " + name);
        }
        internal string? Get(string name) => _values.GetValueOrDefault(name);
        internal bool Has(string name) => _values.ContainsKey(name);
        internal string Require(string name) => !string.IsNullOrWhiteSpace(Get(name)) ? Get(name)! : throw new ArgumentException("Required option: --" + name);
        internal List<string> List(string name)
        {
            if (Get(name) is not { } value) return [];
            List<string> result = [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            if (result.Count == 0) throw new ArgumentException("Empty selection option: " + name);
            return result;
        }
        internal int Parallelism => Get("max-parallelism") is { } value ? int.TryParse(value, out int n) && n is > 0 and <= 64 ? n : throw new ArgumentException("Parallelism must be 1..64.") : ScanEngine.DefaultMaxParallelism;
    }
}
