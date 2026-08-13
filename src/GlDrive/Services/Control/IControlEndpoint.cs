namespace GlDrive.Services.Control;

/// <summary>
/// A group of related routes. Implementations receive their dependencies as constructor
/// arguments and register their routes on the shared table.
///
/// Implementations must NOT take ServerManager, AppConfig, IrcService, MountService or
/// SpreadManager — see ControlApiBoundaryTests. They take reader facades instead, so an
/// endpoint cannot reach a live credential even by accident.
/// </summary>
public interface IControlEndpoint
{
    void Register(RouteTable routes);
}
