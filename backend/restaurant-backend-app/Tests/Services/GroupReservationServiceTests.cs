using APIHandler.Constants;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GroupReservation;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using APIHandler.Services.Auth;
using APIHandler.Services.GroupReservations;
using APIHandler.Services.Locations;
using APIHandler.Services.Orders;
using AutoFixture.Xunit2;
using Moq;

namespace APIHandler.Tests.Services;

public class GroupReservationServiceTests
{
    private readonly Mock<IGroupReservationRepository> _groupReservationRepositoryMock = new();
    private readonly Mock<ITableRepository> _tableRepositoryMock = new();
    private readonly Mock<IReservationRepository> _reservationRepositoryMock = new();
    private readonly Mock<IGuestSessionRepository> _guestSessionRepositoryMock = new();
    private readonly Mock<IUserService> _userServiceMock = new();
    private readonly Mock<ILocationService> _locationServiceMock = new();
    private readonly Mock<IOrderService> _orderServiceMock = new();

    private readonly GroupReservationService _service;

    public GroupReservationServiceTests()
    {
        _service = new GroupReservationService(
            _groupReservationRepositoryMock.Object,
            _tableRepositoryMock.Object,
            _reservationRepositoryMock.Object,
            _guestSessionRepositoryMock.Object,
            _userServiceMock.Object,
            _locationServiceMock.Object,
            _orderServiceMock.Object);
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationByTokenAsync_ValidInput_ReturnsGroupReservationInfo(
        GroupReservation groupReservation,
        Table table,
        Reservation reservation,
        Location location,
        string expectedUserName)
    {
        groupReservation.ReservationId = reservation.Id;
        groupReservation.Status = GroupReservationStatus.Open;
        reservation.TableId = table.Id;
        table.LocationId = location.Id;

        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync(groupReservation.ReservationToken))
            .ReturnsAsync(groupReservation);

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _guestSessionRepositoryMock
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(0);

        _tableRepositoryMock
            .Setup(r => r.GetTableAsync(table.Id))
            .ReturnsAsync(table);

        _locationServiceMock
            .Setup(s => s.GetLocationByIdAsync(table.LocationId))
            .ReturnsAsync(location);

        _userServiceMock
            .Setup(s => s.GetUserName(reservation.CustomerId!))
            .ReturnsAsync(expectedUserName);

        var result = await _service.GetGroupReservationByTokenAsync(groupReservation.ReservationToken);

        Assert.Equal(groupReservation.GroupReservationId, result.GroupReservationId);
        Assert.Equal(table.LocationId, result.LocationId);
        Assert.Equal(reservation.GuestCount, result.MaxGuestCount);
        Assert.Equal(reservation.StartTime, result.StartTime);
        Assert.Equal(reservation.EndTime, result.EndTime);
        Assert.Equal(table.TableNumber, result.TableNumber);
        Assert.Equal("Green & Tasty", result.RestaurantName);
        Assert.Equal(location.Address, result.LocationAddress);
        Assert.Equal(expectedUserName, result.InviterName);
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationByTokenAsync_InvalidToken_ThrowsNotFound(string token)
    {
        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync(token))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(() => _service.GetGroupReservationByTokenAsync(token));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Closed)]
    [InlineAutoData(GroupReservationStatus.Submitted)]
    public async Task GetGroupReservationByTokenAsync_GroupReservationIsNotOpen_ThrowsException(
        GroupReservationStatus status,
        GroupReservation groupReservation)
    {
        groupReservation.Status = status;

        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync(groupReservation.ReservationToken))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _service.GetGroupReservationByTokenAsync(groupReservation.ReservationToken));
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationByTokenAsync_WhenGuestCountIsMax_ThrowsGuestLimitExceeded(GroupReservation groupReservation, Reservation reservation)
    {
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = reservation.Id;

        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync(groupReservation.ReservationToken))
            .ReturnsAsync(groupReservation);

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _guestSessionRepositoryMock
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(reservation.GuestCount);

        await Assert.ThrowsAsync<GuestLimitExceededException>(() => _service.GetGroupReservationByTokenAsync(groupReservation.ReservationToken));
    }

    [Theory]
    [AutoData]
    public async Task CloseGroupReservationAsync_ValidInput_UpdatesGroupReservationStatus(GroupReservation groupReservation)
    {
        _groupReservationRepositoryMock
            .Setup(r => r.GetByReservationIdAsync(groupReservation.ReservationId))
            .ReturnsAsync(groupReservation);

        GroupReservation? savedGroupReservation = null;
        _groupReservationRepositoryMock
            .Setup(r => r.SaveGroupReservationAsync(It.IsAny<GroupReservation>()))
            .Callback<GroupReservation>(r => savedGroupReservation = r);

        await _service.CloseGroupReservationAsync(groupReservation.ReservationId);

        Assert.NotNull(savedGroupReservation);
        Assert.Equal(GroupReservationStatus.Closed, savedGroupReservation.Status);
    }

    [Theory]
    [AutoData]
    public async Task CloseGroupReservationAsync_GroupReservationNotFound_DoesNotThrowException(string reservationId)
    {
        _groupReservationRepositoryMock
            .Setup(r => r.GetByReservationIdAsync(reservationId))
            .ReturnsAsync((GroupReservation?)null);

        await _service.CloseGroupReservationAsync(reservationId);
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationInviteTokenAsync_ValidInput_ReturnsToken(Reservation reservation, GroupReservation groupReservation)
    {
        reservation.Status = ReservationStatus.Reserved;
        reservation.GuestCount = 3;
        groupReservation.ReservationId = reservation.Id;
        groupReservation.Status = GroupReservationStatus.Open;

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _groupReservationRepositoryMock
            .Setup(r => r.GetByReservationIdAsync(reservation.Id))
            .ReturnsAsync(groupReservation);

        GetGroupInviteTokenResponse result = await _service.GetGroupReservationInviteTokenAsync(reservation.Id);

        Assert.Equal(groupReservation.ReservationToken, result.Token);
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationInviteTokenAsync_InvalidReservationId_ThrowsNotFound(string reservationId)
    {
        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservationId))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(() => _service.GetGroupReservationInviteTokenAsync(reservationId));
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationInviteTokenAsync_OneGuest_ThrowsValidationException(Reservation reservation)
    {
        reservation.Status = ReservationStatus.Reserved;
        reservation.GuestCount = 1;

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        await Assert.ThrowsAsync<ValidationException>(() => _service.GetGroupReservationInviteTokenAsync(reservation.Id));
    }

    [Theory]
    [InlineAutoData(ReservationStatus.Completed)]
    [InlineAutoData(ReservationStatus.Cancelled)]
    [InlineAutoData(ReservationStatus.InProgress)]
    public async Task GetGroupReservationInviteTokenAsync_StatusIsNotReserved_ThrowsValidationException(ReservationStatus reservationStatus, Reservation reservation)
    {
        reservation.Status = reservationStatus;
        reservation.GuestCount = 1;

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        await Assert.ThrowsAsync<ValidationException>(() => _service.GetGroupReservationInviteTokenAsync(reservation.Id));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    public async Task GetGroupReservationInviteTokenAsync_GroupStatusIsNotOpen_ThrowsException(GroupReservationStatus status, Type type, Reservation reservation, GroupReservation groupReservation)
    {
        reservation.Status = ReservationStatus.Reserved;
        reservation.GuestCount = 3;
        groupReservation.ReservationId = reservation.Id;
        groupReservation.Status = status;

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _groupReservationRepositoryMock
            .Setup(r => r.GetByReservationIdAsync(reservation.Id))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync(type, () => _service.GetGroupReservationInviteTokenAsync(reservation.Id));
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_ConsolidatesSubmittedGuestItems_AndSubmitsOrderAndGroup()
    {
        const string userId = "owner-1";
        const string groupId = "group-1";
        const string reservationId = "reservation-1";
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
            ReservationToken = "token",
            Status = GroupReservationStatus.Open
        };
        var submittedGuest = new GuestSession
        {
            Id = "guest-1",
            GroupReservationId = groupId,
            GuestName = "Guest",
            IsSubmitted = true,
            Dishes =
            [
                new OrderItem { DishId = "dish-1", Quantity = 2 },
                new OrderItem { DishId = "dish-2", Quantity = 1 }
            ]
        };
        var secondSubmittedGuest = new GuestSession
        {
            Id = "guest-2",
            GroupReservationId = groupId,
            GuestName = "Another guest",
            IsSubmitted = true,
            Dishes = [new OrderItem { DishId = "dish-1", Quantity = 1 }]
        };
        var unsubmittedGuest = new GuestSession
        {
            Id = "guest-3",
            GroupReservationId = groupId,
            GuestName = "Not submitted",
            Dishes = [new OrderItem { DishId = "dish-2", Quantity = 8 }]
        };
        var ownerSession = new GuestSession
        {
            Id = "owner-session",
            GroupReservationId = groupId,
            GuestName = "Owner",
            IsOwnerSession = true,
            IsSubmitted = true,
            Dishes = [new OrderItem { DishId = "dish-2", Quantity = 10 }]
        };

        _groupReservationRepositoryMock
            .Setup(repository => repository.GetGroupReservationByIdAsync(groupId))
            .ReturnsAsync(group);
        _reservationRepositoryMock
            .Setup(repository => repository.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(reservation);
        _guestSessionRepositoryMock
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupId))
            .ReturnsAsync([submittedGuest, secondSubmittedGuest, unsubmittedGuest, ownerSession]);

        List<OrderItem>? orderItems = null;
        _orderServiceMock
            .Setup(service => service.AddGroupPreOrderAndSubmitAsync(reservationId, It.IsAny<List<OrderItem>>()))
            .Callback<string, List<OrderItem>>((_, items) => orderItems = items)
            .Returns(Task.CompletedTask);
        _groupReservationRepositoryMock
            .Setup(repository => repository.SaveGroupReservationAsync(It.IsAny<GroupReservation>()))
            .Returns(Task.CompletedTask);

        var result = await _service.SubmitGroupReservationAsync(userId, groupId, reservationId);

        Assert.Equal(2, result.Count);
        Assert.Equal(3, result.Single(item => item.DishId == "dish-1").Quantity);
        Assert.Equal(1, result.Single(item => item.DishId == "dish-2").Quantity);
        Assert.NotNull(orderItems);
        Assert.Equal(3, orderItems!.Single(item => item.DishId == "dish-1").Quantity);
        Assert.Equal(1, orderItems.Single(item => item.DishId == "dish-2").Quantity);
        Assert.Equal(GroupReservationStatus.Submitted, group.Status);
        _orderServiceMock.Verify(
            service => service.AddGroupPreOrderAndSubmitAsync(reservationId, It.IsAny<List<OrderItem>>()),
            Times.Once);
        _groupReservationRepositoryMock.Verify(
            repository => repository.SaveGroupReservationAsync(group),
            Times.Once);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_WhenNoGuestSessionSubmitted_ThrowsValidationException()
    {
        var (group, reservation) = ConfigureSubmission("owner-1", "group-1", "reservation-1");
        _guestSessionRepositoryMock
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(group.GroupReservationId))
            .ReturnsAsync([]);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.SubmitGroupReservationAsync("owner-1", group.GroupReservationId, reservation.Id));

        _orderServiceMock.Verify(
            service => service.AddGroupPreOrderAndSubmitAsync(It.IsAny<string>(), It.IsAny<List<OrderItem>>()),
            Times.Never);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_WhenSubmittedSessionsHaveNoDishes_ThrowsValidationException()
    {
        var (group, reservation) = ConfigureSubmission("owner-1", "group-1", "reservation-1");
        var session = new GuestSession
        {
            Id = "guest-1",
            GroupReservationId = group.GroupReservationId,
            GuestName = "Guest",
            IsSubmitted = true,
            Dishes = []
        };
        _guestSessionRepositoryMock
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(group.GroupReservationId))
            .ReturnsAsync([session]);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.SubmitGroupReservationAsync("owner-1", group.GroupReservationId, reservation.Id));

        _orderServiceMock.Verify(
            service => service.AddGroupPreOrderAndSubmitAsync(It.IsAny<string>(), It.IsAny<List<OrderItem>>()),
            Times.Never);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_WhenCallerIsNotOwner_ThrowsActionForbiddenException()
    {
        var (group, reservation) = ConfigureSubmission("owner-1", "group-1", "reservation-1");

        await Assert.ThrowsAsync<ActionForbiddenException>(() =>
            _service.SubmitGroupReservationAsync("other-user", group.GroupReservationId, reservation.Id));

        _guestSessionRepositoryMock.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()),
            Times.Never);
    }

    private (GroupReservation Group, Reservation Reservation) ConfigureSubmission(
        string ownerId,
        string groupId,
        string reservationId)
    {
        var group = new GroupReservation
        {
            GroupReservationId = groupId,
            ReservationId = reservationId,
            ReservationToken = "token",
            Status = GroupReservationStatus.Open
        };
        var reservation = new Reservation
        {
            Id = reservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = ownerId
        };
        _groupReservationRepositoryMock
            .Setup(repository => repository.GetGroupReservationByIdAsync(groupId))
            .ReturnsAsync(group);
        _reservationRepositoryMock
            .Setup(repository => repository.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(reservation);
        return (group, reservation);
    }
}
