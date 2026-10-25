using System.Text.Json;
using HalconDotNet;
using HalconVision.Domain;
using HalconVision.Application;

namespace HalconVision.App;

public sealed record DisplayGeometry(IReadOnlyList<Point2D> Points, string Color);
public sealed record DisplayCaption(string Text, string Color, Point2D? Position = null);

/// <summary>Managed display snapshots only; never retains HALCON objects beyond this synchronous call.</summary>
public sealed class DetectionOverlayScene
{
    public static IReadOnlyDictionary<string, string> ImportantFields { get; } = new Dictionary<string, string>
    {
        ["score"] = "评分", ["angle"] = "角度（°）", ["length"] = "长度（像素）", ["count"] = "数量", ["area"] = "面积（像素）",
        ["className"] = "分类类别", ["confidence"] = "分类置信度", ["diameter"] = "直径（像素）",
        ["centerDistance"] = "圆心距（像素）", ["boundaryDistance"] = "圆周最短距离（像素）",
        ["centerToLine"] = "圆心到直线（像素）", ["boundaryToLine"] = "圆周到直线（像素）"
    };
    public static IReadOnlyDictionary<string, string> AdvancedFields { get; } = new Dictionary<string, string>
    {
        ["line.RowBegin"] = "起点行坐标", ["line.ColumnBegin"] = "起点列坐标",
        ["line.RowEnd"] = "终点行坐标", ["line.ColumnEnd"] = "终点列坐标", ["classId"] = "类别 ID",
        ["row"] = "模板参考点行", ["column"] = "模板参考点列",
        ["circle.Row"] = "圆心行坐标", ["circle.Column"] = "圆心列坐标", ["circle.Radius"] = "半径（像素）",
        ["circle.Circumference"] = "周长（像素）", ["residualRms"] = "径向残差（像素）", ["signedClearance"] = "圆周净距（像素，负值为穿过）"
    };
    public List<DisplayGeometry> Geometry { get; } = [];
    public List<DisplayCaption> Captions { get; } = [];

    // Display only: structured outputs and rule comparisons retain their original precision.
    public static string FormatDisplayValue(string value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : value;

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
                NodeResultSummary.Color(result).ToString()));
            var rows = NodeOutputValue.From(result, node?.Definition);
            foreach (var row in rows.Where(row => (ImportantFields.ContainsKey(row.Name) || AdvancedFields.ContainsKey(row.Name)) &&
                (layer.Fields ?? ImportantFields.Keys.ToList()).Contains(row.Name) &&
                (!result.Outputs.TryGetValue(row.Name, out var scalar) || scalar.ValueKind != JsonValueKind.Null)))
                scene.Captions.Add(new($"{name} · {ImportantFields.GetValueOrDefault(row.Name) ?? AdvancedFields[row.Name]}：{FormatDisplayValue(row.Value)}", "White"));
            if (!layer.ShowGeometry || run is null || run.RuntimeOutputs is null || !run.RuntimeOutputs.TryGetValue(layer.NodeId, out var runtime)) continue;
            if (!SharesImageSpace(flow, run, settings.NodeId, layer.NodeId) || !HasDimensions(result.Outputs, width, height))
            { scene.Captions.Add(new($"{name}：图像坐标来源或尺寸不匹配，未叠加图形", "Orange")); continue; }
            if (runtime.Values.OfType<ImageRoiOverlay>().FirstOrDefault() is { Bounds: var bounds })
                scene.Geometry.Add(new([new(bounds.RowBegin, bounds.ColumnBegin), new(bounds.RowBegin, bounds.ColumnEnd),
                    new(bounds.RowEnd, bounds.ColumnEnd), new(bounds.RowEnd, bounds.ColumnBegin), new(bounds.RowBegin, bounds.ColumnBegin)], "Gold"));
            if (runtime.Values.OfType<MultiRoiOverlay>().FirstOrDefault() is { } multi)
                foreach (var item in multi.Rois)
                {
                    var box = item.Bounds;
                    var color = item.Decision is { } decision ? NodeResultSummary.ClassificationColor(decision).ToString() : item.Warning ? "Orange" : "Gold";
                    scene.Geometry.Add(new([new(box.RowBegin, box.ColumnBegin), new(box.RowBegin, box.ColumnEnd),
                        new(box.RowEnd, box.ColumnEnd), new(box.RowEnd, box.ColumnBegin), new(box.RowBegin, box.ColumnBegin)], color));
                    scene.Captions.Add(new($"{name} / {item.Name}：{item.Summary}", color, new(box.RowEnd, box.ColumnBegin)));
                }
            if (runtime.Values.OfType<LineCircleOverlay>().FirstOrDefault() is { } lineCircleOverlay)
            {
                if (lineCircleOverlay.Measurement is { } measured)
                {
                    scene.Geometry.Add(new(measured.Circle.Outline(), "LimeGreen"));
                    scene.Geometry.Add(new([new(measured.Line.RowBegin, measured.Line.ColumnBegin), new(measured.Line.RowEnd, measured.Line.ColumnEnd)], "LimeGreen"));
                    if (!measured.ProjectionWithinSegment)
                    {
                        var start = Math.Pow(measured.Line.RowBegin - measured.Projection.Row, 2) + Math.Pow(measured.Line.ColumnBegin - measured.Projection.Column, 2) <
                            Math.Pow(measured.Line.RowEnd - measured.Projection.Row, 2) + Math.Pow(measured.Line.ColumnEnd - measured.Projection.Column, 2)
                            ? new Point2D(measured.Line.RowBegin, measured.Line.ColumnBegin) : new Point2D(measured.Line.RowEnd, measured.Line.ColumnEnd);
                        scene.Geometry.Add(new([start, measured.Projection], "SlateGray"));
                    }
                    if (lineCircleOverlay.ShowNormal && measured.CenterNormal.Length > 1e-12)
                        scene.Geometry.Add(new([new(measured.CenterNormal.RowBegin, measured.CenterNormal.ColumnBegin), measured.Projection], "DeepSkyBlue"));
                    if (lineCircleOverlay.ShowBoundaryLine && measured.BoundaryLine is { Length: > 1e-12 } segment)
                        scene.Geometry.Add(new([new(segment.RowBegin,segment.ColumnBegin),new(segment.RowEnd,segment.ColumnEnd)], "Gold"));
                }
                continue;
            }
            if (runtime.Values.OfType<CirclePairOverlay>().FirstOrDefault() is { } pairOverlay)
            {
                if (pairOverlay.Measurement is { } pair)
                {
                    scene.Geometry.Add(new(pair.CircleA.Outline(), "LimeGreen"));
                    scene.Geometry.Add(new(pair.CircleB.Outline(), "LimeGreen"));
                    if (pairOverlay.ShowCenterLine && pair.CenterLine.Length > 1e-12)
                        scene.Geometry.Add(new([new(pair.CenterLine.RowBegin,pair.CenterLine.ColumnBegin),new(pair.CenterLine.RowEnd,pair.CenterLine.ColumnEnd)], "DeepSkyBlue"));
                    if (pairOverlay.ShowBoundaryLine && pair.BoundaryLine is { Length: > 1e-12 } segment)
                        scene.Geometry.Add(new([new(segment.RowBegin,segment.ColumnBegin),new(segment.RowEnd,segment.ColumnEnd)], "Gold"));
                }
                continue;
            }
            if (runtime.Values.OfType<CircleGeometryOverlay>().FirstOrDefault() is { } circleOverlay)
            {
                var b = circleOverlay.SearchRegion;
                if (circleOverlay.ShowDiagnostics)
                {
                    foreach (var measure in circleOverlay.MeasureRegions) scene.Geometry.Add(new(measure, "DodgerBlue"));
                    foreach (var point in circleOverlay.EdgePoints.Points)
                        scene.Geometry.Add(new([new(point.Row - 2, point.Column), new(point.Row + 2, point.Column)], "LimeGreen"));
                }
                if (circleOverlay.ShowSearchRegion)
                    scene.Geometry.Add(new([new(b.RowBegin,b.ColumnBegin),new(b.RowBegin,b.ColumnEnd),new(b.RowEnd,b.ColumnEnd),new(b.RowEnd,b.ColumnBegin),new(b.RowBegin,b.ColumnBegin)],circleOverlay.FittedCircle is null ? "Red" : "Gold"));
                if (circleOverlay.ShowFittedCircle && circleOverlay.FittedCircle is { } circle)
                {
                    scene.Geometry.Add(new(circle.Outline(), "LimeGreen"));
                    scene.Geometry.Add(new([new(circle.Row - 5, circle.Column), new(circle.Row + 5, circle.Column)], "LimeGreen"));
                    scene.Geometry.Add(new([new(circle.Row, circle.Column - 5), new(circle.Row, circle.Column + 5)], "LimeGreen"));
                }
                continue;
            }
            if (runtime.Values.OfType<GeometryOverlay>().FirstOrDefault() is { } overlay)
            {
                if (overlay.SearchRegion is { } roi)
                    scene.Geometry.Add(new([new(roi.RowBegin, roi.ColumnBegin), new(roi.RowBegin, roi.ColumnEnd),
                        new(roi.RowEnd, roi.ColumnEnd), new(roi.RowEnd, roi.ColumnBegin), new(roi.RowBegin, roi.ColumnBegin)], "Gold"));
                if (overlay.FittedLine is { } line)
                    scene.Geometry.Add(new([new(line.RowBegin, line.ColumnBegin), new(line.RowEnd, line.ColumnEnd)], "Red"));
                continue;
            }
            if (runtime.Values.OfType<QuickMatchOverlay>().FirstOrDefault() is { } matching)
            {
                var frame = matching.Images.FirstOrDefault(item => item.ImageIndex == settings.ImageIndex);
                if (frame?.SearchRegion is { } search)
                    scene.Geometry.Add(new([new(search.RowBegin, search.ColumnBegin), new(search.RowBegin, search.ColumnEnd),
                        new(search.RowEnd, search.ColumnEnd), new(search.RowEnd, search.ColumnBegin), new(search.RowBegin, search.ColumnBegin)], "Gold"));
                foreach (var match in frame?.Matches ?? [])
                {
                    foreach (var contour in match.Contours) scene.Geometry.Add(new(contour, "LimeGreen"));
                    var pose = match.Pose;
                    scene.Geometry.Add(new([new(pose.Row - 5, pose.Column), new(pose.Row + 5, pose.Column)], "LimeGreen"));
                    scene.Geometry.Add(new([new(pose.Row, pose.Column - 5), new(pose.Row, pose.Column + 5)], "LimeGreen"));
                }
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
        if (run?.FlowId == flow.Id && run.Inspection?.ConfigurationIdentity == InspectionPlan.ConfigurationIdentity(flow.Document))
        foreach (var region in flow.Document.InspectionRegions.Where(r => r.ImageNodeId == settings.NodeId))
        {
            var tasks = flow.Document.InspectionTasks.Where(t => t.Enabled && t.RegionId == region.Id &&
                (settings.Overlays ?? []).Any(l => l.ShowGeometry && t.NodeIds.Contains(l.NodeId))).ToArray();
            if (tasks.Length == 0) continue;
            var results = run.Inspection.Tasks.Where(t => tasks.Any(item => item.Id == t.TaskId)).ToArray();
            var color = results.Length != tasks.Length || results.Any(t => t.Verdict is not (InspectionVerdict.Ok or InspectionVerdict.Ng)) ? "Orange" : results.Any(t => t.Verdict == InspectionVerdict.Ng) ? "Red" : "LimeGreen";
            var b = region.Bounds;
            scene.Geometry.Add(new([new(b.RowBegin,b.ColumnBegin),new(b.RowBegin,b.ColumnEnd),new(b.RowEnd,b.ColumnEnd),new(b.RowEnd,b.ColumnBegin),new(b.RowBegin,b.ColumnBegin)],color));
            scene.Captions.Add(new($"{region.Name}："+string.Join(" / ",tasks.Select(t=>
            {
                var verdict=results.FirstOrDefault(r=>r.TaskId==t.Id)?.Verdict;
                return $"{t.Name} {(verdict == InspectionVerdict.Ok ? "OK" : verdict == InspectionVerdict.Ng ? "NG" : "ERROR")}";
            })),color));
        }
        return scene;
    }

    private static bool HasDimensions(IReadOnlyDictionary<string, JsonElement> outputs, int width, int height) =>
        outputs.Values.Any(value => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("width", out var w) &&
            value.TryGetProperty("height", out var h) && w.TryGetInt32(out var wi) && h.TryGetInt32(out var hi) && wi == width && hi == height);

    private static bool SharesImageSpace(FlowTabViewModel flow, WorkflowExecutionRun run, Guid first, Guid second)
    {
        var firstFrame = run.SpatialOutputs?.GetValueOrDefault(first)?.Values.FirstOrDefault();
        var secondFrame = run.SpatialOutputs?.GetValueOrDefault(second)?.Values.FirstOrDefault();
        if (firstFrame is not null && secondFrame is not null)
            return firstFrame.RunId == secondFrame.RunId && firstFrame.FrameId == secondFrame.FrameId && firstFrame.SpaceId == secondFrame.SpaceId;
        HashSet<Guid> Roots(Guid id, HashSet<Guid> seen)
        {
            if (!seen.Add(id)) return [];
            var node = flow.Nodes.FirstOrDefault(item => item.Id == id);
            if (node is null) return [];
            if (node.TypeId == "halcon.position-correction") return [id];
            var inputIds = node.InputPorts.Where(port => port.DataType is "halcon.image" or "halcon.region").Select(port => port.Id).ToHashSet();
            var edges = flow.Document.Edges.Where(edge => edge.ToNodeId == id && inputIds.Contains(edge.ToPortId)).ToArray();
            return edges.Length == 0 ? [id] : edges.SelectMany(edge => Roots(edge.FromNodeId, seen)).ToHashSet();
        }
        var a = Roots(first, []); var b = Roots(second, []);
        return a.Count == 1 && a.SetEquals(b);
    }
}
