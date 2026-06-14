namespace OpenMultiBraveLauncherV3;

internal static class InstanceRegistry
{
    public static void PersistSettings(
        LauncherSettingsFile settings,
        IEnumerable<InstanceEntry> entries,
        string? workspaceAccountId,
        string activeAccountId,
        string activeShopId)
    {
        var currentInstances = entries.Select(e => e.Config).ToList();
        settings.Instances = workspaceAccountId is null
            ? currentInstances
            : settings.Instances
                .Where(i => !string.Equals(i.AccountId, workspaceAccountId, StringComparison.Ordinal))
                .Concat(currentInstances)
                .ToList();

        settings.MaxConcurrentProfiles = Math.Clamp(settings.MaxConcurrentProfiles, 1, 50);
        if (workspaceAccountId is null)
        {
            settings.ActiveAccountId = activeAccountId;
            settings.ActiveShopId = activeShopId;
        }

        LauncherSettings.Save(settings);
    }
}
