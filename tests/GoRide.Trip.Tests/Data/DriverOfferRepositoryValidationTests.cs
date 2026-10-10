using GoRide.Trip.Data;
using Moq;
using Xunit;

namespace GoRide.Trip.Tests.Data;

public class DriverOfferRepositoryValidationTests
{
    [Theory]
    [InlineData("Completed", "InProgress")]
    [InlineData("Accepted", "InProgress")]
    [InlineData("Pending", "Arrived")]
    [InlineData("InProgress", "Arrived")]
    [InlineData("Arrived", "Arrived")]
    [InlineData("Arrived", "Cancelled")]
    [InlineData("Arrived", null)]
    public async Task InvalidStep_IsRejectedWithoutOpeningConnection(string fromStatus, string? toStatus)
    {
        var factory = new Mock<IDbConnectionFactory>(MockBehavior.Strict);
        var repository = new DriverOfferRepository(factory.Object);

        Assert.Null(await repository.TryAdvanceStatusAsync("trip", "driver", fromStatus, toStatus!));

        factory.VerifyNoOtherCalls();
    }
}
