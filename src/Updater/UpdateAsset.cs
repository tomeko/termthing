namespace TermThing.Updater;

/// <summary>A single downloadable file attached to a GitHub release.</summary>
public sealed record UpdateAsset(string Name, Uri Url, long Size);
