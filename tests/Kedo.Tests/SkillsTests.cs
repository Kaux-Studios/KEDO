/// <summary>Skills live in .agents/skills (Codex, Cursor) and are mirrored to .claude/skills (Claude Code); they must not drift.</summary>
public class SkillsTests
{
    [Fact]
    public void ClaudeSkillsMirrorAgentsSkills()
    {
        var root = ParserTests.RepoRoot();
        var agents = Path.Combine(root, ".agents", "skills");
        var claude = Path.Combine(root, ".claude", "skills");
        var files = Directory.GetFiles(agents, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(agents, f)).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);
        foreach (var rel in files)
        {
            var mirror = Path.Combine(claude, rel);
            Assert.True(File.Exists(mirror), $"missing mirror {rel}; run tools/sync-skills.ps1");
            Assert.Equal(File.ReadAllText(Path.Combine(agents, rel)).Replace("\r\n", "\n"), File.ReadAllText(mirror).Replace("\r\n", "\n"));
        }
    }

    [Fact]
    public void EverySkillHasValidFrontmatter()
    {
        var root = Path.Combine(ParserTests.RepoRoot(), ".agents", "skills");
        foreach (var dir in Directory.GetDirectories(root))
        {
            var text = File.ReadAllText(Path.Combine(dir, "SKILL.md"));
            Assert.StartsWith("---\n", text.Replace("\r\n", "\n"));
            Assert.Contains($"name: {Path.GetFileName(dir)}\n", text.Replace("\r\n", "\n"));
            Assert.Contains("description: ", text);
            var description = text.Replace("\r\n", "\n").Split('\n').First(l => l.StartsWith("description: ", StringComparison.Ordinal));
            Assert.True(description.Length <= 1024 + 13, $"{dir}: description over 1024 chars");
        }
    }
}
