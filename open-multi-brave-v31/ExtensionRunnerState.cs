namespace OpenMultiBraveLauncherV3;

public sealed class ExtensionRunnerState
{
    public string? SheetName { get; init; }
    public int? StartRow { get; init; }
    public int? EndRow { get; init; }
    public int? LastCompletedRow { get; init; }
    public int? CurrentRow { get; init; }
    public string? LastSku { get; init; }
    public string? Phase { get; init; }
    public bool? Running { get; init; }
    public string? LastMessage { get; init; }

    public bool IsInterruptedMidRun()
    {
        if (Running == true)
            return false;

        if (string.Equals(Phase, "finished", StringComparison.OrdinalIgnoreCase))
            return false;

        if (EndRow is > 0 && LastCompletedRow is > 0 && LastCompletedRow >= EndRow)
            return false;

        if (LastCompletedRow is null or < 1 && CurrentRow is null or < 1)
            return false;

        var phase = Phase ?? "";
        if (phase is "paused" or "error" or "waiting" or "opening" or "starting")
            return true;

        if (phase is "stopped" && EndRow is > 0 && LastCompletedRow < EndRow)
            return true;

        return CurrentRow is > 0 &&
               LastCompletedRow is > 0 &&
               CurrentRow > LastCompletedRow;
    }

    public int? StoppedAtRow =>
        CurrentRow is > 0 && LastCompletedRow is > 0 && CurrentRow > LastCompletedRow
            ? CurrentRow
            : LastCompletedRow is > 0
                ? LastCompletedRow.Value + 1
                : CurrentRow;
}
