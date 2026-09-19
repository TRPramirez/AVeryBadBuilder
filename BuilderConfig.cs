namespace BadBuilder.Configuration;

internal sealed class BuilderConfig
{
    internal BuilderConfig(IEnumerable<HomebrewEntry> builtInHomebrew) => Homebrew.AddRange(builtInHomebrew);

    internal string? MountPoint { get; set; }
    internal DiskInfo? TargetDisk { get; set; }

    internal ExploitOption SelectedExploit { get; set; } = ExploitOption.BadUpdate;
    internal BootstrapOption SelectedBootstrap { get; set; } = BootstrapOption.XeUnshackle;

    internal List<HomebrewEntry> Homebrew { get; } = [];
    internal HomebrewEntry? LaunchHomebrew { get; set; }

    internal bool FirmwareUpdateEnabled { get; set; } = false;

    // Optional apps selected by the user (YouTube, Spotify, etc.)
    public List<string> OptionalApps { get; set; } = new();

    public override string ToString()
    {
        return
        $"""
            Target drive: {TargetDisk?.Name ?? "None"}

            Selected exploit: {SelectedExploit}
            Selected bootstrap: {SelectedBootstrap}

            Homebrew: {string.Join(", ", Homebrew.Select(item => item.Artifact.DisplayName))}
            Default launch homebrew: {LaunchHomebrew?.Artifact.DisplayName ?? "N/A"}

            Optional Apps: {(OptionalApps.Count > 0 ? string.Join(", ", OptionalApps) : "None")}

            Firmware Update Only: {FirmwareUpdateEnabled}
        """;
    }
}