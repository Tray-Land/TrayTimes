using TrayTimes.Commute;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Commute;

[TestClass]
public sealed class SharedLocationTests
{
    [TestMethod]
    public async Task LocateAsync_SeveralRequestsInOneRefresh_LooksUpOnce()
    {
        // Arrange
        List<bool> asked = [];
        TaskCompletionSource<PlaceLocation> pending = new();
        SharedLocation location = new(quick =>
        {
            asked.Add(quick);
            return pending.Task;
        });

        // Act: two monitors ask while it's on its way, and a third after it arrived.
        Task<PlaceLocation> first = location.LocateAsync(quick: true);
        Task<PlaceLocation> second = location.LocateAsync(quick: false);
        pending.SetResult(new PlaceLocation(new GeoPoint(43, -88), Task.FromResult<GeoPoint?>(new GeoPoint(43.001, -88))));
        Task<PlaceLocation> late = location.LocateAsync(quick: false);

        // Assert: one quick lookup, its fix and refinement shared.
        CollectionAssert.AreEqual(new[] { true }, asked);
        Assert.AreSame(await first, await second);
        Assert.AreSame(await first, await late);
    }

    [TestMethod]
    public async Task LocateAsync_AfterBeginRefresh_LooksUpAgain()
    {
        // Arrange
        List<bool> asked = [];
        SharedLocation location = new(quick =>
        {
            asked.Add(quick);
            return Task.FromResult(new PlaceLocation(null, Error: "Fixture location off"));
        });
        await location.LocateAsync(quick: true);

        // Act
        location.BeginRefresh();
        await location.LocateAsync(quick: false);

        // Assert
        CollectionAssert.AreEqual(new[] { true, false }, asked);
    }
}
