using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

internal static class CodexTextLauncher
{
    private static readonly string LauncherDirectory =
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    private static readonly string Script = Path.Combine(LauncherDirectory, "encoding_guard.py");
    private static readonly string Python = ResolvePython();

    private static string ResolvePython()
    {
        var environmentOverride = Environment.GetEnvironmentVariable("CODEX_TEXT_PYTHON");
        if (!String.IsNullOrWhiteSpace(environmentOverride) && File.Exists(environmentOverride))
            return Path.GetFullPath(environmentOverride);

        var runtimePointer = Path.Combine(LauncherDirectory, "codex-text.runtime");
        if (File.Exists(runtimePointer))
        {
            var configured = File.ReadAllText(runtimePointer, Encoding.UTF8).Trim();
            if (!String.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return Path.GetFullPath(configured);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".conda", "envs", "codex", "python.exe");
    }

    private static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            return value;

        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static int Main(string[] args)
    {
        if (!File.Exists(Python) || !File.Exists(Script))
        {
            Console.Error.WriteLine("{\"error\":\"launcher_missing\",\"message\":\"Encoding Guard runtime was not found\"}");
            return 125;
        }

        var commandLine = new StringBuilder(Quote(Script));
        foreach (var arg in args)
            commandLine.Append(' ').Append(Quote(arg));

        var start = new ProcessStartInfo(Python, commandLine.ToString())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.EnvironmentVariables["PYTHONUTF8"] = "1";
        start.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

        try
        {
            using (var process = Process.Start(start))
            {
                var stdout = new Thread(() =>
                    process.StandardOutput.BaseStream.CopyTo(Console.OpenStandardOutput()));
                var stderr = new Thread(() =>
                    process.StandardError.BaseStream.CopyTo(Console.OpenStandardError()));
                stdout.Start();
                stderr.Start();
                process.WaitForExit();
                stdout.Join();
                stderr.Join();
                return process.ExitCode;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("{\"error\":\"launcher_failed\",\"message\":\"{0}\"}",
                error.Message.Replace("\\", "\\\\").Replace("\"", "\\\""));
            return 125;
        }
    }
}
