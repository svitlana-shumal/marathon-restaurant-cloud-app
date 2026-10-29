using Amazon.CognitoIdentityProvider.Model;
using APIHandler.Constants.Dishes;
using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GuestSessions;
using APIHandler.Repositories.Auth;
using APIHandler.Repositories.Dishes;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using APIHandler.Services.GuestSessions;
using AutoFixture.Xunit2;
using Moq;
using NPOI.POIFS.Storage;
using System.Security.Cryptography;
using System.Text;

namespace APIHandler.Tests.Services;

public class GuestSessionServiceTests
{
    private readonly Mock<IGuestSessionRepository> _mockGuestSessionRepository = new();
    private readonly Mock<IGroupReservationRepository> _mockGroupReservationRepository = new();
    private readonly Mock<IDishRepository> _mockDishRepository = new();
    private readonly Mock<IReservationRepository> _mockReservationRepository = new();
    private readonly Mock<IUserRepository> _mockUserRepository = new();
    private readonly Mock<ITableRepository> _mockTableRepository = new();
    private readonly GuestSessionService _guestSessionService;

    public GuestSessionServiceTests()
    {
        _guestSessionService = new GuestSessionService(
            _mockGroupReservationRepository.Object,
            _mockGuestSessionRepository.Object,
            _mockDishRepository.Object,
            _mockReservationRepository.Object,
            _mockUserRepository.Object,
            _mockTableRepository.Object);
    }
    [Theory]
    [InlineAutoData(0)]
    [InlineAutoData(3)]
    public async Task CreateGuestSessionAsync_CorrectData_ReturnsSessionToken(
        int guestCount,
        CreateGuestSessionRequest request,
        GroupReservation groupReservation,
        Reservation parentReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;

        parentReservation.GuestCount = 5;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(parentReservation.Id))
            .ReturnsAsync(parentReservation);

        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync([]);

        GuestSession? session = null;
        _mockGuestSessionRepository
            .Setup(r => r.SaveAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(s => session = s);

        _mockGuestSessionRepository
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(guestCount);

        var response = await _guestSessionService.CreateGuestSessionAsync(request);

        Assert.NotEmpty(response.SessionId);
        Assert.NotEmpty(response.SessionToken);
        Assert.NotNull(session);
        Assert.Equal(groupReservation.GroupReservationId, session.GroupReservationId);
        Assert.Equal(request.GuestName, session.GuestName);
        Assert.NotNull(session.SessionTokenHash);
        Assert.NotEmpty(session.SessionTokenHash);
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_InvalidGroupReservationId_ThrowsGroupReservationNotFoundException(CreateGuestSessionRequest request)
    {
        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(request.GroupReservationId))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_GroupReservationExpired_ThrowsGroupReservationExpiredException(CreateGuestSessionRequest request, GroupReservation groupReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(request.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldAddNewDish_WhenDishDoesNotExistInSession(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;
        dish.State = DishState.Available;
        int quantityToAdd = 2;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        await _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantityToAdd);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(dish.Id, guestSession.Dishes[0].DishId);
        Assert.Equal(quantityToAdd, guestSession.Dishes[0].Quantity);
        Assert.Equal(OrderItemState.Pending, guestSession.Dishes[0].State);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldIncreaseQuantity_WhenDishAlreadyExists(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        int initialQuantity = 3;
        int quantityToAdd = 2;
        guestSession.Dishes = [new OrderItem { DishId = dish.Id, Quantity = initialQuantity, State = OrderItemState.Pending }];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;
        dish.State = DishState.Available;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        await _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantityToAdd);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(initialQuantity + quantityToAdd, guestSession.Dishes[0].Quantity);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [InlineAutoData(0)]
    [InlineAutoData(-1)]
    public async Task AddDishAsync_ShouldThrowValidationException_WhenQuantityIsZeroOrLess(int quantity, string guestSessionId, Dish dish)
    {
        dish.State = DishState.Available;

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.AddDishAsync(guestSessionId, dish.Id, quantity));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowGuestSessionNotFoundException_WhenSessionNotFound(string guestSessionId, Dish dish)
    {
        dish.State = DishState.Available;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        var exception = await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _guestSessionService.AddDishAsync(guestSessionId, dish.Id, 2));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowDishNotFound_WhenDishNotFound(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, string dishId)
    {
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);

        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([]);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.AddDishAsync(guestSession.Id, dishId, quantity: 2));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowDishNotAvailable_WhenDishIsNotAvailable(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        dish.State = DishState.OutOfStock;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        var exception = await Assert.ThrowsAsync<DishIsNotAvailableException>(() =>
            _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantity: 2));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task AddDishAsync_ShouldThrowGroupReservationExpired_WhenGroupStatusIsNotOpen(GroupReservationStatus status, Type exceptionType, GuestSession guestSession, GroupReservation groupReservation, Dish dish)
    {
        dish.State = DishState.Available;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = status;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        var exception = await Assert.ThrowsAsync(exceptionType, () =>
            _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantity: 2));
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldDecreaseQuantity_WhenQuantityIsGreaterThanAmountToRemove(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        int initialQuantity = 5;
        int quantityToRemove = 2;
        guestSession.Dishes =
        [
            new OrderItem { DishId = dishId, Quantity = initialQuantity, State = OrderItemState.Pending }
        ];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);


        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, quantityToRemove);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(initialQuantity - quantityToRemove, guestSession.Dishes[0].Quantity);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldRemoveDishFromList_WhenQuantityToRemoveIsEqualOrGreater(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        int initialQuantity = 2;
        int quantityToRemove = 3;
        guestSession.Dishes =
        [
            new OrderItem { DishId = dishId, Quantity = initialQuantity, State = OrderItemState.Pending }
        ];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, quantityToRemove);

        Assert.Empty(guestSession.Dishes);
        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowDishNotFoundException_WhenDishDoesNotExistInSession(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
           .ReturnsAsync(groupReservation);

        var exception = await Assert.ThrowsAsync<DishNotFoundException>(() =>
            _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, 2));

        Assert.Contains("not found in guest session", exception.Message);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowValidationException_WhenQuantityIsZeroOrLess(string guestSessionId, string dishId)
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, 0));

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, -5));
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowGuestSessionNotFoundException_WhenSessionNotFound(string guestSessionId, string dishId)
    {
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        var exception = await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, 2));
    }
    [Theory]
    [InlineAutoData(1, 0)]
    [InlineAutoData(1, 1)]
    [InlineAutoData(2, 1)]
    [InlineAutoData(2, 2)]
    public async Task CreateGuestSessionAsync_WhenCapacityReached_ThrowsGuestLimitExceededException(
        int reservationCapacity,
        int guestCount,
        CreateGuestSessionRequest request,
        GroupReservation groupReservation,
        Reservation parentReservation)
    {
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.ReservationId = parentReservation.Id;

        parentReservation.GuestCount = reservationCapacity;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(parentReservation.Id))
            .ReturnsAsync(parentReservation);

        _mockGuestSessionRepository
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(guestCount);

        var exception = await Assert.ThrowsAsync<GuestLimitExceededException>(() => _guestSessionService.CreateGuestSessionAsync(request));

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_MissingParentReservation_ThrowsGroupReservationExpiredException(
        CreateGuestSessionRequest request,
        GroupReservation groupReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(groupReservation.ReservationId))
            .ReturnsAsync((Reservation?)null);

        var exception = await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_ValidTokenAndId_ReturnsTrue(GuestSession guestSession, byte[] tokenBytes)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));
        guestSession.SessionTokenHash = hashString;

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync(guestSession);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSession.Id);

        Assert.True(result);
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_InvalidToken_ReturnsFalse(byte[] tokenBytes, string guestSessionId)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync((GuestSession?)null);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSessionId);

        Assert.False(result);
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_InvalidGuestSessionId_ReturnsFalse(GuestSession guestSession, byte[] tokenBytes, string guestSessionId)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));
        guestSession.SessionTokenHash = hashString;

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync(guestSession);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSessionId);

        Assert.False(result);
    }
    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_Valid_SubmitsSuccessfuilly(GuestSession guestSession, GroupReservation groupReservation, Dish[] dishes)
    {
        foreach (var dish in dishes)
            dish.State = DishState.Available;

        guestSession.IsSubmitted = false;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
           .ReturnsAsync(groupReservation);

        _mockDishRepository
            .Setup(d => d.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(dishes);

        GuestSession? session = null;
        _mockGuestSessionRepository
            .Setup(r => r.SaveAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(s => session = s);

        await _guestSessionService.SubmitCartAsync(guestSession.Id);

        Assert.NotNull(session);
        Assert.True(session.IsSubmitted);
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_SessionNotFound_ThrowsGuestSessionNotFound(string guestSessionId)
    {
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        await Assert.ThrowsAsync<GuestSessionNotFoundException>(() => _guestSessionService.SubmitCartAsync(guestSessionId));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task SubmitCartAsync_GroupReservationNotOpen_ThrowsException(GroupReservationStatus status, Type exceptionType, GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = status;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync(exceptionType, () => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_GuestSessionHasNoDishes_ThrowsCartIsEmptyException(GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<CartIsEmptyException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_GuestSessionSubmitted_ThrowsAlreadySubmited(GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.IsSubmitted = true;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GuestSessionSubmittedException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [InlineAutoData(DishState.OutOfStock)]
    [InlineAutoData(DishState.OnStop)]
    public async Task SubmitCartAsync_DishNotAvailable_ThrowsDishNotAvailable(DishState state, Dish dish, GuestSession guestSession, GroupReservation groupReservation)
    {
        dish.State = state;
        guestSession.IsSubmitted = false;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        guestSession.Dishes = 
        [
            new OrderItem()
            {
                DishId = dish.Id,
                Quantity = 1,
            }
        ];

        _mockDishRepository
            .Setup(d => d.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([dish]);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
           .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<DishIsNotAvailableException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }
    [Theory]
    [MemberAutoData(nameof(PepareDishesForGuestSession))]
    public async Task GetGuestSessionInfoAsync_Guest_ReturnsAsync(
        IEnumerable<string> dishIds,
        IEnumerable<Dish> dishes,
        List<OrderItem> orderItems,
        decimal expectedTotal,
        GuestSession session)
    {
        session.Dishes = orderItems;
        session.IsOwnerSession = false;

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(dishIds))
            .ReturnsAsync(dishes);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(session.Id))
            .ReturnsAsync(session);

        GetGuestSessionResponse result = await _guestSessionService.GetGuestSessionInfoAsync(session.Id);

        Assert.Equal(session.GuestName, result.Name);
        Assert.Equal(dishes.Count(), result.Items.Count());
        Assert.Equal(expectedTotal, result.Total);
    }

    [Theory]
    [MemberAutoData(nameof(PepareDishesForGuestSession))]
    public async Task GetGuestSessionInfoAsync_GroupOwner_ReturnsAsync(
        IEnumerable<string> dishIds,
        IEnumerable<Dish> dishes,
        List<OrderItem> orderItems,
        decimal expectedTotal,
        GuestSession session,
        GroupReservation groupReservation,
        Reservation reservation,
        string customerId,
        string ownerName,
        UserType userType)
    {
        session.Dishes = orderItems;
        session.IsOwnerSession = true;
        session.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.ReservationId = reservation.Id;
        reservation.CustomerId = customerId;
        userType.Attributes.Add(new AttributeType()
        {
            Name = "given_name",
            Value = ownerName,
        });

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(dishIds))
            .ReturnsAsync(dishes);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(session.Id))
            .ReturnsAsync(session);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _mockUserRepository
            .Setup(r => r.GetUserByIdAsync(customerId))
            .ReturnsAsync(userType);

        GetGuestSessionResponse result = await _guestSessionService.GetGuestSessionInfoAsync(session.Id);

        Assert.Equal(ownerName, result.Name);
        Assert.Equal(dishes.Count(), result.Items.Count());
        Assert.Equal(expectedTotal, result.Total);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_WhenOwnerSessionExists_ReturnsItsId()
    {
        const string userId = "owner-user";
        const string reservationId = "reservation-1";
        const string groupId = "group-1";
        var reservation = new Reservation
        {
            Id = reservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = userId
        };
        var group = new GroupReservation
        {
            GroupReservationId = groupId,
            ReservationId = reservationId,
            ReservationToken = "token"
        };
        var ownerSession = new GuestSession
        {
            Id = "owner-session-1",
            GroupReservationId = groupId,
            GuestName = "Owner",
            Dishes = [],
            IsOwnerSession = true,
            OwnerUserId = userId
        };

        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(reservation);
        _mockGroupReservationRepository
            .Setup(repository => repository.GetByReservationIdAsync(reservationId))
            .ReturnsAsync(group);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupId))
            .ReturnsAsync([ownerSession]);

        var response = await _guestSessionService.GetOwnerGuestSessionIdAsync(userId, reservationId);

        Assert.Equal(ownerSession.Id, response.GuestSessionId);
        _mockGuestSessionRepository.Verify(repository => repository.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_WhenOwnerSessionDoesNotExist_CreatesOne()
    {
        const string userId = "owner-user";
        const string reservationId = "reservation-1";
        const string groupId = "group-1";
        var reservation = new Reservation
        {
            Id = reservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = userId
        };
        var group = new GroupReservation
        {
            GroupReservationId = groupId,
            ReservationId = reservationId,
            ReservationToken = "token"
        };
        GuestSession? savedSession = null;

        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(reservation);
        _mockGroupReservationRepository
            .Setup(repository => repository.GetByReservationIdAsync(reservationId))
            .ReturnsAsync(group);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupId))
            .ReturnsAsync([]);
        _mockGuestSessionRepository
            .Setup(repository => repository.SaveAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(session => savedSession = session)
            .Returns(Task.CompletedTask);

        var response = await _guestSessionService.GetOwnerGuestSessionIdAsync(userId, reservationId);

        Assert.NotNull(savedSession);
        Assert.Equal(response.GuestSessionId, savedSession.Id);
        Assert.Equal(groupId, savedSession.GroupReservationId);
        Assert.True(savedSession.IsOwnerSession);
        Assert.Equal(userId, savedSession.OwnerUserId);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_ReturnsGuestParticipantsWithTotals_AndExcludesOwnerSession()
    {
        const string userId = "owner-user";
        const string groupReservationId = "group-1";
        const string reservationId = "reservation-1";
        var group = new GroupReservation
        {
            GroupReservationId = groupReservationId,
            ReservationId = reservationId,
            ReservationToken = "token"
        };
        var reservation = new Reservation
        {
            Id = reservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = userId
        };
        var ownerSession = new GuestSession
        {
            Id = "owner-session",
            GroupReservationId = groupReservationId,
            GuestName = "Owner",
            Dishes = [],
            IsOwnerSession = true
        };
        var guestSession = new GuestSession
        {
            Id = "guest-session",
            GroupReservationId = groupReservationId,
            GuestName = "Andrii",
            Dishes =
            [
                new OrderItem { DishId = "dish-1", Quantity = 2 },
                new OrderItem { DishId = "dish-2", Quantity = 1 }
            ],
            IsSubmitted = true
        };
        var secondGuestSession = new GuestSession
        {
            Id = "second-guest-session",
            GroupReservationId = groupReservationId,
            GuestName = "Olena",
            Dishes = [new OrderItem { DishId = "dish-2", Quantity = 1 }]
        };
        var dishes = new[]
        {
            new Dish { Id = "dish-1", Name = "Caesar Salad", Price = 180 },
            new Dish { Id = "dish-2", Name = "Tom Yum", Price = 220 }
        };

        _mockGroupReservationRepository
            .Setup(repository => repository.GetGroupReservationByIdAsync(groupReservationId))
            .ReturnsAsync(group);
        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(reservation);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupReservationId))
            .ReturnsAsync([ownerSession, guestSession, secondGuestSession]);
        _mockDishRepository
            .Setup(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(dishes);

        var result = await _guestSessionService.GetGroupSessionsAsync(userId, groupReservationId);

        Assert.Equal(2, result.Participants.Count());
        var participant = result.Participants.Single(p => p.Id == "guest-session");
        Assert.Equal("guest-session", participant.Id);
        Assert.Equal(ParticipantType.Guest, participant.Type);
        Assert.Equal("Andrii", participant.Name);
        Assert.True(participant.IsSubmitted);
        Assert.Equal(2, participant.Items.Count());
        Assert.Contains(participant.Items, item => item.Dish.Id == "dish-1" && item.Dish.Name == "Caesar Salad" && item.Dish.Price == 180);
        Assert.Equal(580, participant.Total);
        var secondParticipant = result.Participants.Single(p => p.Id == "second-guest-session");
        Assert.False(secondParticipant.IsSubmitted);
        Assert.Equal(220, secondParticipant.Total);
        Assert.Equal(800, result.Total);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_WhenGroupHasNoSessions_ReturnsEmptySnapshot()
    {
        const string userId = "owner-user";
        const string groupReservationId = "group-1";
        const string reservationId = "reservation-1";
        var group = new GroupReservation
        {
            GroupReservationId = groupReservationId,
            ReservationId = reservationId,
            ReservationToken = "token"
        };
        var reservation = new Reservation
        {
            Id = reservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = userId
        };

        _mockGroupReservationRepository
            .Setup(repository => repository.GetGroupReservationByIdAsync(groupReservationId))
            .ReturnsAsync(group);
        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(reservation);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupReservationId))
            .ReturnsAsync([]);
        _mockDishRepository
            .Setup(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([]);

        var result = await _guestSessionService.GetGroupSessionsAsync(userId, groupReservationId);

        Assert.Empty(result.Participants);
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_WhenCallerIsNotOwner_ThrowsActionForbiddenException()
    {
        const string groupReservationId = "group-1";
        const string reservationId = "reservation-1";
        var group = new GroupReservation
        {
            GroupReservationId = groupReservationId,
            ReservationId = reservationId,
            ReservationToken = "token"
        };
        var reservation = new Reservation
        {
            Id = reservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = "actual-owner"
        };

        _mockGroupReservationRepository
            .Setup(repository => repository.GetGroupReservationByIdAsync(groupReservationId))
            .ReturnsAsync(group);
        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(reservation);

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _guestSessionService.GetGroupSessionsAsync("different-user", groupReservationId));
        _mockGuestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_DeletesGuestAndReturnsUpdatedSnapshot()
    {
        const string userId = "owner-user";
        const string groupId = "group-1";
        const string reservationId = "reservation-1";
        var (group, reservation) = ConfigureGroupOwner(userId, groupId, reservationId);
        var deletedSession = new GuestSession
        {
            Id = "deleted-guest",
            GroupReservationId = groupId,
            GuestName = "Andrii",
            Dishes = [new OrderItem { DishId = "dish-1", Quantity = 5 }]
        };
        var remainingSession = new GuestSession
        {
            Id = "remaining-guest",
            GroupReservationId = groupId,
            GuestName = "Olena",
            Dishes = [new OrderItem { DishId = "dish-2", Quantity = 1 }]
        };
        var remainingDish = new Dish { Id = "dish-2", Name = "Tom Yum", Price = 220 };

        _mockGuestSessionRepository
            .Setup(repository => repository.GetByGuestSessionId(deletedSession.Id))
            .ReturnsAsync(deletedSession);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupId))
            .ReturnsAsync([remainingSession]);
        _mockDishRepository
            .Setup(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([remainingDish]);

        var result = await _guestSessionService.DeleteGuestSessionAsync(userId, groupId, deletedSession.Id);

        var participant = Assert.Single(result.Participants);
        Assert.Equal(remainingSession.Id, participant.Id);
        Assert.Equal("Olena", participant.Name);
        Assert.Equal(220, participant.Total);
        Assert.Equal(220, result.Total);
        Assert.Single(participant.Items);
        Assert.Equal("dish-2", participant.Items.Single().Dish.Id);
        _mockGuestSessionRepository.Verify(repository => repository.DeleteAsync(deletedSession), Times.Once);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_WhenSessionDoesNotExist_ThrowsGuestSessionNotFoundException()
    {
        const string groupId = "group-1";
        const string reservationId = "reservation-1";
        var (group, _) = ConfigureGroupOwner("owner-user", groupId, reservationId);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetByGuestSessionId("missing-session"))
            .ReturnsAsync((GuestSession?)null);

        await Assert.ThrowsAsync<GuestSessionNotFoundException>(
            () => _guestSessionService.DeleteGuestSessionAsync("owner-user", group.GroupReservationId, "missing-session"));
        _mockGuestSessionRepository.Verify(repository => repository.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_WhenSessionBelongsToAnotherGroup_ThrowsValidationException()
    {
        const string groupId = "group-1";
        const string reservationId = "reservation-1";
        var (group, _) = ConfigureGroupOwner("owner-user", groupId, reservationId);
        var session = new GuestSession
        {
            Id = "guest-session",
            GroupReservationId = "another-group",
            GuestName = "Guest",
            Dishes = []
        };
        _mockGuestSessionRepository
            .Setup(repository => repository.GetByGuestSessionId(session.Id))
            .ReturnsAsync(session);

        await Assert.ThrowsAsync<ValidationException>(
            () => _guestSessionService.DeleteGuestSessionAsync("owner-user", group.GroupReservationId, session.Id));
        _mockGuestSessionRepository.Verify(repository => repository.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_WhenTargetIsOwnerSession_ThrowsValidationException()
    {
        const string groupId = "group-1";
        const string reservationId = "reservation-1";
        var (group, _) = ConfigureGroupOwner("owner-user", groupId, reservationId);
        var ownerSession = new GuestSession
        {
            Id = "owner-session",
            GroupReservationId = groupId,
            GuestName = "Owner",
            Dishes = [],
            IsOwnerSession = true,
            OwnerUserId = "owner-user"
        };
        _mockGuestSessionRepository
            .Setup(repository => repository.GetByGuestSessionId(ownerSession.Id))
            .ReturnsAsync(ownerSession);

        await Assert.ThrowsAsync<ValidationException>(
            () => _guestSessionService.DeleteGuestSessionAsync("owner-user", group.GroupReservationId, ownerSession.Id));
        _mockGuestSessionRepository.Verify(repository => repository.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_WhenCallerIsNotOwner_ThrowsActionForbiddenException()
    {
        var (group, _) = ConfigureGroupOwner("real-owner", "group-1", "reservation-1");

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _guestSessionService.DeleteGuestSessionAsync("other-user", group.GroupReservationId, "guest-session"));
        _mockGuestSessionRepository.Verify(repository => repository.GetByGuestSessionId(It.IsAny<string>()), Times.Never);
        _mockGuestSessionRepository.Verify(repository => repository.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    private (GroupReservation Group, Reservation Reservation) ConfigureGroupOwner(
        string ownerId,
        string groupId,
        string reservationId)
    {
        var group = new GroupReservation
        {
            GroupReservationId = groupId,
            ReservationId = reservationId,
            ReservationToken = "token"
        };
        var reservation = new Reservation
        {
            Id = reservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = ownerId
        };

        _mockGroupReservationRepository
            .Setup(repository => repository.GetGroupReservationByIdAsync(groupId))
            .ReturnsAsync(group);
        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(reservation);
        return (group, reservation);
    }

    public static TheoryData<IEnumerable<string>, IEnumerable<Dish>, List<OrderItem>, decimal> PepareDishesForGuestSession()
    {
        var dishMap = new Dictionary<string, Dish>()
        {
            ["1"] = new()
            {
                Id = "1",
                Price = 1
            },
            ["2"] = new()
            {
                Id = "2",
                Price = 2
            },
            ["3"] = new()
            {
                Id = "3",
                Price = 3
            }
        };

        var orderItems = new List<OrderItem>
        {
            new OrderItem()
                    {
                        DishId = "1",
                        Quantity = 2
                    },
                    new OrderItem()
                    {
                        DishId = "2",
                        Quantity = 3
                    },
                    new OrderItem()
                    {
                        DishId = "3",
                        Quantity = 1
                    }
        };

        var expectedTotal = orderItems.Sum(o => dishMap[o.DishId].Price * o.Quantity);

        return new()
        {
            { dishMap.Keys, dishMap.Values, orderItems, expectedTotal }
        };
    }
}
