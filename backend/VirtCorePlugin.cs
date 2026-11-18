using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NKeeper.Abstractions;

namespace NKeeper.Plugins.Virtualization;

/// <summary>
/// The virt-core plugin (docs/architecture.md §7.36): what the hypervisor plugins share. It contributes nothing of its
/// own: each hypervisor contributes its session type, and uses this assembly, which virt-core exports, for the session;
/// its frontend exports the tab.
/// </summary>
public sealed class VirtCorePlugin : IPlugin
{
    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
    {
    }
}

/// <summary>How a hypervisor plugin registers itself, from its <see cref="IPlugin.ConfigureServices"/>.</summary>
public static class VirtualizationServiceCollectionExtensions
{
    /// <summary>The provider, and the handler of its guests session type, <see cref="GuestSessionHandler{TProvider}"/>.</summary>
    public static IServiceCollection AddGuestProvider<TProvider>(this IServiceCollection services)
        where TProvider : class, IGuestProvider
    {
        services.TryAddScoped<TProvider>();
        services.AddScoped<GuestSessionHandler<TProvider>>();
        return services;
    }
}
