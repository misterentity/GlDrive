namespace GlDrive.Services.Control;

/// <summary>
/// A group of related routes. Implementations receive their dependencies as constructor
/// arguments and register their routes on the shared table.
///
/// ControlApiBoundaryTests enforces this structurally, in two tiers. Tier 1 — ServerManager,
/// MountService, IrcService, FtpConnectionPool, FishKeyStore, FtpClientFactory — is forbidden
/// outright, on every endpoint, no exceptions. Tier 2 — AppConfig and SpreadManager — is
/// permitted only on the endpoints named in ControlApiBoundaryTests.KnownInterimHolders
/// (currently StatusEndpoints and SpreadEndpoints), as a stopgap until the IConfigReader /
/// ISpreadReader facades land in Plan 2; that holder list is asserted not to grow, so a new
/// endpoint cannot quietly join the exemption. A new endpoint should take reader facades, not
/// a live subsystem, so it cannot reach a credential even by accident.
/// </summary>
public interface IControlEndpoint
{
    void Register(RouteTable routes);
}
