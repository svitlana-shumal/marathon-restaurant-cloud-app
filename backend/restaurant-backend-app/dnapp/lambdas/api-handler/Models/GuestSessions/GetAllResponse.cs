using APIHandler.Models.Orders;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace APIHandler.Models.GuestSessions;

public record GetAllSessionsResponse(IEnumerable<Participant> Participants, decimal Total);

public record GetOwnerGuestSessionIdResponse(
    [property: JsonPropertyName("guestSessionId")] string GuestSessionId);

public record GetGroupSessionsResponse(
    [property: JsonPropertyName("participants")] IEnumerable<GroupSessionParticipant> Participants,
    [property: JsonPropertyName("total")] decimal Total);

public record GroupSessionParticipant(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("type")]
    [property: JsonConverter(typeof(JsonStringEnumConverter<ParticipantType>))] ParticipantType Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("items")] IEnumerable<GroupSessionOrderItem> Items,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("isSubmitted")] bool IsSubmitted);

public record GroupSessionOrderItem(
    [property: JsonPropertyName("dish")] GroupSessionDish Dish,
    [property: JsonPropertyName("quantity")] int Quantity);

public record GroupSessionDish(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("price")] decimal Price);

public record Participant(
    string? Id,
    [property: JsonConverter(typeof(JsonStringEnumConverter<ParticipantType>))] ParticipantType Type,
    string Name,
    IEnumerable<OrderItemResponse> Items,
    decimal Total,
    bool IsSubmitted);

public enum ParticipantType
{
    Owner,
    Guest
}
