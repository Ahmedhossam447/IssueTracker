using MediatR;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace IssueTracker.Application.Behaviors
{
    public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
    {
        private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;

        public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
        {
            _logger = logger;
        }
        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            var requestName = typeof(TRequest).Name;
            _logger.LogInformation("[START] {RequestName} - Payload: {@Request}", requestName, request);
            
            var timer = new Stopwatch();
            timer.Start();

            var response = await next();

            timer.Stop();
            _logger.LogInformation("[END] {RequestName} - Duration: {Duration} ms", requestName, timer.ElapsedMilliseconds);
            return response;
        }
    }
}
