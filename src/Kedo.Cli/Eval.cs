using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Kedo.Compiler.Evaluation;

namespace Kedo.Cli;

/// <summary>
/// `kedo eval <suite.json>` (#17): prepares a workspace per task from the suite's base content, lets an agent work in it
/// (`--agent="<command>"` with `{prompt_file}`, `{prompt}` and `{workspace}` placeholders) or applies the reference
/// solution (`--reference`), then verifies the task's checks. The same suite compares an agent with and without skills
/// or MCP: the agent command decides what the agent gets.
/// </summary>
internal static class Eval
{
    public static int Run(string[] args, Dictionary<string, string> valued, bool json, bool reference, bool keep, bool mcp = false)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: kedo eval <suite.json> (--reference | --agent=\"<command with {prompt_file} {workspace}>\") [--skills=<skills dir>] [--mcp] [--only=a,b] [--workdir=<dir>] [--timeout=<seconds>] [--keep] [--json]");
            return 2;
        }

        valued.TryGetValue("--agent", out var agent);
        if (!reference && agent is null)
        {
            Console.Error.WriteLine("give --reference to check the suite itself, or --agent=\"...\" to evaluate an agent");
            return 2;
        }

        var suitePath = Path.GetFullPath(args[0]);
        var (baseRelative, tasks) = EvalSuite.Load(File.ReadAllText(suitePath));
        var baseDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(suitePath)!, baseRelative));
        var only = valued.TryGetValue("--only", out var list) ? new HashSet<string>(list.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal) : null;
        var workRoot = valued.TryGetValue("--workdir", out var wd) ? Path.GetFullPath(wd) : Path.Combine(Path.GetTempPath(), "kedo-eval-" + Environment.ProcessId);
        var timeout = valued.TryGetValue("--timeout", out var t) && int.TryParse(t, out var seconds) ? seconds : 600;
        var outcomes = new List<(EvalOutcome Outcome, double Seconds)>();
        foreach (var task in tasks.Where(t => only is null || only.Contains(t.Id)))
        {
            var workspace = Path.Combine(workRoot, task.Id);
            EvalSuite.Prepare(task, baseDir, workspace);
            var clock = Stopwatch.StartNew();
            if (reference)
            {
                EvalSuite.ApplySolution(task, workspace);
            }
            else
            {
                // The four configurations of anexo G §7.1: with or without skills, with or without the MCP server.
                if (valued.TryGetValue("--skills", out var skills))
                {
                    CopyDirectory(Path.GetFullPath(skills), Path.Combine(workspace, ".claude", "skills"));
                    CopyDirectory(Path.GetFullPath(skills), Path.Combine(workspace, ".agents", "skills"));
                }

                if (mcp)
                {
                    using var file = File.Create(Path.Combine(workspace, ".mcp.json"));
                    using var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
                    writer.WriteStartObject();
                    writer.WriteStartObject("mcpServers");
                    writer.WriteStartObject("kedo");
                    writer.WriteString("command", Environment.ProcessPath ?? "kedo");
                    writer.WriteStartArray("args");
                    writer.WriteStringValue("mcp");
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                RunAgent(agent!, task, workspace, timeout);
            }

            var outcome = EvalSuite.Verify(task, workspace);
            outcomes.Add((outcome, clock.Elapsed.TotalSeconds));
            if (!json)
            {
                Console.WriteLine($"{(outcome.Passed ? "pass" : "FAIL")}  {task.Id}{(outcome.Passed ? "" : ": " + string.Join(" | ", outcome.Failures))}");
            }

            if (!keep)
            {
                Directory.Delete(workspace, recursive: true);
            }
        }

        var passed = outcomes.Count(o => o.Outcome.Passed);
        if (json)
        {
            using var stream = Console.OpenStandardOutput();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            writer.WriteStartObject();
            writer.WriteString("suite", suitePath);
            writer.WriteString("mode", reference ? "reference" : "agent");
            writer.WriteNumber("passed", passed);
            writer.WriteNumber("total", outcomes.Count);
            writer.WriteStartArray("tasks");
            foreach (var (outcome, secs) in outcomes)
            {
                writer.WriteStartObject();
                writer.WriteString("id", outcome.Id);
                writer.WriteBoolean("passed", outcome.Passed);
                writer.WriteNumber("seconds", Math.Round(secs, 2));
                writer.WriteStartArray("failures");
                foreach (var f in outcome.Failures)
                {
                    writer.WriteStringValue(f);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine($"{passed}/{outcomes.Count} task(s) passed");
        }

        return passed == outcomes.Count ? 0 : 1;
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void RunAgent(string command, EvalTask task, string workspace, int timeoutSeconds)
    {
        var promptFile = Path.Combine(workspace, "TASK.md");
        File.WriteAllText(promptFile, task.Prompt + "\n\nThe workspace holds the content (.kedo files) and its schema in `schema/`. Leave every file valid: `kedo check --schema=schema .` must report no errors.\n", new UTF8Encoding(false));
        var line = command.Replace("{prompt_file}", promptFile).Replace("{workspace}", workspace).Replace("{prompt}", task.Prompt.Replace("\"", "'"));
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c " + line)
            : new ProcessStartInfo("/bin/sh", new[] { "-c", line });
        start.WorkingDirectory = workspace;
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            process.Kill(entireProcessTree: true);
        }

        File.WriteAllText(Path.Combine(workspace, "agent.log"), output.Result + "\n--- stderr\n" + error.Result);
    }
}
