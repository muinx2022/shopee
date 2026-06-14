namespace OpenMultiBraveLauncherV3;

internal static class ExtensionProgressCoordinator
{
    public static async Task<ExtensionRunnerState?> ReadProgressAsync(
        bool running,
        int cdpPort,
        DirectoryInfo profileRoot,
        bool silent,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        ExtensionRunnerState? state = null;
        if (running)
        {
            try
            {
                state = await ExtensionRunnerAutomation.TryReadStateViaCdpAsync(
                    cdpPort, profileRoot, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!silent)
            {
                log($"CDP chua doc duoc ({ex.Message}) - thu doc file profile...");
            }
        }

        if (state is not null)
            return state;

        if (ExtensionProgressReader.TryRead(profileRoot, out var fileState))
            return fileState;

        if (!silent)
            log("Chua doc duoc tien do extension. Reload extension trong brave://extensions roi thu lai.");

        return null;
    }

    public static Task<bool> PushFormConfigAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        string sheet,
        int? startRow,
        int? endRow,
        CancellationToken cancellationToken)
    {
        return ExtensionRunnerAutomation.TryApplyFormConfigAsync(
            cdpPort,
            profileRoot,
            sheet,
            startRow,
            endRow,
            cancellationToken);
    }
}
