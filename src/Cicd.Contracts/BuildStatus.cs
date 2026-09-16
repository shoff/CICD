namespace Cicd.Contracts;

public enum BuildStatus
{
    Queued = 0,
    Running = 1,
    Success = 2,
    Failure = 3,
    Error = 4,
    Canceled = 5,
}

public static class BuildStatusExtensions
{
    public static bool IsFinished(this BuildStatus status) =>
        status is BuildStatus.Success or BuildStatus.Failure or BuildStatus.Error or BuildStatus.Canceled;
}

public enum StepStatus
{
    Pending = 0,
    Running = 1,
    Success = 2,
    Failure = 3,
    Skipped = 4,
    Canceled = 5,
}

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}
