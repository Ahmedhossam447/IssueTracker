using IssueTracker.Application;
using IssueTracker.Infrastructure;
using IssueTracker.Middlewares;
using Serilog;

namespace IssueTracker
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Host.UseSerilog((context, configuration) =>
            {
                configuration.ReadFrom.Configuration(context.Configuration);
            });
            // Add services to the container.
            builder.Services.AddApplication();
            builder.Services.AddInfrastructure(builder.Configuration);

            // Register Exception Handler
            builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
            builder.Services.AddProblemDetails();

            builder.Services.AddControllers();
            // Configure Swagger/OpenAPI
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddOpenApiDocument(config =>
            {
                config.Title = "IssueTracker API";
            });

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseOpenApi();
                app.UseSwaggerUi();
            }

            app.UseHttpsRedirection();

            // Use the global exception handler
            app.UseExceptionHandler();

            app.UseAuthorization();


            app.MapControllers();

            app.Run();
        }
    }
}
