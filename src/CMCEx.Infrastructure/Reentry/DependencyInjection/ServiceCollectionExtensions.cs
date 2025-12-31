using CMCEx.Core.Reentry;
using CMCEx.Infrastructure.Reentry;
using Microsoft.Extensions.DependencyInjection;

namespace CMCEx.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddReentryUdp(this IServiceCollection services, Action<ReentryOptions>? configure = null)
    {
        var opts = new ReentryOptions();
        configure?.Invoke(opts);

        services.AddSingleton(opts);
        services.AddSingleton<IReentryCommandSender, UdpReentryCommandSender>();
        return services;
    }
}
