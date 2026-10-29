using System.Text.Json.Serialization;

namespace APIHandler.Models.GroupReservation;

public class SubmitGroupReservationRequest
{
    [JsonPropertyName("reservationId")]
    public string? ReservationId { get; set; }
}

public record GroupPreOrderItemResponse(
    [property: JsonPropertyName("dishId")] string DishId,
    [property: JsonPropertyName("quantity")] int Quantity);
