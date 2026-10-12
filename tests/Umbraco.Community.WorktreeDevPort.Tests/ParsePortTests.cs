namespace Umbraco.Community.WorktreeDevPort.Tests;

public class ParsePortTests
{
    [Theory]
    [MemberData(nameof(SavedPortCase.All), MemberType = typeof(SavedPortCase))]
    public void Reads_saved_text_as_expected(SavedPortCase testCase)
        => Assert.Equal(testCase.Port, WorktreeDevPortKestrelConfiguration.ParsePort(testCase.Text));
}
