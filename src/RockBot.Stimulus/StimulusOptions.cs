namespace RockBot.Stimulus;

/// <summary>
/// Options for locating stimulus pattern files on disk. One JSON file per pattern, under
/// <see cref="BasePath"/> — resolved under <c>RockBot.Host.AgentProfileOptions.BasePath</c>
/// when relative, the same way <c>FailureClusterOptions.BasePath</c> and every other small
/// store's <c>BasePath</c> already resolves.
/// </summary>
public sealed class StimulusOptions
{
    /// <summary>Base directory for stimulus pattern files. Defaults to <c>"stimuli"</c>.</summary>
    public string BasePath { get; set; } = "stimuli";

    /// <summary>
    /// How often the loader polls the stimuli directory as a fallback to
    /// <see cref="System.IO.FileSystemWatcher"/>, for the same reason
    /// <c>AgentProfileOptions.PollIntervalSeconds</c> exists — the watcher is unreliable on
    /// Docker Desktop bind mounts and network/overlay volumes such as Longhorn PVCs.
    /// Set to zero to disable polling. Defaults to 5s, matching <c>AgentProfileOptions</c>.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>Master enable switch. When false, <c>AddStimulusDispatcher</c> registers nothing.</summary>
    public bool Enabled { get; set; } = true;
}
