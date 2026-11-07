using System.Text.Json;
using TrayTimes.Commute;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Commute;

[TestClass]
public sealed class GooglePlacesSearchTests
{
    [TestMethod]
    public void Parse_BusinessAndAddress_LabelsEachAndKeepsLocation()
    {
        // Arrange
        const string json = """
            {"places":[
              {"displayName":{"text":"Blue Bottle"},"shortFormattedAddress":"12 Main St, Madison","location":{"latitude":43.1,"longitude":-89.4}},
              {"displayName":{"text":"500 Elm St"},"formattedAddress":"500 Elm St, Madison, WI 53703, USA","location":{"latitude":43.2,"longitude":-89.3}}
            ]}
            """;

        // Act
        var results = GooglePlacesSearch.Parse(json);

        // Assert
        Assert.AreEqual(2, results.Count);
        Assert.AreEqual("Blue Bottle, 12 Main St, Madison", results[0].Address);
        Assert.AreEqual(new GeoPoint(43.1, -89.4), results[0].Location);
        Assert.AreEqual("Blue Bottle", results[0].Title);
        Assert.AreEqual("12 Main St, Madison", results[0].Subtitle);
        Assert.IsNull(results[1].Title);
        Assert.AreEqual("500 Elm St, Madison, WI 53703, USA", results[1].Address);
    }

    [TestMethod]
    public void Parse_NoPlacesOrNoLocation_SkipsThem()
    {
        Assert.AreEqual(0, GooglePlacesSearch.Parse("{}").Count);
        Assert.AreEqual(0, GooglePlacesSearch.Parse("""{"places":[{"displayName":{"text":"X"}}]}""").Count);
    }

    [TestMethod]
    public void BuildRequestBody_WithBias_IncludesCircleAndEscapesQuery()
    {
        // Act
        string body = GooglePlacesSearch.BuildRequestBody("joe's \"cafe\"", new GeoPoint(43, -89));

        // Assert
        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.AreEqual("joe's \"cafe\"", doc.RootElement.GetProperty("textQuery").GetString());
        Assert.AreEqual(43, doc.RootElement.GetProperty("locationBias").GetProperty("circle").GetProperty("center").GetProperty("latitude").GetDouble());
    }

    [TestMethod]
    public void BuildRequestBody_WithoutBias_OmitsLocationBias()
    {
        using JsonDocument doc = JsonDocument.Parse(GooglePlacesSearch.BuildRequestBody("cafe", null));
        Assert.IsFalse(doc.RootElement.TryGetProperty("locationBias", out _));
    }

    [TestMethod]
    public void ParseErrorMessage_ErrorBody_ReturnsMessage()
    {
        Assert.AreEqual("API key not valid", GooglePlacesSearch.ParseErrorMessage("""{"error":{"message":"API key not valid"}}"""));
        Assert.IsNull(GooglePlacesSearch.ParseErrorMessage("not json"));
    }
}
