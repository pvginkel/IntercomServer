using Serilog;

namespace IntercomTest;

internal static class TaskUtils
{
    private static readonly ILogger Logger = Log.ForContext(typeof(TaskUtils));

    public static void Run(Func<Task> func)
    {
        RunWithExceptionHandler(func);
    }

    private static async void RunWithExceptionHandler(Func<Task> func)
    {
        try
        {
            await func();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error while executing background task");
        }
    }
}
