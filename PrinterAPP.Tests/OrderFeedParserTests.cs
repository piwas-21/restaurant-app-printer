using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// Guards the fix for the prod poll-wedge bug (PR #61 / v1.0.20): a single un-deserialisable order in
/// the printer feed must NOT throw for the whole batch. <see cref="OrderFeedParser.Parse"/> deserialises
/// each order independently, so good orders still print and each bad one is captured as an error the
/// poll loop logs to the Errors/Diagnostics page and skips. These tests fail fast if that resilience regresses.
/// </summary>
public class OrderFeedParserTests
{
    // A delivery order whose deliveryAddress is a STRING — the exact drift that wedged prod: the model
    // types it as an object, so System.Text.Json throws for this one element.
    private const string MalformedDeliveryOrder = """
        {
          "orderNumber": "202607190099",
          "type": "Delivery",
          "deliveryAddress": "Rue du Rhone 12, 1204 Geneve",
          "items": [ { "productName": "Lahmacun", "quantity": 1 } ]
        }
        """;

    private const string GoodTakeawayOrder = """
        { "orderNumber": "202607190100", "type": "TakeAway", "deliveryAddress": null,
          "items": [ { "productName": "Pide", "quantity": 2 } ] }
        """;

    private static string Feed(params string[] orderJson) =>
        $$"""{ "data": { "items": [ {{string.Join(",", orderJson)}} ] } }""";

    [Fact]
    public void One_bad_order_is_skipped_and_the_rest_still_parse()
    {
        var result = OrderFeedParser.Parse(Feed(MalformedDeliveryOrder, GoodTakeawayOrder));

        // The good order survives — this is the whole point: one bad order no longer kills the batch.
        var order = Assert.Single(result.Orders);
        Assert.Equal("202607190100", order.OrderNumber);

        // The bad one is reported (not thrown), with its order number and index extracted for diagnostics.
        var error = Assert.Single(result.Errors);
        Assert.Equal(0, error.Index);
        Assert.Equal("202607190099", error.OrderNumber);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void All_valid_orders_parse_with_no_errors()
    {
        var result = OrderFeedParser.Parse(Feed(GoodTakeawayOrder, GoodTakeawayOrder));

        Assert.Equal(2, result.Orders.Count);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Empty_items_array_yields_no_orders_and_no_errors()
    {
        var result = OrderFeedParser.Parse("""{ "data": { "items": [] } }""");

        Assert.Empty(result.Orders);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parses_order_page_cursor_fields_and_accepts_legacy_responses_without_them()
    {
        var paged = OrderFeedParser.Parse("""{ "data": { "items": [], "hasMoreOrders": true, "nextOrderCursor": "next-1" } }""");
        var legacy = OrderFeedParser.Parse("""{ "data": { "items": [] } }""");

        Assert.True(paged.IsSuccess);
        Assert.True(paged.HasMoreOrders);
        Assert.Equal("next-1", paged.NextOrderCursor);
        Assert.True(legacy.IsSuccess);
        Assert.False(legacy.HasMoreOrders);
        Assert.Null(legacy.NextOrderCursor);
    }

    [Theory]
    [InlineData("""{ "data": { "items": [], "hasMoreOrders": true } }""")]
    [InlineData("""{ "data": { "items": [], "hasMoreOrders": true, "nextOrderCursor": " " } }""")]
    public void Rejects_more_order_pages_without_a_progressing_cursor(string json)
    {
        var result = OrderFeedParser.Parse(json);

        Assert.False(result.IsSuccess);
        Assert.Contains("cursor", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_data_envelope_yields_no_orders_and_no_errors()
    {
        var result = OrderFeedParser.Parse("""{ "data": null }""");

        Assert.Empty(result.Orders);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parses_frozen_section_identity_on_order_children()
    {
        const string item = """
        { "orderNumber": "SECTION-1", "type": "TakeAway", "items": [
            { "productName": "Tacos", "quantity": 1, "sideItems": [
              { "productName": "Kebab", "quantity": 2, "sectionId": "meat-section" }
            ] }
          ] }
        """;

        var result = OrderFeedParser.Parse(Feed(item));

        Assert.Equal("meat-section", Assert.Single(Assert.Single(result.Orders).Items).SideItems![0].SectionId);
    }

    [Fact]
    public void Case_insensitive_envelope_still_binds_items()
    {
        // Mirrors the old PropertyNameCaseInsensitive behaviour on the wrapper.
        var result = OrderFeedParser.Parse($$"""{ "Data": { "Items": [ {{GoodTakeawayOrder}} ] } }""");

        Assert.Single(result.Orders);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Invalid_json_body_is_reported_as_a_single_envelope_error()
    {
        var result = OrderFeedParser.Parse("not json at all");

        Assert.Empty(result.Orders);
        var error = Assert.Single(result.Errors);
        Assert.Equal(-1, error.Index);
        Assert.Null(error.OrderNumber);
    }

    [Fact]
    public void Bad_order_without_a_number_falls_back_to_its_index()
    {
        const string numberlessBadOrder = """{ "type": "Delivery", "deliveryAddress": "a string" }""";

        var result = OrderFeedParser.Parse(Feed(GoodTakeawayOrder, numberlessBadOrder));

        Assert.Single(result.Orders);
        var error = Assert.Single(result.Errors);
        Assert.Equal(1, error.Index);
        Assert.Null(error.OrderNumber);
    }

    [Fact]
    public void Every_order_bad_yields_no_orders_and_one_error_each()
    {
        var result = OrderFeedParser.Parse(Feed(MalformedDeliveryOrder, MalformedDeliveryOrder));

        Assert.Empty(result.Orders);
        Assert.Equal(2, result.Errors.Count);
        Assert.All(result.Errors, e => Assert.Equal("202607190099", e.OrderNumber));
    }

    [Theory]
    [InlineData("""{ "data": {} }""")]              // data present, items missing
    [InlineData("""{ "data": { "items": "oops" } }""")]  // items present but not an array
    public void Missing_or_non_array_items_yields_no_orders_and_no_errors(string json)
    {
        var result = OrderFeedParser.Parse(json);

        Assert.Empty(result.Orders);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Null_element_in_items_is_reported_as_an_error()
    {
        var result = OrderFeedParser.Parse("""{ "data": { "items": [ null ] } }""");

        Assert.Empty(result.Orders);
        var error = Assert.Single(result.Errors);
        Assert.Equal(0, error.Index);
        Assert.Null(error.OrderNumber);
    }

    [Fact]
    public void Non_string_order_number_on_a_bad_order_is_still_extracted()
    {
        // orderNumber as a JSON number: TryReadOrderNumber must fall back to its raw text for diagnostics.
        const string badOrderNumericNumber = """{ "orderNumber": 202607190099, "deliveryAddress": "a string" }""";

        var result = OrderFeedParser.Parse(Feed(badOrderNumericNumber));

        var error = Assert.Single(result.Errors);
        Assert.Equal("202607190099", error.OrderNumber);
    }

    [Fact]
    public void Non_scalar_order_number_is_not_extracted()
    {
        // orderNumber as an object (or any non-string/non-number kind) must NOT leak a raw "{...}" into
        // diagnostics — it's reported as an indexed failure with no number.
        const string badOrderObjectNumber = """{ "orderNumber": { "x": 1 }, "deliveryAddress": "a string" }""";

        var result = OrderFeedParser.Parse(Feed(badOrderObjectNumber));

        var error = Assert.Single(result.Errors);
        Assert.Equal(0, error.Index);
        Assert.Null(error.OrderNumber);
    }

    [Fact]
    public void Stable_table_identity_fields_survive_feed_deserialization()
    {
        const string json = """
            {
              "orderNumber": "202609160001",
              "type": "DineIn",
              "tableId": "11111111-1111-1111-1111-111111111111",
              "tableLabel": "T-QA",
              "tableNumber": null,
              "status": "Confirmed",
              "items": [ { "productName": "Pide", "quantity": 1 } ]
            }
            """;

        var result = OrderFeedParser.Parse(Feed(json));

        var order = Assert.Single(result.Orders);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), order.TableId);
        Assert.Equal("T-QA", order.TableLabel);
        Assert.Null(order.TableNumber);
    }

    [Fact]
    public void Null_or_empty_routing_states_retain_the_legacy_contract()
    {
        var nullStates = OrderFeedParser.Parse(Feed("""
            { "orderNumber": "LEGACY-NULL", "status": "Confirmed", "routingStates": null,
              "items": [ { "productName": "Pide", "quantity": 1 } ] }
            """));
        var emptyStates = OrderFeedParser.Parse(Feed("""
            { "orderNumber": "LEGACY-EMPTY", "status": "Confirmed", "routingStates": [],
              "items": [ { "productName": "Pide", "quantity": 1 } ] }
            """));

        Assert.Single(nullStates.Orders);
        Assert.Single(emptyStates.Orders);
        Assert.True(nullStates.IsSuccess);
        Assert.True(emptyStates.IsSuccess);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", 1, "FrontKitchen", "Queued")]
    [InlineData("11111111-1111-1111-1111-111111111111", 0, "FrontKitchen", "Queued")]
    [InlineData("11111111-1111-1111-1111-111111111111", 1, "FrontKitchen", "Printed")]
    public void Malformed_non_legacy_routes_fail_closed_without_returning_an_order(
        string jobId, int revision, string target, string status)
    {
        var json = $$"""
        {
          "orderNumber": "ROUTE-BAD",
          "status": "Confirmed",
          "routingStates": [
            { "jobId": "{{jobId}}", "revision": {{revision}}, "target": "{{target}}",
              "status": "{{status}}", "deviceId": "front-device" }
          ],
          "items": [ { "productName": "Soup", "quantity": 1 } ]
        }
        """;

        var result = OrderFeedParser.Parse(Feed(json));

        if (jobId == "11111111-1111-1111-1111-111111111111" && revision == 1 && status == "Printed")
        {
            Assert.Single(result.Orders);
            Assert.True(result.IsSuccess);
        }
        else
        {
            Assert.Empty(result.Orders);
            Assert.False(result.IsSuccess);
            Assert.Contains("routing", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Unknown_target_enum_fails_closed()
    {
        var result = OrderFeedParser.Parse(Feed("""
        {
          "orderNumber": "ROUTE-ENUM",
          "status": "Confirmed",
          "routingStates": [
            { "jobId": "11111111-1111-1111-1111-111111111111", "revision": 1,
              "target": 99, "status": "Queued", "deviceId": "front-device" }
          ],
          "items": [ { "productName": "Soup", "quantity": 1 } ]
        }
        """));

        Assert.Empty(result.Orders);
        Assert.False(result.IsSuccess);
        Assert.Contains("routing", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_status_enum_fails_closed()
    {
        var result = OrderFeedParser.Parse(Feed("""
        {
          "orderNumber": "ROUTE-STATUS",
          "status": "Confirmed",
          "routingStates": [
            { "jobId": "11111111-1111-1111-1111-111111111111", "revision": 1,
              "target": "FrontKitchen", "status": "FutureState", "deviceId": "front-device" }
          ],
          "items": [ { "productName": "Soup", "quantity": 1 } ]
        }
        """));

        Assert.Empty(result.Orders);
        Assert.False(result.IsSuccess);
        Assert.Contains("routing", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Duplicate_target_routes_fail_closed()
    {
        var result = OrderFeedParser.Parse(Feed("""
        {
          "orderNumber": "ROUTE-DUP",
          "status": "Confirmed",
          "routingStates": [
            { "jobId": "11111111-1111-1111-1111-111111111111", "revision": 1,
              "target": "FrontKitchen", "status": "Queued", "deviceId": "front-a" },
            { "jobId": "22222222-2222-2222-2222-222222222222", "revision": 1,
              "target": "FrontKitchen", "status": "Queued", "deviceId": "front-b" }
          ],
          "items": [ { "productName": "Soup", "quantity": 1 } ]
        }
        """));

        Assert.Empty(result.Orders);
        Assert.False(result.IsSuccess);
        Assert.Contains("duplicate", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }
}
