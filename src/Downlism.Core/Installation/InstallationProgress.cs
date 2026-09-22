namespace Downlism.Core.Installation;

/// <summary>Completed work items in the current installation phase.</summary>
public sealed record InstallationProgress(string Phase, int Completed, int Total)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Completed / Total, 0, 1) : 0;
}
