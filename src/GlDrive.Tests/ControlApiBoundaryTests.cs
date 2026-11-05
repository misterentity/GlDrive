using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GlDrive.Services;
using GlDrive.Services.Control.Endpoints;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Invariants 5 and 6 of the control API spec. An adversarial pass over the subsystems found
/// 37 secret-bearing members reachable from live subsystem handles — including a one-hop path
/// from ServerManager to the plaintext glftpd password. The HTTP layer (ControlApi and
/// everything under GlDrive.Services.Control) must therefore reach app state only through the
/// AutomationRegistry, which owns scope, approval and audit. These tests make that boundary
/// structural instead of a rule people remember.
///
/// AppConfig is the one exception, and only on ControlApi itself: it reads the listener port
/// and owner token. <see cref="KnownConfigHolders"/> is asserted not to grow.
/// </summary>
public class ControlApiBoundaryTests
{
    /// <summary>Types the HTTP layer must never hold under any circumstances.</summary>
    private static readonly string[] ForbiddenTypeNames =
    [
        "ServerManager", "MountService", "IrcService", "SpreadManager",
        "FtpConnectionPool", "FishKeyStore", "FtpClientFactory"
    ];

    private static readonly string[] ConfigTypeNames = ["AppConfig"];

    private static readonly string[] KnownConfigHolders = ["ControlApi"];

    private static IEnumerable<Type> HttpLayerTypes =>
        typeof(ControlApi).Assembly.GetTypes()
            .Where(t => !t.IsNested && (t == typeof(ControlApi)
                        || (t.Namespace?.StartsWith("GlDrive.Services.Control", StringComparison.Ordinal) ?? false)));

    [Fact]
    public void The_scan_covers_the_listener_and_its_endpoints_so_it_cannot_pass_vacuously()
    {
        var names = HttpLayerTypes.Select(t => t.Name).ToArray();
        Assert.Contains(nameof(ControlApi), names);
        Assert.Contains(nameof(AutomationEndpoints), names);
    }

    /// <summary>
    /// Local fixture proving DeclaredFields walks static fields, not just instance ones — a
    /// static-held forbidden type would otherwise be invisible to the scans below. It lives
    /// in the test assembly, so it never joins HttpLayerTypes; this test drives
    /// DeclaredFields/Holds directly instead.
    /// </summary>
    private sealed class StaticFieldFixture
    {
#pragma warning disable CS0414 // never read by design — the scan inspects the declared field type, not its value
        private static readonly ServerManager? _cachedServerManager = null;
#pragma warning restore CS0414
    }

    [Fact]
    public void DeclaredFields_walks_static_fields_too()
    {
        var caught = DeclaredFields(typeof(StaticFieldFixture))
            .Where(f => Holds(f.Type, ForbiddenTypeNames))
            .Select(f => f.Name)
            .ToArray();

        Assert.Contains("_cachedServerManager", caught);
    }

    /// <summary>
    /// The name lists match by Type.Name, a bare string. Renaming ServerManager in production
    /// would silently drop it from the scan with a green suite — this fails loudly instead,
    /// naming the stale entry.
    /// </summary>
    [Fact]
    public void Every_forbidden_type_name_still_resolves_to_a_real_type()
    {
        var assembly = typeof(ControlApi).Assembly;
        var knownNames = assembly.GetTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var name in ForbiddenTypeNames.Concat(ConfigTypeNames))
            Assert.True(knownNames.Contains(name),
                $"'{name}' no longer resolves to a type in {assembly.GetName().Name} — " +
                "it was likely renamed, which silently drops it from the boundary scan.");
    }

    [Fact]
    public void The_http_layer_holds_no_manager_pool_or_keystore()
    {
        var violations = new List<string>();

        foreach (var type in HttpLayerTypes)
        {
            foreach (var (name, fieldType) in DeclaredFields(type))
                if (Holds(fieldType, ForbiddenTypeNames))
                    violations.Add($"{type.Name}.{name} : {fieldType.Name}");

            foreach (var p in ConstructorParameters(type))
                if (Holds(p.ParameterType, ForbiddenTypeNames))
                    violations.Add($"{type.Name}.ctor({p.Name} : {p.ParameterType.Name})");
        }

        Assert.True(violations.Count == 0,
            "The control HTTP layer must reach app state through AutomationRegistry only:\n  " +
            string.Join("\n  ", violations.Distinct()));
    }

    [Fact]
    public void Only_the_listener_holds_the_config()
    {
        var holders = HttpLayerTypes
            .Where(t => DeclaredFields(t).Any(m => Holds(m.Type, ConfigTypeNames))
                        || ConstructorParameters(t).Any(p => Holds(p.ParameterType, ConfigTypeNames)))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(KnownConfigHolders, holders);
    }

    private static IEnumerable<ParameterInfo> ConstructorParameters(Type type)
        => type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(c => c.GetParameters());

    /// <summary>
    /// Fields declared anywhere in the type's hierarchy up to (excluding) System.Object,
    /// static included — a `private static readonly ServerManager _sm` is exactly as
    /// reachable as an instance field.
    /// </summary>
    private static IEnumerable<(string Name, Type Type)> DeclaredFields(Type type)
    {
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
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
