namespace Argon.Features.RegionalUnit;

public record ArgonUnitOptions(
    string datacenter,
    string role,
    ECEFCoordinate globalPos,
    IPAddress entryAddress);