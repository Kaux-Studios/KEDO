using Kedo;

public class VersionTests
{
    [Fact]
    public void ToolchainVersionBelongsToLanguageMajor()
    {
        Assert.StartsWith($"{KedoVersion.LanguageMajor}.", KedoVersion.Current);
    }
}
