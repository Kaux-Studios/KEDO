using Kedo;

public class VersionTests
{
    [Fact]
    public void ToolchainVersionBelongsToLanguageMajor()
    {
        Assert.StartsWith($"{KedoVersion.LanguageMajor}.", KedoVersion.Current);
    }

    [Fact]
    public void ToolchainVersionMatchesThePackageVersion()
    {
        // `kedo version` and the packages must say the same; release.yml also checks the tag against KedoVersion.Current.
        var props = System.Xml.Linq.XDocument.Load(Path.Combine(ParserTests.RepoRoot(), "Directory.Build.props"));
        string Value(string name) => props.Descendants(name).Single().Value;
        var suffix = Value("VersionSuffix");
        Assert.Equal(KedoVersion.Current, suffix.Length == 0 ? Value("VersionPrefix") : $"{Value("VersionPrefix")}-{suffix}");
    }
}
