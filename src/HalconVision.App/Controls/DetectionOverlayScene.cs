using System.Text.Json;
using HalconDotNet;
using HalconVision.Domain;

namespace HalconVision.App;

public sealed record DisplayGeometry(IReadOnlyList<Point2D> Points, string Color);
public sealed record DisplayCaption(string Text, string Color);

/// <summary>Managed display snapshots only; never retains HALCON objects beyond this synchronous call.</summary>
public sealed class DetectionOverlayScene
{
    public List<DisplayGeometry> Geometry { get; } = [];
    public List<DisplayCaption> Captions { get; } = [];

    public static DetectionOverlayScene Create(FlowTabViewModel flow, WorkflowExecutionRun? run,
        DetectionContentSettings settings, int width, int height)
    {
        var scene = new DetectionOverlayScene();
        foreach (var layer in settings.Overlays ?? [])
        {
            var node = flow.Nodes.FirstOrDefault(item => item.Id == layer.NodeId);
            var result = run?.FlowId == flow.Id ? run.NodeResults.FirstOrDefault(item => item.NodeId == layer.NodeId) : null;
            var name = node?.DisplayName ?? "已删除节点";
            if (result is null) { scene.Captions.Add(new($"{name}：本轮未运行", "Orange")); continue; }
            if (layer.ShowDecision) scene.Captions.Add(new($"{name}：{NodeResultSummary.Describe(result)}",
                result.Status == NodeExecutionStatus.Success && !NodeResultSummary.Describe(result).StartsWith("未找到") ? "LimeGreen" : "Orange"));
            var rows = NodeOutputValue.From(result, node?.Definition);
            foreach (var row in rows.Where(row => (layer.Fields ?? ["score", "count", "angle", "length"]).Contains(row.Name)))
                scene.Captions.Add(new($"{name}.{row.Name} = {row.Value}", "White"));
            if (!layer.ShowGeometry || run is null || run.RuntimeOutputs is null || !run.RuntimeOutputs.TryGetValue(layer.NodeId, out var runtime)) continue;
            if (!SharesImageSpace(flow, settings.NodeId, layer.NodeId) || !HasDimensions(result.Outputs, width, height))
            { scene.Captions.Add(new($"{name}：图像坐标来源或尺寸不匹配，未叠加图形", "Orange")); continue; }
            if (runtime.Values.OfType<GeometryOverlay>().FirstOrDefault() is { } overlay)
            {
                if (overlay.SearchRegion is { } roi)
                    scene.Geometry.Add(new([new(roi.RowBegin, roi.ColumnBegin), new(roi.RowBegin, roi.ColumnEnd),
                        new(roi.RowEnd, roi.ColumnEnd), new(roi.RowEnd, roi.ColumnBegin), new(roi.RowBegin, roi.ColumnBegin)], "Gold"));
                if (overlay.FittedLine is { } line)
                    scene.Geometry.Add(new([new(line.RowBegin, line.ColumnBegin), new(line.RowEnd, line.ColumnEnd)], "Red"));
                continue;
            }
            foreach (var iconic in runtime.Values.OfType<HObject>().Distinct(ReferenceEqualityComparer.Instance))
            {
                if (iconic is HRegion region && region.IsInitialized())
                {
                    using var contours = region.GenContourRegionXld("border"); AddContours(contours);
                }
                else if (iconic is HXLDCont contours && contours.IsInitialized()) AddContours(contours);
            }
            void AddContours(HXLDCont contours)
            {
                for (var index = 1; index <= contours.CountObj(); index++)
                {
                    using var contour = contours.SelectObj(index);
                    contour.GetContourXld(out HTuple rows, out HTuple columns);
                    using (rows) using (columns)
                        scene.Geometry.Add(new(Enumerable.Range(0, Math.Min(rows.Length, columns.Length))
                            .Select(i => new Point2D(rows[i].D, columns[i].D)).ToArray(), "LimeGreen"));
                }
            }
        }
        return scene;
    }

    private static bool HasDimensions(IReadOnlyDictionary<string, JsonElement> outputs, int width, int height) =>
        outputs.Values.Any(value => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("width", out var w) &&
            value.TryGetProperty("height", out var h) && w.TryGetInt32(out var wi) && h.TryGetInt32(out var hi) && wi == width && hi == height);

    private static bool SharesImageSpace(FlowTabViewModel flow, Guid first, Guid second)
    {
        HashSet<Guid> Roots(Guid id, HashSet<Guid> seen)
        {
            if (!seen.Add(id)) return [];
            var node = flow.Nodes.FirstOrDefault(item => item.Id == id);
            if (node is null) return [];
            var inputIds = node.InputPorts.Where(port => port.DataType is "halcon.image" or "halcon.region").Select(port => port.Id).ToHashSet();
            var edges = flow.Document.Edges.Where(edge => edge.ToNodeId == id && inputIds.Contains(edge.ToPortId)).ToArray();
            return edges.Length == 0 ? [id] : edges.SelectMany(edge => Roots(edge.FromNodeId, seen)).ToHashSet();
        }
        var a = Roots(first, []); var b = Roots(second, []);
        return a.Count == 1 && a.SetEquals(b);
    }
}
