using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GlDrive.Services.Control;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Invariants 5 and 6 of the control API spec. An adversarial pass over the subsystems found
/// 37 secret-bearing members reachable from the handles endpoints would otherwise hold —
/// including a one-hop path from ServerManager to the plaintext glftpd password. These tests
/// make the boundary structural instead of a rule people remember.
///
/// Forbidden types come in two tiers. Tier 1 (<see cref="ForbiddenTypeNames"/>) may never be
/// held by any endpoint, full stop. Tier 2 (<see cref="InterimTypeNames"/>) covers live
/// subsystem handles that predate the reader facades landing in Plan 2 — permitted only on
/// the finite, named holders in <see cref="KnownInterimHolders"/>, asserted not to grow.
/// </summary>
public class ControlApiBoundaryTests
{
    /// <summary>Tier 1: types an endpoint must never hold under any circumstances.</summary>
    private static readonly string[] ForbiddenTypeNames =
    [
        "ServerManager", "MountService", "IrcService",
        "FtpConnectionPool", "FishKeyStore", "FtpClientFactory"
    ];

    /// <summary>
    /// Tier 2: live subsystem handles permitted only on the endpoints named in
    /// <see cref="KnownInterimHolders"/> until the reader facades (IConfigReader /
    /// ISpreadReader) land in Plan 2. SpreadManager is genuinely one hop from secrets
    /// (SpreadJob.ServerConfigResolver → ServerConfig.Irc.Channels[].Key gives FiSH channel
    /// keys) — it must not stay exempt forever, which is why the holder list is asserted.
    /// </summary>
    private static readonly string[] InterimTypeNames = ["AppConfig", "SpreadManager"];

    /// <summary>
    /// The only endpoints permitted to hold a Tier-2 interim type. Both predate the reader
    /// facades (IConfigReader / ISpreadReader) that remove the need, which land in Plan 2.
    /// Named and asserted here so the exemption is visible and finite — a new endpoint cannot
    /// quietly join it, and this list shrinking to empty is the signal Plan 2 is done.
    /// </summary>
    private static readonly string[] KnownInterimHolders = ["StatusEndpoints", "SpreadEndpoints"];

    private static IEnumerable<Type> EndpointTypes =>
        typeof(IControlEndpoint).Assembly.GetTypes()
            .Where(t => typeof(IControlEndpoint).IsAssignableFrom(t)
                        && t is { IsInterface: false, IsAbstract: false });

    [Fact]
    public void There_is_at_least_one_endpoint_so_this_test_cannot_pass_vacuously()
        => Assert.NotEmpty(EndpointTypes);

    [Fact]
    public void No_endpoint_holds_a_manager_or_pool_or_keystore()
    {
        var violations = new List<string>();

        foreach (var type in EndpointTypes)
        {
            foreach (var (name, fieldType) in DeclaredFields(type))
                if (Holds(fieldType, ForbiddenTypeNames))
                    violations.Add($"{type.Name}.{name} : {fieldType.Name}");

            foreach (var ctor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                foreach (var p in ctor.GetParameters())
                    if (Holds(p.ParameterType, ForbiddenTypeNames))
                        violations.Add($"{type.Name}.ctor({p.Name} : {p.ParameterType.Name})");
        }

        Assert.True(violations.Count == 0,
            "Endpoints must take reader facades, not live subsystems:\n  " +
            string.Join("\n  ", violations.Distinct()));
    }

    [Fact]
    public void The_interim_exemption_list_does_not_grow()
    {
        // Same member surface as No_endpoint_holds_a_manager_or_pool_or_keystore: a Tier-2
        // type acquired through a field, a non-public constructor, or an object initializer
        // must join this ratchet exactly as a public constructor parameter would.
        var holders = EndpointTypes
            .Where(t => DeclaredFields(t).Any(m => Holds(m.Type, InterimTypeNames))
                        || t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            .Any(c => c.GetParameters().Any(p => Holds(p.ParameterType, InterimTypeNames))))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(KnownInterimHolders.OrderBy(n => n).ToArray(), holders);
    }

    /// <summary>
    /// Fields declared anywhere in the type's hierarchy up to (excluding) System.Object.
    /// Type.GetFields(NonPublic) alone only returns members declared on the type itself, so a
    /// private field on a future abstract base class would otherwise be invisible here.
    /// </summary>
    private static IEnumerable<(string Name, Type Type)> DeclaredFields(Type type)
    {
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public
                                           | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                yield return (f.Name, f.FieldType);
    }

    /// <summary>
    /// Unwraps Func&lt;T&gt;, Lazy&lt;T&gt;, nullables and collections — passing a
    /// Func&lt;ServerManager&gt; is exactly as reachable as passing the manager.
    /// </summary>
    private static bool Holds(Type type, string[] typeNames)
    {
        foreach (var t in Flatten(type))
            if (typeNames.Contains(t.Name, StringComparer.Ordinal))
                return true;
        return false;
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;

        if (type.IsArray && type.GetElementType() is { } element)
            foreach (var t in Flatten(element)) yield return t;

        if (type.IsGenericType)
            foreach (var arg in type.GetGenericArguments())
                foreach (var t in Flatten(arg)) yield return t;
    }
}
