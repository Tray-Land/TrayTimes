using System.Net;
using System.Text;
using TrayTimes.Transit;
using TrayTimes.Transit.BusTime;

namespace TrayTimes.Tests.Transit.BusTime;

[TestClass]
public sealed class BusTimeTransitServiceTests
{
    private static readonly Uri BaseUri = new("https://bustime.example/api/v3/");

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void IsConfigured_WithoutApiKey_ReturnsFalse(string? apiKey)
    {
        // Arrange
        BusTimeTransitService service = new("Test", BaseUri, apiKey, TimeZoneInfo.Utc);

        // Act & Assert
        Assert.IsFalse(service.IsConfigured);
    }

    [TestMethod]
    public async Task GetRoutesAsync_WithoutApiKey_ThrowsWithoutCallingServer()
    {
        // Arrange
        StubHandler handler = new("{}");
        BusTimeTransitService service = new("Test", BaseUri, null, TimeZoneInfo.Utc, new HttpClient(handler));

        // Act & Assert
        await Assert.ThrowsExactlyAsync<TransitApiException>(() => service.GetRoutesAsync());
        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    public async Task GetArrivalsAsync_NoStops_ReturnsEmptyWithoutCallingServer()
    {
        // Arrange
        StubHandler handler = new("{}");
        BusTimeTransitService service = CreateService(handler);

        // Act
        IReadOnlyList<Arrival> arrivals = await service.GetArrivalsAsync([]);

        // Assert
        Assert.IsEmpty(arrivals);
        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    public async Task GetArrivalsAsync_MoreThanTenStops_RequestsOnlyTheFirstTen()
    {
        // Arrange
        StubHandler handler = new("""{"bustime-response":{"prd":[]}}""");
        BusTimeTransitService service = CreateService(handler);
        string[] stopIds = Enumerable.Range(1, 12).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();

        // Act
        await service.GetArrivalsAsync(stopIds);

        // Assert
        string query = Uri.UnescapeDataString(handler.Requests.Single().Query);
        StringAssert.Contains(query, "key=secret");
        StringAssert.EndsWith(query, "stpid=1,2,3,4,5,6,7,8,9,10");
    }

    [TestMethod]
    public async Task GetArrivalsAsync_ValidPredictions_ReturnsSoonestFirst()
    {
        // Arrange
        StubHandler handler = new("""
            {"bustime-response":{"prd":[
              {"typ":"A","stpid":"100","vid":"5501","rt":"14","rtdd":"14","rtdir":"NORTH","des":"Downtown","prdtm":"20260925 08:15","prdctdn":"15","dly":false,"dyn":0},
              {"typ":"D","stpid":"100","vid":"5502","rt":"GRE","rtdd":"GRE","rtdir":"SOUTH","des":"Airport","prdtm":"20260925 08:05:30","prdctdn":"DUE","dly":false,"dyn":0}
            ]}}
            """);
        BusTimeTransitService service = CreateService(handler);

        // Act
        IReadOnlyList<Arrival> arrivals = await service.GetArrivalsAsync(["100"]);

        // Assert
        Assert.HasCount(2, arrivals);
        Assert.AreEqual("GRE", arrivals[0].RouteId);
        Assert.IsTrue(arrivals[0].IsDue);
        Assert.IsTrue(arrivals[0].IsDeparture);
        Assert.AreEqual(new DateTimeOffset(2026, 9, 25, 8, 5, 30, TimeSpan.Zero), arrivals[0].PredictedTime);
        Assert.AreEqual("14", arrivals[1].RouteId);
        Assert.AreEqual(new DateTimeOffset(2026, 9, 25, 8, 15, 0, TimeSpan.Zero), arrivals[1].PredictedTime);
    }

    [TestMethod]
    public async Task GetArrivalsAsync_DynamicActions_MapsStatusAndDropsInvalidatedTrips()
    {
        // Arrange
        StubHandler handler = new("""
            {"bustime-response":{"prd":[
              {"stpid":"100","rt":"14","prdtm":"20260925 08:01","dyn":1},
              {"stpid":"100","rt":"14","prdtm":"20260925 08:02","dyn":4},
              {"stpid":"100","rt":"14","prdtm":"20260925 08:03","dyn":16},
              {"stpid":"100","rt":"14","prdtm":"20260925 08:04","dyn":17}
            ]}}
            """);
        BusTimeTransitService service = CreateService(handler);

        // Act
        IReadOnlyList<Arrival> arrivals = await service.GetArrivalsAsync(["100"]);

        // Assert
        CollectionAssert.AreEqual(
            new[] { ArrivalStatus.Canceled, ArrivalStatus.DropOffOnly },
            arrivals.Select(a => a.Status).ToArray());
    }

    [TestMethod]
    public async Task GetArrivalsAsync_DelayedCountdown_MarksArrivalDelayed()
    {
        // Arrange
        StubHandler handler = new("""{"bustime-response":{"prd":[{"stpid":"100","rt":"14","prdtm":"20260925 08:10","prdctdn":"DLY"}]}}""");
        BusTimeTransitService service = CreateService(handler);

        // Act
        IReadOnlyList<Arrival> arrivals = await service.GetArrivalsAsync(["100"]);

        // Assert
        Assert.IsTrue(arrivals.Single().IsDelayed);
    }

    [TestMethod]
    public async Task GetArrivalsAsync_NoArrivalsMessage_ReturnsEmpty()
    {
        // Arrange
        StubHandler handler = new("""{"bustime-response":{"error":[{"stpid":"100","msg":"No arrival times"}]}}""");
        BusTimeTransitService service = CreateService(handler);

        // Act
        IReadOnlyList<Arrival> arrivals = await service.GetArrivalsAsync(["100"]);

        // Assert
        Assert.IsEmpty(arrivals);
    }

    [TestMethod]
    public async Task GetRoutesAsync_ApiError_ThrowsWithServerMessage()
    {
        // Arrange
        StubHandler handler = new("""{"bustime-response":{"error":[{"msg":"Invalid API access key supplied"}]}}""");
        BusTimeTransitService service = CreateService(handler);

        // Act
        TransitApiException ex = await Assert.ThrowsExactlyAsync<TransitApiException>(() => service.GetRoutesAsync());

        // Assert
        Assert.AreEqual("Invalid API access key supplied", ex.Message);
    }

    [TestMethod]
    public async Task GetRoutesAsync_MalformedJson_ThrowsTransitApiException()
    {
        // Arrange
        StubHandler handler = new("not json");
        BusTimeTransitService service = CreateService(handler);

        // Act & Assert
        await Assert.ThrowsExactlyAsync<TransitApiException>(() => service.GetRoutesAsync());
    }

    [TestMethod]
    public async Task GetRoutesAsync_NumbersAndBlankFields_ReadsLeniently()
    {
        // Arrange: BusTime sometimes sends route IDs as numbers and leaves optional fields blank.
        StubHandler handler = new("""{"bustime-response":{"routes":[{"rt":14,"rtnm":"Forest Home","rtclr":"#ff0000","rtdd":""},{"rt":""}]}}""");
        BusTimeTransitService service = CreateService(handler);

        // Act
        IReadOnlyList<TransitRoute> routes = await service.GetRoutesAsync();

        // Assert
        Assert.AreEqual(new TransitRoute("14", "14", "Forest Home", "#ff0000"), routes.Single());
    }

    [TestMethod]
    public async Task GetPatternsAsync_UnorderedPoints_ReturnsPointsInSequence()
    {
        // Arrange
        StubHandler handler = new("""
            {"bustime-response":{"ptr":[
              {"pid":"1","rtdir":"NORTH","pt":[{"seq":2,"lat":43.2,"lon":-88},{"seq":1,"lat":43.1,"lon":-88}]},
              {"pid":"2","rtdir":"SOUTH","pt":[{"seq":1,"lat":43.1,"lon":-88}]}
            ]}}
            """);
        BusTimeTransitService service = CreateService(handler);

        // Act
        IReadOnlyList<RoutePattern> patterns = await service.GetPatternsAsync("14");

        // Assert: the single-point pattern can't be drawn, so it's dropped.
        RoutePattern pattern = patterns.Single();
        Assert.AreEqual("14", pattern.RouteId);
        CollectionAssert.AreEqual(new[] { new GeoPoint(43.1, -88), new GeoPoint(43.2, -88) }, pattern.Points.ToArray());
    }

    [TestMethod]
    public async Task GetVehiclesAsync_ServerError_ThrowsHttpRequestException()
    {
        // Arrange
        StubHandler handler = new("oops", HttpStatusCode.InternalServerError);
        BusTimeTransitService service = CreateService(handler);

        // Act & Assert
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => service.GetVehiclesAsync(["5501"]));
    }

    private static BusTimeTransitService CreateService(StubHandler handler) =>
        new("Test", BaseUri, "secret", TimeZoneInfo.Utc, new HttpClient(handler));

    /// <summary>Answers every request with a canned body and records the request URIs.</summary>
    private sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
