using TrayTimes.Commute;
using TrayTimes.Models;
using TrayTimes.Tests.Transit;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Commute;

[TestClass]
public sealed class DestinationSearchTests
{
    private static readonly GeoPoint Here = new(43, -88);

    [TestMethod]
    public async Task SearchAsync_InvalidProviderCoordinates_ReportsTheFailureAndKeepsValidResults()
    {
        // Arrange
        DestinationSearch search = new((_, _) => Task.FromResult<IReadOnlyList<GeocodeResult>>([new("Invalid", new GeoPoint(double.NaN, 0))]),
            (_, _, _) => Task.FromResult<IReadOnlyList<TransitStop>>([new("S:1", "Station", Here), new("S:2", "Invalid", new GeoPoint(91, 0))]));

        // Act
        DestinationSearchResults results = await search.SearchAsync("Station", Here);

        // Assert
        Assert.AreEqual("S:1", results.Items.Single().StopId);
        Assert.IsTrue(results.AddressFailed && results.StopsFailed);
    }

    [TestMethod]
    public async Task SearchAsync_NoLocation_KeepsAddressesAndExplainsWhyStopsWereNotSearched()
    {
        // Arrange
        DestinationSearch search = new((_, _) => Task.FromResult<IReadOnlyList<GeocodeResult>>([new("Station", Here)]),
            (_, _, _) => throw new AssertFailedException("Must not search stops without a location"));

        // Act
        DestinationSearchResults results = await search.SearchAsync("Station", null);

        // Assert
        Assert.HasCount(1, results.Items);
        Assert.IsTrue(results.StopsNeedLocation);
        Assert.IsFalse(results.StopsFailed);
    }

    [TestMethod]
    public async Task SearchAsync_StreetAddress_SearchesAddressesOnlyAfterSubmit()
    {
        // Arrange
        int addresses = 0, stops = 0;
        DestinationSearch search = new((query, _) =>
        {
            addresses++;
            Assert.AreEqual("123 Main Street", query);
            return Task.FromResult<IReadOnlyList<GeocodeResult>>([new(query, Here)]);
        }, (_, _, _) =>
        {
            stops++;
            return Task.FromResult<IReadOnlyList<TransitStop>>([]);
        });
        Assert.AreEqual(0, addresses);
        Assert.AreEqual(0, stops);

        // Act
        DestinationSearchResults results = await search.SearchAsync(" 123 Main Street ", Here);

        // Assert
        Assert.AreEqual(1, addresses);
        Assert.AreEqual(0, stops);
        Assert.IsNull(results.Items.Single().StopId);
        Assert.IsFalse(results.AddressFailed || results.StopsFailed);
    }

    [TestMethod]
    public async Task SearchAsync_StopName_ReturnsAddressesAndDistinctStopsWithOneTransitRequest()
    {
        // Arrange
        RequestTestHandler handler = new()
        {
            RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("""
                {"results":[{"global_stop_id":"S:1","stop_name":"Central Station","stop_lat":43,"stop_lon":-88},
                            {"global_stop_id":"S:1","stop_name":"Central Station","stop_lat":43,"stop_lon":-88}]}
                """)),
        };
        DestinationSearch search = new((_, _) => Task.FromResult<IReadOnlyList<GeocodeResult>>([new("Central Station", Here)]),
            handler.CreateService().SearchStopsAsync);

        // Act
        DestinationSearchResults results = await search.SearchAsync("Central Station", Here);

        // Assert
        Assert.AreEqual(1, handler.Attempts);
        Assert.HasCount(2, results.Items);
        Assert.AreEqual("S:1", results.Items[1].StopId);
    }

    [TestMethod]
    public async Task SearchAsync_NoAddressProvider_FallsBackToStopsEvenForAnAddress()
    {
        // Arrange
        int calls = 0;
        DestinationSearch search = new(null, (_, _, _) =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<TransitStop>>([new("S:1", "Main Street", Here)]);
        });

        // Act
        DestinationSearchResults results = await search.SearchAsync("123 Main Street", Here);

        // Assert
        Assert.AreEqual(1, calls);
        Assert.AreEqual("S:1", results.Items.Single().StopId);
    }

    [TestMethod]
    public async Task SearchAsync_OneProviderFails_KeepsOtherResultsAndReportsTheFailure()
    {
        // Arrange
        DestinationSearch search = new((_, _) => throw new HttpRequestException("Not logged"),
            (_, _, _) => Task.FromResult<IReadOnlyList<TransitStop>>([new("S:1", "Station", Here)]));

        // Act
        DestinationSearchResults results = await search.SearchAsync("Station", Here);

        // Assert
        Assert.HasCount(1, results.Items);
        Assert.IsTrue(results.AddressFailed);
        Assert.IsFalse(results.StopsFailed);
    }

    [TestMethod]
    public async Task SearchAsync_BothProvidersFail_ReportsBothFailures()
    {
        // Arrange
        DestinationSearch search = new((_, _) => throw new TaskCanceledException(),
            (_, _, _) => throw new TransitApiException("Not logged"));

        // Act
        DestinationSearchResults results = await search.SearchAsync("Station", Here);

        // Assert
        Assert.IsEmpty(results.Items);
        Assert.IsTrue(results.AddressFailed && results.StopsFailed);
    }

    [TestMethod]
    public async Task SearchAsync_CanceledSubmit_DoesNotReturnStaleResults()
    {
        // Arrange
        using CancellationTokenSource cancellation = new();
        DestinationSearch search = new(null, (_, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult<IReadOnlyList<TransitStop>>([new("S:1", "Station", Here)]);
        });

        // Act / Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => search.SearchAsync("Station", Here, cancellation.Token));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public async Task SearchAsync_EmptyQuery_RejectsWithoutCallingProviders(string query)
    {
        // Arrange
        DestinationSearch search = new(null, (_, _, _) => throw new AssertFailedException("Must not search"));

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentException>(() => search.SearchAsync(query, Here));
    }

    [TestMethod]
    public async Task SearchAsync_OverlongQuery_RejectsWithoutCallingProviders()
    {
        // Arrange
        DestinationSearch search = new(null, (_, _, _) => throw new AssertFailedException("Must not search"));

        // Act / Assert
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => search.SearchAsync(new string('x', DestinationSearch.MaxQueryLength + 1), Here));
    }

    [TestMethod]
    [DataRow("1234", true)]
    [DataRow("Central Station", true)]
    [DataRow("Main & Oak", true)]
    [DataRow("123 Main St", false)]
    [DataRow("100-102 Main St", false)]
    [DataRow("Main St, Milwaukee", false)]
    public void IsStopQuery_NameCodeOrAddress_ClassifiesWithoutRequests(string query, bool expected)
    {
        Assert.AreEqual(expected, DestinationSearch.IsStopQuery(query));
    }

    [TestMethod]
    public void AsPlace_StopResult_KeepsStopIdentityAsANewCustomPlace()
    {
        // Arrange
        DestinationResult result = new("Central Station", Here, "S:1");

        // Act
        FavoritePlace place = result.AsPlace();

        // Assert
        Assert.AreEqual(PlaceKind.Custom, place.Kind);
        Assert.AreEqual(result.Name, place.Name);
        Assert.AreEqual(Here, place.Location);
        Assert.AreEqual("S:1", place.StopId);
        Assert.AreNotEqual(place.Id, result.AsPlace().Id);
    }
}
