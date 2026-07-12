using IssueTracker.Core.Interfaces;
using IssueTracker.Infrastructure.Data;
using IssueTracker.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Configuration;

namespace IssueTracker.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection")));

        // Map DbContext to AppDbContext so GenericRepository can inject it
        services.AddScoped<DbContext>(provider => provider.GetRequiredService<AppDbContext>());

        services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));

        return services;
    }
}
