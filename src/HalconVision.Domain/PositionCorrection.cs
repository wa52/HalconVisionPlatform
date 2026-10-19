using System.Text.Json;

namespace HalconVision.Domain;

public sealed record FrameReference(Guid RunId, Guid FrameId, Guid SourceNodeId, string SourcePortId, Guid SpaceId,
    int Width, int Height, int Count, bool Annotated, bool Corrected, string ConfigurationIdentity, int ImageIndex = 1);
public enum ImageSpaceBehavior { Preserve, NewSpace }
public sealed record PoseSet(Guid NodeId, FrameReference? SourceFrame, string ModelIdentity,
    string ConfigurationIdentity, IReadOnlyList<QuickMatchImageResult> Images);
public sealed record PositionReference(QuickMatchPose Pose, int Width, int Height, Guid MatchNodeId,
    string ModelIdentity, string ImageConfigurationIdentity, DateTime TaughtAtUtc, string PoseImageConfigurationIdentity = "");
public sealed record CoordinateTransform2D(double[] CurrentToReference, double[] ReferenceToCurrent,
    Guid SourceSpaceId, Guid TargetSpaceId, string Convention = "pixel-center-row-column-v1");

public static class PositionReferenceParameters
{
    public const string Key = "reference";
    public static NodeInstance Remap(NodeInstance node, IReadOnlyDictionary<Guid, Guid> ids)
    {
        if (node.TypeId != "halcon.position-correction" || !node.Parameters.TryGetValue(Key, out var value) || value.ValueKind == JsonValueKind.Null) return node;
        var reference = value.Deserialize<PositionReference>() ?? throw new InvalidDataException("位置矫正基准无效。");
        // A copied correction alone must be taught again; never keep a reference to another flow.
        var replacement = ids.TryGetValue(reference.MatchNodeId, out var id)
            ? JsonSerializer.SerializeToElement(reference with { MatchNodeId = id }) : JsonSerializer.SerializeToElement<PositionReference?>(null);
        return node with { Parameters = new(node.Parameters) { [Key] = replacement } };
    }
}
