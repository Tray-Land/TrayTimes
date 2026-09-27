using TrayTimes.Transit;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class ArrivalMergeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void WithSchedule_NothingScheduled_ReturnsLiveList()
    {
        // Arrange
        IReadOnlyList<Arrival> live = [CreateArrival(10)];

        // Act
        IReadOnlyList<Arrival> merged = ArrivalMerge.WithSchedule(live, []);

        // Assert
        Assert.AreSame(live, merged);
    }

    [TestMethod]
    public void WithSchedule_NoPredictionsForRoute_AddsWholeSchedule()
    {
        // Arrange
        IReadOnlyList<Arrival> live = [CreateArrival(10, routeId: "GRE")];
        IReadOnlyList<Arrival> scheduled = [CreateArrival(5, isRealTime: false), CreateArrival(20, isRealTime: false)];

        // Act
        IReadOnlyList<Arrival> merged = ArrivalMerge.WithSchedule(live, scheduled);

        // Assert
        Assert.HasCount(3, merged);
    }

    [TestMethod]
    public void WithSchedule_ScheduledNoLaterThanPredictions_Dropped()
    {
        // Arrange
        IReadOnlyList<Arrival> live = [CreateArrival(12)];
        IReadOnlyList<Arrival> scheduled = [CreateArrival(10, isRealTime: false), CreateArrival(14, isRealTime: false), CreateArrival(30, isRealTime: false)];

        // Act
        IReadOnlyList<Arrival> merged = ArrivalMerge.WithSchedule(live, scheduled);

        // Assert
        CollectionAssert.AreEqual(new[] { Now.AddMinutes(12), Now.AddMinutes(30) }, merged.Select(a => a.PredictedTime).ToArray());
        Assert.IsTrue(merged[0].IsRealTime);
    }

    [TestMethod]
    public void WithSchedule_PredictionForOtherDirection_KeepsSchedule()
    {
        // Arrange
        IReadOnlyList<Arrival> live = [CreateArrival(30, directionId: "SOUTH")];
        IReadOnlyList<Arrival> scheduled = [CreateArrival(10, isRealTime: false)];

        // Act
        IReadOnlyList<Arrival> merged = ArrivalMerge.WithSchedule(live, scheduled);

        // Assert
        Assert.HasCount(2, merged);
    }

    [TestMethod]
    public void WithSchedule_RouteAndDirectionInDifferentCase_StillMatch()
    {
        // Arrange
        IReadOnlyList<Arrival> live = [CreateArrival(30, routeId: "gre", directionId: "north")];
        IReadOnlyList<Arrival> scheduled = [CreateArrival(10, routeId: "GRE", isRealTime: false)];

        // Act
        IReadOnlyList<Arrival> merged = ArrivalMerge.WithSchedule(live, scheduled);

        // Assert
        Assert.HasCount(1, merged);
    }

    [TestMethod]
    public void WithSchedule_MixedTimes_SortsSoonestFirst()
    {
        // Arrange
        IReadOnlyList<Arrival> live = [CreateArrival(15, routeId: "GRE")];
        IReadOnlyList<Arrival> scheduled = [CreateArrival(5, isRealTime: false), CreateArrival(25, isRealTime: false)];

        // Act
        IReadOnlyList<Arrival> merged = ArrivalMerge.WithSchedule(live, scheduled);

        // Assert
        CollectionAssert.AreEqual(new[] { 5, 15, 25 }, merged.Select(a => (int)(a.PredictedTime - Now).TotalMinutes).ToArray());
    }

    private static Arrival CreateArrival(int minutes, string routeId = "14", string directionId = "NORTH", bool isRealTime = true) =>
        new("100", routeId, routeId, directionId, "Downtown", isRealTime ? "5301" : null, Now.AddMinutes(minutes), false, false, false, ArrivalStatus.Scheduled, isRealTime);
}
