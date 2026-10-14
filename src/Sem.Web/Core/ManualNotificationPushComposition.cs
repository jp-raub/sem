using Microsoft.Extensions.DependencyInjection.Extensions;
namespace Sem.Web.Core;

public static class ManualNotificationPushComposition
{
    // Register infrastructure only. Never change durable activation or accepted work.
    // Caller supplies the scoped database factory and the existing bounded WebPush transport.
    public static IServiceCollection AddManualNotificationPushPipeline(this IServiceCollection services)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton(new ManualPushRuntime(true));
        services.TryAddScoped<ManualNotificationPushQueue>();
        services.TryAddScoped<ManualNotificationPushDispatcher>();
        services.TryAddScoped<ManualNotificationPushEngine>();
        services.AddHostedService<ManualNotificationPushWorker>();
        return services;
    }
}
