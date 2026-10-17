using Kedo.Compiler.Evaluation;

/// <summary>The agent evaluation suite (#17) stays meaningful: every reference solution passes and no task passes untouched.</summary>
public class EvalTests
{
    private static (string Base, IReadOnlyList<EvalTask> Tasks) Suite()
    {
        var path = Path.Combine(ParserTests.RepoRoot(), "eval", "tasks.json");
        var (relative, tasks) = EvalSuite.Load(File.ReadAllText(path));
        return (Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, relative)), tasks);
    }

    [Fact]
    public void TheSuiteHasThirtyDistinctTasks()
    {
        var (_, tasks) = Suite();
        Assert.True(tasks.Count >= 30, $"{tasks.Count} tasks");
        Assert.Equal(tasks.Count, tasks.Select(t => t.Id).Distinct().Count());
        Assert.All(tasks, t => Assert.Contains(t.Checks, c => c.Kind == "clean"));
    }

    [Fact]
    public void EveryReferenceSolutionPassesAndNoTaskPassesUntouched()
    {
        var (baseDir, tasks) = Suite();
        var root = Path.Combine(Path.GetTempPath(), "kedo-eval-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var task in tasks)
            {
                var workspace = Path.Combine(root, task.Id);
                EvalSuite.Prepare(task, baseDir, workspace);
                Assert.False(EvalSuite.Verify(task, workspace).Passed, $"{task.Id} passes without any change");
                EvalSuite.ApplySolution(task, workspace);
                var outcome = EvalSuite.Verify(task, workspace);
                Assert.True(outcome.Passed, $"{task.Id}: {string.Join(" | ", outcome.Failures)}");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void TheTraceHostRunsAHookToItsEnd()
    {
        var (baseDir, tasks) = Suite();
        var task = tasks.First(t => t.Id == "hook-condition");
        var workspace = Path.Combine(Path.GetTempPath(), "kedo-eval-trace-" + Guid.NewGuid().ToString("N"));
        try
        {
            EvalSuite.Prepare(task, baseDir, workspace);
            EvalSuite.ApplySolution(task, workspace);
            var failing = task with { Checks = task.Checks.Select(c => c.Kind == "effect" ? c with { Expected = "nothing_like_this(" } : c).ToList() };
            var outcome = EvalSuite.Verify(failing, workspace);
            Assert.False(outcome.Passed);
            Assert.Contains(outcome.Failures, f => f.Contains("effect:maintenance(room:workshop)"));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
