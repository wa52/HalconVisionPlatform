using System.Globalization;
using System.Text.Json;
using HalconVision.Domain;

namespace HalconVision.Halcon;

internal sealed record NamedDlRoi(Guid Id, string Name, DlCropRoi Crop);
internal static class DlRoiSet
{
    public static IReadOnlyList<NamedDlRoi> Read(NodeInstance node, int width, int height)
    {
        if (!node.Parameters.TryGetValue("rois", out var value))
        {
            var crop = DlCropRoi.Read(node, width, height);
            return [new(Guid.Empty, crop.IsExplicit ? "ROI 1" : "全图", crop)];
        }
        var definitions = DetectionRoi.Parse(value);
        if (definitions.Count == 0)
        {
            var full = node with { Parameters = new(node.Parameters) { ["roi"] = JsonSerializer.SerializeToElement("") } };
            return [new(Guid.Empty, "全图", DlCropRoi.Read(full, width, height))];
        }
        var selected = new List<NamedDlRoi>();
        foreach (var item in definitions.Where(item => item.Enabled))
        {
            var text = string.Join(",", new[] { item.RowBegin, item.ColumnBegin, item.RowEnd, item.ColumnEnd }
                .Select(number => number.ToString("R", CultureInfo.InvariantCulture)));
            var single = node with { Parameters = new(node.Parameters) { ["roi"] = JsonSerializer.SerializeToElement(text) } };
            try { selected.Add(new(item.Id, item.Name.Trim(), DlCropRoi.Read(single, width, height))); }
            catch (InvalidDataException ex) { throw new InvalidDataException($"ROI“{item.Name}”：{ex.Message}", ex); }
        }
        if (selected.Count == 0) throw new InvalidDataException("所有 ROI 均已禁用，请至少启用一个，或清空列表以使用全图。");
        return selected;
    }
}
