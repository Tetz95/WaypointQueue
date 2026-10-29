using Model;
using Model.Ops;
using Moq;
using Track;
using WaypointQueue.Services;
using WaypointQueue.Wrappers;

namespace WaypointQueue.Tests
{
    public class UncouplingServiceTest
    {
        private readonly Mock<ICarService> _carServiceMock;
        private readonly Mock<IOpsControllerWrapper> _opsControllerWrapperMock;

        private readonly UncouplingService _sut;

        private Area SylvaArea = new();
        private Area WhittierArea = new();
        private Area BrysonArea = new();
        private Area DillsboroArea = new();

        public UncouplingServiceTest()
        {
            _carServiceMock = new Mock<ICarService>();
            _opsControllerWrapperMock = new Mock<IOpsControllerWrapper>();
            _sut = new UncouplingService(_carServiceMock.Object, _opsControllerWrapperMock.Object);
        }

        private void SetupAreaMocks()
        {
            SylvaArea = new Area()
            {
                identifier = "Sylva"
            };
            WhittierArea = new Area()
            {
                identifier = "Whittier"
            };
            BrysonArea = new Area()
            {
                identifier = "Bryson"
            };
            // No car in the mocked consists is bound for Dillsboro
            DillsboroArea = new Area()
            {
                identifier = "Dillsboro"
            };

            _opsControllerWrapperMock.Setup(m => m.TryGetAreaById("Sylva", out SylvaArea)).Returns(true);
            _opsControllerWrapperMock.Setup(m => m.TryGetAreaById("Whittier", out WhittierArea)).Returns(true);
            _opsControllerWrapperMock.Setup(m => m.TryGetAreaById("Bryson", out BrysonArea)).Returns(true);
            _opsControllerWrapperMock.Setup(m => m.TryGetAreaById("Dillsboro", out DillsboroArea)).Returns(true);
        }

        [Fact]
        public void FindCarToUncouple_FromEndA_Success()
        {
            // 0-1-2-3-4-5-6-7-8-9
            List<Car> consistFromEndA = GenerateConsist();
            List<Car> carsToCut =
            [
                GenerateMockCar(0),
                GenerateMockCar(1),
                GenerateMockCar(2),
            ];

            var (carToUncouple, endToUncouple) = _sut.FindCarToUncouple(carsToCut, consistFromEndA);

            Assert.Equal("2", carToUncouple.id);
            Assert.Equal(Car.LogicalEnd.B, endToUncouple);
        }

        [Fact]
        public void FindCarToUncouple_FromEndB_Success()
        {
            // 0-1-2-3-4-5-6-7-8-9
            List<Car> consistFromEndA = GenerateConsist();
            List<Car> carsToCut =
            [
                GenerateMockCar(7),
                GenerateMockCar(8),
                GenerateMockCar(9),
            ];

            var (carToUncouple, endToUncouple) = _sut.FindCarToUncouple(carsToCut, consistFromEndA);

            Assert.Equal("7", carToUncouple.id);
            Assert.Equal(Car.LogicalEnd.A, endToUncouple);
        }

        [Fact]
        public void FindCarToUncouple_FromEndB_OrderDoesNotMatter()
        {
            // 0-1-2-3-4-5-6-7-8-9
            List<Car> consistFromEndA = GenerateConsist();
            List<Car> carsToCut =
            [
                GenerateMockCar(9),
                GenerateMockCar(8),
                GenerateMockCar(7),
                GenerateMockCar(6),
            ];


            var (carToUncouple, endToUncouple) = _sut.FindCarToUncouple(carsToCut, consistFromEndA);

            Assert.Equal("6", carToUncouple.id);
            Assert.Equal(Car.LogicalEnd.A, endToUncouple);
        }

        [Fact]
        public void FindCarToUncouple_ThrowsException_WhenCarsAreNotSubsetOfConsist()
        {
            List<Car> consistFromEndA = GenerateConsist();
            List<Car> carsToCut =
            [
                GenerateMockCar(15),
                GenerateMockCar(16),
            ];

            Assert.Throws<InvalidOperationException>(() => _sut.FindCarToUncouple(carsToCut, consistFromEndA));
        }

        // <--- direction of couple means pickups always use far side
        // FAR  0-1-2-3-4-5-6-7-8-9-10  NEAR
        [Theory]
        [InlineData(4, 2, 3, "0", "2")]
        [InlineData(4, 150, 0, null, null)]
        [InlineData(9, 1, 9, "0", "8")]
        [InlineData(0, 1, 0, null, null)]
        [InlineData(1, 1, 1, "0", "0")]
        [InlineData(9, 5, 5, "0", "4")]
        public void FindPickupByCount(int indexOfCoupled, int carsToPickup, int expectedCount, string expectedFirstId, string expectedLastId)
        {
            SetupMockedConsist(out Car locomotive, out List<Car> consist);
            var mockedWaypoint = GenerateMockWaypoint(locomotive);

            mockedWaypoint.Setup(wp => wp.PostCouplingCutMode).Returns(ManagedWaypoint.PostCoupleCutType.Pickup);
            mockedWaypoint.Setup(wp => wp.NumberOfCarsToCut).Returns(carsToPickup);

            Car carCoupledTo = consist[indexOfCoupled];

            _carServiceMock.Setup(cs => cs.EnumerateCoupled(carCoupledTo, It.IsAny<Car.LogicalEnd>())).Returns(consist);

            var result = _sut.FindPickupOrDropoffByCount(mockedWaypoint.Object, carCoupledTo);

            Assert.Equal(expectedCount, result.Count);
            if (expectedCount > 0)
            {
                Assert.Equal(expectedFirstId, result.FirstOrDefault().id);
                Assert.Equal(expectedLastId, result.LastOrDefault().id);
            }
        }

        // <--- direction of couple means dropoffs always use far side
        // FAR  0-1-2-3-4-5-6-7-8-9-10  NEAR
        [Theory]
        [InlineData(4, 2, 7, "0", "6")]
        [InlineData(4, 150, 11, "0", "10")]
        [InlineData(6, 1, 8, "0", "7")]
        [InlineData(0, 1, 2, "0", "1")]
        [InlineData(9, 0, 0, null, null)]
        [InlineData(5, 0, 0, null, null)]
        [InlineData(10, 0, 0, null, null)]
        [InlineData(3, 2, 6, "0", "5")]
        public void FindDropoffByCount(int indexOfCoupled, int carsToDropoff, int expectedCount, string expectedFirstId, string expectedLastId)
        {
            SetupMockedConsist(out Car locomotive, out List<Car> consist);
            var mockedWaypoint = GenerateMockWaypoint(locomotive);

            mockedWaypoint.Setup(wp => wp.PostCouplingCutMode).Returns(ManagedWaypoint.PostCoupleCutType.Dropoff);
            mockedWaypoint.Setup(wp => wp.NumberOfCarsToCut).Returns(carsToDropoff);

            Car carCoupledTo = consist[indexOfCoupled];

            _carServiceMock.Setup(cs => cs.EnumerateCoupled(carCoupledTo, It.IsAny<Car.LogicalEnd>())).Returns(consist);

            var result = _sut.FindPickupOrDropoffByCount(mockedWaypoint.Object, carCoupledTo);

            Assert.Equal(expectedCount, result.Count);
            if (expectedCount > 0)
            {
                Assert.Equal(expectedFirstId, result.FirstOrDefault().id);
                Assert.Equal(expectedLastId, result.LastOrDefault().id);
            }
        }

        // S - Sylva, W - Whittier, B - Bryson, L - Loco
        // S-S-W-L-B-B-S-W-W-S-B
        // 0-1-2-3-4-5-6-7-8-9-10
        [Theory]
        [InlineData("Whittier", true, true, 2, "0", "1")]
        [InlineData("Whittier", true, false, 2, "10", "9")]
        [InlineData("Whittier", false, true, 3, "0", "2")]
        [InlineData("Whittier", false, false, 4, "10", "7")]
        [InlineData("Sylva", true, true, 0, null, null)]
        [InlineData("Sylva", true, false, 1, "10", "10")]
        [InlineData("Sylva", false, true, 2, "0", "1")]
        [InlineData("Sylva", false, false, 2, "10", "9")]
        [InlineData("Bryson", true, true, 4, "0", "3")]
        [InlineData("Bryson", true, false, 0, null, null)]
        [InlineData("Bryson", false, true, 6, "0", "5")]
        [InlineData("Bryson", false, false, 1, "10", "10")]
        [InlineData("Dillsboro", true, true, 0, null, null)]
        [InlineData("Dillsboro", false, true, 0, null, null)]
        [InlineData("Dillsboro", false, false, 0, null, null)]
        public void FindCutByDestinationArea(string areaName, bool excludeMatchFromCut, bool closestBlock, int expectedCount, string expectedFirstId, string expectedLastId)
        {
            SetupMockedConsist(out Car locomotive, out var _);

            var mockedWaypoint = GenerateMockWaypoint(locomotive);

            mockedWaypoint.Setup(wp => wp.UncoupleDestinationId).Returns(areaName);
            mockedWaypoint.Setup(wp => wp.ExcludeMatchingCarsFromCut).Returns(excludeMatchFromCut);
            mockedWaypoint.Setup(wp => wp.CountUncoupledFromNearestToWaypoint).Returns(closestBlock);

            mockedWaypoint.Setup(wp => wp.WillUncoupleByNoDestination).Returns(false);
            mockedWaypoint.Setup(wp => wp.WillUncoupleByDestinationTrack).Returns(false);
            mockedWaypoint.Setup(wp => wp.WillUncoupleByDestinationIndustry).Returns(false);
            mockedWaypoint.Setup(wp => wp.WillUncoupleByDestinationArea).Returns(true);

            _carServiceMock.Setup(s => s.GetEndsRelativeToLocation(It.IsAny<Car>(), It.IsAny<Location>())).Returns((Car.LogicalEnd.A, Car.LogicalEnd.B));

            var result = _sut.FindCutByDestination(mockedWaypoint.Object);

            Assert.Equal(expectedCount, result.Count);

            if (expectedCount > 0)
            {
                Assert.Equal(expectedFirstId, result.FirstOrDefault().id);
                Assert.Equal(expectedLastId, result.LastOrDefault().id);
            }
        }

        // S - Sylva, W - Whittier, B - Bryson, L - Loco
        // S-S-W-B-B-W-S-W-B-W-S-B-L
        // 0-1-2-3-4-5-6-7-8-9-10-11-12
        [Theory]
        [InlineData(5, "Whittier", true, true, 6, "5", "0")]
        [InlineData(5, "Whittier", true, false, 3, "0", "2")]
        [InlineData(5, "Whittier", false, true, 5, "4", "0")]
        [InlineData(5, "Whittier", false, false, 2, "0", "1")]
        [InlineData(5, "Bryson", true, true, 5, "4", "0")]
        [InlineData(5, "Bryson", true, false, 5, "0", "4")]
        [InlineData(5, "Bryson", false, true, 3, "2", "0")]
        [InlineData(5, "Bryson", false, false, 3, "0", "2")]
        [InlineData(5, "Sylva", true, true, 2, "1", "0")]
        [InlineData(5, "Sylva", true, false, 2, "0", "1")]
        [InlineData(5, "Sylva", false, true, 0, null, null)]
        [InlineData(5, "Sylva", false, false, 0, null, null)]
        [InlineData(5, "Dillsboro", false, true, 6, "0", "5")]
        [InlineData(5, "Dillsboro", false, false, 6, "0", "5")]
        public void FindPickupByDestinationArea(int indexOfCoupled, string areaName, bool excludeMatchFromCut, bool closestBlock, int expectedCount, string expectedFirstId, string expectedLastId)
        {
            SetupMockedPostCoupleConsist(out Car locomotive, out List<Car> consist);

            var mockedWaypoint = GenerateMockWaypoint(locomotive);

            mockedWaypoint.Setup(wp => wp.UncoupleDestinationId).Returns(areaName);
            mockedWaypoint.Setup(wp => wp.ExcludeMatchingCarsFromCut).Returns(excludeMatchFromCut);
            mockedWaypoint.Setup(wp => wp.CountUncoupledFromNearestToWaypoint).Returns(closestBlock);

            mockedWaypoint.Setup(wp => wp.WillUncoupleByNoDestination).Returns(false);
            mockedWaypoint.Setup(wp => wp.WillUncoupleByDestinationTrack).Returns(false);
            mockedWaypoint.Setup(wp => wp.WillUncoupleByDestinationIndustry).Returns(false);
            mockedWaypoint.Setup(wp => wp.WillUncoupleByDestinationArea).Returns(true);

            _carServiceMock.Setup(s => s.GetEndsRelativeToLocation(It.IsAny<Car>(), It.IsAny<Location>())).Returns((Car.LogicalEnd.A, Car.LogicalEnd.B));

            Car carCoupledTo = consist[indexOfCoupled];
            _carServiceMock.Setup(cs => cs.EnumerateCoupled(carCoupledTo, It.IsAny<Car.LogicalEnd>())).Returns(consist);

            var result = _sut.FindPickupByDestination(mockedWaypoint.Object, carCoupledTo);

            Assert.Equal(expectedCount, result.Count);

            if (expectedCount > 0)
            {
                Assert.Equal(expectedFirstId, result.FirstOrDefault().id);
                Assert.Equal(expectedLastId, result.LastOrDefault().id);
            }
        }

        // S - Sylva, W - Whittier, B - Bryson, L - Loco
        // S-S-W-B-B-W-S-W-B-W-S-B-L
        // 0-1-2-3-4-5-6-7-8-9-10-11-12
        [Theory]
        [InlineData(5, "Whittier", true, true, 7, "0", "6")]
        [InlineData(5, "Whittier", true, false, 9, "0", "8")]
        [InlineData(5, "Whittier", false, true, 8, "0", "7")]
        [InlineData(5, "Whittier", false, false, 10, "0", "9")]
        [InlineData(5, "Bryson", true, true, 8, "0", "7")]
        [InlineData(5, "Bryson", true, false, 11, "0", "10")]
        [InlineData(5, "Bryson", false, true, 9, "0", "8")]
        [InlineData(5, "Bryson", false, false, 12, "0", "11")]
        [InlineData(5, "Sylva", true, true, 0, null, null)]
        [InlineData(5, "Sylva", true, false, 10, "0", "9")]
        [InlineData(5, "Sylva", false, true, 7, "0", "6")]
        [InlineData(5, "Sylva", false, false, 11, "0", "10")]
        [InlineData(5, "Dillsboro", false, true, 0, null, null)]
        [InlineData(5, "Dillsboro", false, false, 0, null, null)]
        public void FindDropoffByDestinationArea(int indexOfCoupled, string areaName, bool excludeMatchFromCut, bool closestBlock, int expectedCount, string expectedFirstId, string expectedLastId)
        {
            SetupMockedPostCoupleConsist(out Car locomotive, out List<Car> consist);

            var mockedWaypoint = GenerateMockWaypoint(locomotive);

            mockedWaypoint.Setup(wp => wp.UncoupleDestinationId).Returns(areaName);
            mockedWaypoint.Setup(wp => wp.ExcludeMatchingCarsFromCut).Returns(excludeMatchFromCut);
            mockedWaypoint.Setup(wp => wp.CountUncoupledFromNearestToWaypoint).Returns(closestBlock);

            mockedWaypoint.Setup(wp => wp.WillUncoupleByNoDestination).Returns(false);
            mockedWaypoint.Setup(wp => wp.WillUncoupleByDestinationTrack).Returns(false);
            mockedWaypoint.Setup(wp => wp.WillUncoupleByDestinationIndustry).Returns(false);
            mockedWaypoint.Setup(wp => wp.WillUncoupleByDestinationArea).Returns(true);

            _carServiceMock.Setup(s => s.GetEndsRelativeToLocation(It.IsAny<Car>(), It.IsAny<Location>())).Returns((Car.LogicalEnd.A, Car.LogicalEnd.B));

            Car carCoupledTo = consist[indexOfCoupled];
            List<Car> reversedConsist = [.. consist];
            reversedConsist.Reverse();
            _carServiceMock.Setup(cs => cs.EnumerateCoupled(carCoupledTo, Car.LogicalEnd.A)).Returns(reversedConsist);
            _carServiceMock.Setup(cs => cs.EnumerateCoupled(carCoupledTo, Car.LogicalEnd.B)).Returns(consist);

            var result = _sut.FindDropoffByDestination(mockedWaypoint.Object, carCoupledTo);

            Assert.Equal(expectedCount, result.Count);

            if (expectedCount > 0)
            {
                Assert.Equal(expectedFirstId, result.FirstOrDefault().id);
                Assert.Equal(expectedLastId, result.LastOrDefault().id);
            }
        }

        [Theory]
        [InlineData(4, 1, "0", "0")]
        [InlineData(9, 8, "7", "0")]
        [InlineData(7, 8, "7", "0")]
        public void FindPickupAllExceptLocomotives(int indexOfCoupled, int expectedCount, string expectedFirstId, string expectedLastId)
        {
            var locoMock = GenerateMockCar(0, isLoco: true);
            var locoMock2 = GenerateMockCar(7, isLoco: true);
            var locoMock3 = GenerateMockCar(12, isLoco: true);
            List<Car> consist =
            [
                locoMock,
                GenerateMockCar(1, SylvaArea),
                GenerateMockCar(2, WhittierArea),
                GenerateMockCar(3, BrysonArea),
                GenerateMockCar(4, BrysonArea),
                GenerateMockCar(5, WhittierArea),
                GenerateMockCar(6, SylvaArea),
                locoMock2,
                GenerateMockCar(8, BrysonArea),
                GenerateMockCar(9, WhittierArea),
                GenerateMockCar(10, SylvaArea),
                GenerateMockCar(11, BrysonArea),
                locoMock3
            ];

            var mockedWaypoint = GenerateMockWaypoint(locoMock3);

            mockedWaypoint.Setup(w => w.CountUncoupledFromNearestToWaypoint).Returns(true);

            _carServiceMock.Setup(s => s.GetEndsRelativeToLocation(It.IsAny<Car>(), It.IsAny<Location>())).Returns((Car.LogicalEnd.A, Car.LogicalEnd.B));

            Car carCoupledTo = consist[indexOfCoupled];
            List<Car> reversedConsist = [.. consist];
            reversedConsist.Reverse();
            _carServiceMock.Setup(cs => cs.EnumerateCoupled(carCoupledTo, Car.LogicalEnd.A)).Returns(reversedConsist);
            _carServiceMock.Setup(cs => cs.EnumerateCoupled(carCoupledTo, Car.LogicalEnd.B)).Returns(consist);

            var result = _sut.FindPickupAllExceptLocomotives(mockedWaypoint.Object, carCoupledTo);

            Assert.Equal(expectedCount, result.Count);

            if (expectedCount > 0)
            {
                Assert.Equal(expectedFirstId, result.FirstOrDefault().id);
                Assert.Equal(expectedLastId, result.LastOrDefault().id);
            }
        }

        [Theory]
        [InlineData(4, 7, "0", "6", true)]
        [InlineData(9, 12, "0", "11", true)]
        // Locomotive 7 hauled dead, in tow: it's dropped with the cars, up to the working locomotive 12
        [InlineData(4, 12, "0", "11", false)]
        public void FindDropoffAllExceptLocomotives(int indexOfCoupled, int expectedCount, string expectedFirstId, string expectedLastId, bool loco7Working)
        {
            var locoMock = GenerateMockCar(7, isLoco: true);
            var locoMock2 = GenerateMockCar(12, isLoco: true);

            List<Car> consist =
            [
                GenerateMockCar(0, SylvaArea),
                GenerateMockCar(1, SylvaArea),
                GenerateMockCar(2, WhittierArea),
                GenerateMockCar(3, BrysonArea),
                GenerateMockCar(4, BrysonArea),
                GenerateMockCar(5, WhittierArea),
                GenerateMockCar(6, SylvaArea),
                locoMock,
                GenerateMockCar(8, BrysonArea),
                GenerateMockCar(9, WhittierArea),
                GenerateMockCar(10, SylvaArea),
                GenerateMockCar(11, BrysonArea),
                locoMock2
            ];

            var mockedWaypoint = GenerateMockWaypoint(locoMock2);

            mockedWaypoint.Setup(w => w.CountUncoupledFromNearestToWaypoint).Returns(true);

            _carServiceMock.Setup(s => s.GetEndsRelativeToLocation(It.IsAny<Car>(), It.IsAny<Location>())).Returns((Car.LogicalEnd.A, Car.LogicalEnd.B));

            Car carCoupledTo = consist[indexOfCoupled];
            List<Car> reversedConsist = [.. consist];
            reversedConsist.Reverse();
            _carServiceMock.Setup(cs => cs.EnumerateCoupled(carCoupledTo, Car.LogicalEnd.A)).Returns(reversedConsist);
            _carServiceMock.Setup(cs => cs.EnumerateCoupled(carCoupledTo, Car.LogicalEnd.B)).Returns(consist);

            _carServiceMock.Setup(cs => cs.GetWorkingLocomotiveIds(It.IsAny<BaseLocomotive>())).Returns(loco7Working ? ["7", "12"] : ["12"]);

            var result = _sut.FindDropoffAllExceptLocomotives(mockedWaypoint.Object, carCoupledTo);

            Assert.Equal(expectedCount, result.Count);

            if (expectedCount > 0)
            {
                Assert.Equal(expectedFirstId, result.FirstOrDefault().id);
                Assert.Equal(expectedLastId, result.LastOrDefault().id);
            }
        }

        // S-S-W-B-B-W-S-W-B-W-S-B-L
        // 0-1-2-3-4-5-6-7-8-9-10-11-12
        private void SetupMockedPostCoupleConsist(out Car locomotive, out List<Car> consist)
        {
            SetupAreaMocks();
            var locoMock = GenerateMockCar(12, isLoco: true);
            consist =
            [
                GenerateMockCar(0, SylvaArea),
                GenerateMockCar(1, SylvaArea),
                GenerateMockCar(2, WhittierArea),
                GenerateMockCar(3, BrysonArea),
                GenerateMockCar(4, BrysonArea),
                GenerateMockCar(5, WhittierArea),
                GenerateMockCar(6, SylvaArea),
                GenerateMockCar(7, WhittierArea),
                GenerateMockCar(8, BrysonArea),
                GenerateMockCar(9, WhittierArea),
                GenerateMockCar(10, SylvaArea),
                GenerateMockCar(11, BrysonArea),
                locoMock,
            ];
            List<Car> reversed = [.. consist];
            reversed.Reverse();

            _carServiceMock.Setup(cs => cs.EnumerateCoupled(locoMock, Car.LogicalEnd.A)).Returns(consist);
            _carServiceMock.Setup(cs => cs.EnumerateCoupled(locoMock, Car.LogicalEnd.B)).Returns(reversed);

            _carServiceMock.Setup(s => s.GetEndsRelativeToLocation(It.IsAny<Car>(), It.IsAny<Location>())).Returns((Car.LogicalEnd.A, Car.LogicalEnd.B));
            SetupTrainEndA();

            locomotive = locoMock;
        }

        // The train end nearest the waypoint is the end reached from the locomotive's logical end A
        private void SetupTrainEndA()
        {
            float closestDistance = 0f;
            Car? closestCar = null;
            Car? furthestCar = null;
            Car.LogicalEnd closestEnd = Car.LogicalEnd.A;
            Car.LogicalEnd furthestEnd = Car.LogicalEnd.B;
            _carServiceMock.Setup(s => s.GetTrainEndLocations(It.IsAny<Car>(), It.IsAny<Location>(), out closestDistance, out closestCar, out furthestCar, out closestEnd, out furthestEnd))
                .Returns((new Location(), new Location()));
        }

        private void SetupMockedConsist(out Car locomotive, out List<Car> consist)
        {
            SetupAreaMocks();
            var locoMock = GenerateMockCar(3, isLoco: true);
            consist = GenerateDestinationConsist(locoMock);
            List<Car> reversed = [.. consist];
            reversed.Reverse();

            _carServiceMock.Setup(cs => cs.EnumerateCoupled(locoMock, Car.LogicalEnd.A)).Returns(consist);
            _carServiceMock.Setup(cs => cs.EnumerateCoupled(locoMock, Car.LogicalEnd.B)).Returns(reversed);

            _carServiceMock.Setup(s => s.GetEndsRelativeToLocation(It.IsAny<Car>(), It.IsAny<Location>())).Returns((Car.LogicalEnd.A, Car.LogicalEnd.B));
            SetupTrainEndA();

            locomotive = locoMock;
        }

        private List<Car> GenerateDestinationConsist(Car loco)
        {
            List<Car> consist =
            [
                GenerateMockCar(0, SylvaArea),
                GenerateMockCar(1, SylvaArea),
                GenerateMockCar(2, WhittierArea),
                loco,
                GenerateMockCar(4, BrysonArea),
                GenerateMockCar(5, BrysonArea),
                GenerateMockCar(6, SylvaArea),
                GenerateMockCar(7, WhittierArea),
                GenerateMockCar(8, WhittierArea),
                GenerateMockCar(9, SylvaArea),
                GenerateMockCar(10, BrysonArea),
            ];
            return consist;
        }

        private List<Car> GenerateConsist()
        {
            List<Car> cars =
            [
                GenerateMockCar(0),
                GenerateMockCar(1),
                GenerateMockCar(2),
                GenerateMockCar(3),
                GenerateMockCar(4),
                GenerateMockCar(5),
                GenerateMockCar(6),
                GenerateMockCar(7),
                GenerateMockCar(8),
                GenerateMockCar(9),
            ];
            return cars;
        }

        private Mock<ManagedWaypoint> GenerateMockWaypoint(Car locomotive)
        {
            var waypoint = new Mock<ManagedWaypoint>();
            waypoint.Setup(wp => wp.Locomotive).Returns((BaseLocomotive)locomotive);
            waypoint.Setup(wp => wp.Location).Returns(new Location());
            return waypoint;
        }

        // The waypoint's Locomotive is a BaseLocomotive, so locomotives must be mocked as one.
        private static Car NewMockCar(bool isLoco)
        {
            return isLoco ? new Mock<BaseLocomotive>().Object : new Mock<Car>().Object;
        }

        private Car GenerateMockCar(int roadNumber, bool isLoco = false)
        {
            Car car = NewMockCar(isLoco);
            car.SetIdent(new CarIdent("PRR", roadNumber.ToString()));
            car.id = roadNumber.ToString();

            var destination = new OpsCarPosition();
            _opsControllerWrapperMock.Setup(w => w.TryGetCarDesination(car, out destination)).Returns(false);

            _carServiceMock.Setup(cs => cs.IsCarLocomotiveType(car)).Returns(isLoco);

            return car;
        }

        private Car GenerateMockCar(int roadNumber, Area area, bool isLoco = false)
        {
            Car car = NewMockCar(isLoco);
            car.SetIdent(new CarIdent("PRR", roadNumber.ToString()));
            car.id = roadNumber.ToString();

            var destination = new OpsCarPosition(displayName: area.identifier, identifier: area.identifier, []);
            _opsControllerWrapperMock.Setup(w => w.TryGetCarDesination(car, out destination)).Returns(true);
            _opsControllerWrapperMock.Setup(w => w.AreaForCarPosition(destination)).Returns(area);

            _carServiceMock.Setup(cs => cs.IsCarLocomotiveType(car)).Returns(isLoco);

            return car;
        }
    }
}
