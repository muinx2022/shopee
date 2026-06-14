namespace OpenMultiBraveLauncherV3;

internal sealed class ApiNotRunningException : Exception
{
    public ApiNotRunningException(string message) : base(message) { }

    public ApiNotRunningException(string message, Exception inner) : base(message, inner) { }
}
