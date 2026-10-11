namespace Hestia.Platform.Tests;

// T26 probe only: one failed test, to show the error annotation. Never merge.
public class ProbeFailureTests
{
    [Fact]
    public void ProbeFails() => Assert.Equal(1, 2);

    [Fact(Skip = "T26 probe: a skipped test")]
    public void ProbeSkipped() => Assert.True(true);
}
