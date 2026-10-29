using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Helpers;
using APIHandler.Models.GroupReservation;
using APIHandler.Services.GroupReservations;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace APIHandler.Handlers.GroupReservations;

public class SubmitGroupReservationHandler(IGroupReservationService groupReservationService) : IApiRequestHandler
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<APIGatewayProxyResponse> HandleRequest(APIGatewayProxyRequest request, ILambdaContext context)
    {
        if (!request.TryGetPathValue("groupReservationId", out string? groupReservationId))
            return ResponseBuilder.CreateErrorResponse(400, "Missing groupReservationId in path parameters.", context.Logger);

        string? userId = UserContextHelper.GetAuthenticatedUserId(request);
        if (userId is null)
            return ResponseBuilder.CreateErrorResponse(401, "Unauthorized.", context.Logger);

        SubmitGroupReservationRequest? body;
        try
        {
            body = string.IsNullOrWhiteSpace(request.Body)
                ? null
                : JsonSerializer.Deserialize<SubmitGroupReservationRequest>(request.Body, RequestJsonOptions);
        }
        catch (JsonException ex)
        {
            return ResponseBuilder.CreateErrorResponse(400, "Request body must be valid JSON.", context.Logger, ex);
        }

        if (string.IsNullOrWhiteSpace(body?.ReservationId))
            return ResponseBuilder.CreateErrorResponse(400, "reservationId is required.", context.Logger);

        try
        {
            var response = await groupReservationService.SubmitGroupReservationAsync(
                userId,
                groupReservationId,
                body.ReservationId);
            return ResponseBuilder.CreateSuccessResponse(response);
        }
        catch (GroupReservationNotFoundException ex)
        {
            return ResponseBuilder.CreateErrorResponse(404, ex.Message, context.Logger);
        }
        catch (ReservationNotFoundException ex)
        {
            return ResponseBuilder.CreateErrorResponse(404, ex.Message, context.Logger);
        }
        catch (ActionForbiddenException ex)
        {
            return ResponseBuilder.CreateErrorResponse(403, ex.Message, context.Logger);
        }
        catch (ValidationException ex)
        {
            return ResponseBuilder.CreateErrorResponse(400, ex.Message, context.Logger);
        }
    }
}
