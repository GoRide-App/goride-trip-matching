using Xunit;

namespace GoRide.Trip.Tests;

/// <summary>
/// Proves the test project itself is wired correctly (references the main project,
/// restores, and runs) before anyone writes a real test against a real story.
/// Delete this once the first genuine test exists.
/// </summary>
public class SanityTests
{
    [Fact]
    public void TestProject_IsWiredCorrectly()
    {
        Assert.Equal(2, 1 + 1);
    }
}
