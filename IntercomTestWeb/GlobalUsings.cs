// The ASP.NET Core Web SDK's implicit usings bring in Microsoft.Extensions.Logging.ILogger, which
// collides with Serilog.ILogger. The whole app logs through Serilog (as the rest of the solution
// does), so alias ILogger to Serilog's everywhere.
global using ILogger = Serilog.ILogger;
