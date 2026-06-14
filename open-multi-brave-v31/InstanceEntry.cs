namespace OpenMultiBraveLauncherV3;

internal sealed class InstanceEntry(InstanceConfig config, BraveInstanceSession session, int cdpPort)
{
    public InstanceConfig Config { get; } = config;

    public BraveInstanceSession Session { get; } = session;

    public int CdpPort { get; } = cdpPort;
}
