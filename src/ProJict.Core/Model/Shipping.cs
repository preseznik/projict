using System.Text.Json.Serialization;

namespace ProJict.Core.Model;

/// <summary>What shipping a project can involve. Push and Release publish; the others stay on this computer.</summary>
public enum ShipStep { Commit, Build, Package, Push, Release }

/// <summary>
/// Requested: waiting for an agent's plan. Planned: the plan waits for your approval. Approved: the agent may run it
/// (also straight away for local steps a project lets run without asking). Running: steps are being reported. The
/// rest are closed.
/// </summary>
public enum ShipState { Requested, Planned, Approved, Running, Done, Failed, Declined, Withdrawn }

/// <summary>How a project is shipped (its Details tab): the steps it has and what an agent does for them.</summary>
public sealed class ShipSettings
{
    public List<ShipStep> Steps { get; set; } = [.. Enum.GetValues<ShipStep>()];
    /// <summary>The instructions an agent follows, e.g. "bump the version in X, run build.ps1, …".</summary>
    public string? HowTo { get; set; }
    /// <summary>Commit, build and package may run without your approval; push and release always ask.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LocalWithoutApproval { get; set; }
}

/// <summary>
/// A request to ship a project: you pick the steps, an AI agent working in the project posts a plan, you approve or
/// decline it, and only then does the agent run the steps and report each one. See ShipFlow.
/// </summary>
public sealed class ShipRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public List<ShipStep> Steps { get; set; } = [];
    public ShipState State { get; set; }
    public string RequestedBy { get; set; } = "";
    public DateTime RequestedAt { get; set; } = DateTime.Now;
    /// <summary>The agent's plan: version, what's in it, items still waiting for review, what it will run.</summary>
    public string? Plan { get; set; }
    public string? Version { get; set; }
    public string? PlannedBy { get; set; }
    public string? DecidedBy { get; set; }
    /// <summary>What happened, signed: asked, planned, approved, each step, the result.</summary>
    public List<ActivityEntry> Log { get; set; } = [];
    public DateTime? FinishedAt { get; set; }
    /// <summary>A finished request's card was closed on the board.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Dismissed { get; set; }

    [JsonIgnore] public bool IsOpen => State is ShipState.Requested or ShipState.Planned or ShipState.Approved or ShipState.Running;
}
