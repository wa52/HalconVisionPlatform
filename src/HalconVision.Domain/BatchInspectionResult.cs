namespace HalconVision.Domain;

public sealed record BatchInspectionResult(
    string BatchId,
    string RecipeName,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    IReadOnlyList<BatchInspectionItem> Items)
{
    public int Total => Items.Count;
    public int Passed => Items.Count(item => item.Status == InspectionStatus.Ok);
    public int Failed => Items.Count(item => item.Status == InspectionStatus.Ng);
    public int Errors => Items.Count(item => item.Status == InspectionStatus.Error);
}

public sealed record BatchInspectionItem(
    string ImagePath,
    InspectionStatus Status,
    int? ComponentCount,
    double? ElapsedMilliseconds,
    string? RecordPath,
    string? Error);

public enum InspectionStatus
{
    Ok,
    Ng,
    Error
}
