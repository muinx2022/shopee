namespace OpenMultiBraveLauncherV3;

internal static class AutoRunController
{
    public static (int from, int to) GetRange(LauncherSettingsFile settings, int total)
    {
        if (total == 0)
            return (1, 0);

        var from = Math.Max(1, settings.AutoRunFromInstance);
        if (from > total)
            from = total;

        var to = settings.AutoRunToInstance <= 0
            ? total
            : Math.Clamp(settings.AutoRunToInstance, from, total);

        return (from, to);
    }

    public static bool ContainsIndex(int zeroBasedIndex, (int from, int to) range)
    {
        if (zeroBasedIndex < 0)
            return false;

        var oneBased = zeroBasedIndex + 1;
        return oneBased >= range.from && oneBased <= range.to;
    }
}
