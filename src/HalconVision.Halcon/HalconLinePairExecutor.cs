using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.Halcon;

public sealed class HalconLinePairExecutor : INodeExecutor
{
    public string TypeId => HalconLinePairNodeProvider.LinePair;
    public int Version => 1;
    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        var token = context.CancellationToken; token.ThrowIfCancellationRequested();
        if (context.RuntimeInputs?.GetValueOrDefault("image") is not HImage image || !image.IsInitialized() || image.CountObj() != 1)
            throw new InvalidDataException("线线测量需要本轮的一张有效原始图像。");
        image.GetImageSize(out int width, out int height);
        var frame = context.InputFrames?.GetValueOrDefault("image");
        if (context.RunId == Guid.Empty || frame is null || frame.RunId != context.RunId || frame.FrameId == Guid.Empty || frame.SpaceId == Guid.Empty ||
            frame.Annotated || frame.Count != 1 || frame.ImageIndex != 1 || frame.Width != width || frame.Height != height)
            throw new InvalidDataException("MEASUREMENT_FRAME_REQUIRED：需要本轮未标注单帧及坐标来源。");
        var a = Read<Line2D>("lineA"); var b = Read<Line2D>("lineB");
        CheckLine(a,"lineA"); CheckLine(b,"lineB");
        var showLines=Flag("showLines"); var showConnector=Flag("showConnector");
        LinePairMeasurement? measurement=null;
        var status=new LinePairStatus(LinePairStatus.Missing,"缺少有效直线，未生成测量值；请检查两个上游查找结果。");
        if(a is not null && b is not null)
        {
            var points=new[]{new Point2D(a.RowBegin,a.ColumnBegin),new(a.RowEnd,a.ColumnEnd),new(b.RowBegin,b.ColumnBegin),new(b.RowEnd,b.ColumnEnd)};
            var box=new Rectangle2D(points.Min(p=>p.Row),points.Min(p=>p.Column),points.Max(p=>p.Row),points.Max(p=>p.Column));
            if(context.Node.Parameters.TryGetValue("taskRoi",out var scoped))
            {
                var scopedNode=context.Node with{Parameters=new(context.Node.Parameters){["roi"]=scoped}};
                var bounds=TaskRoiScope.Read(scopedNode,width,height) ?? throw new InvalidDataException("测量任务区域为空。");
                if(box.RowBegin<bounds.RowBegin || box.ColumnBegin<bounds.ColumnBegin || box.RowEnd>bounds.RowEnd || box.ColumnEnd>bounds.ColumnEnd)
                    throw new InvalidDataException("MEASUREMENT_OUTSIDE_TASK：两条完整线段必须位于测量任务区域内。");
            }
            ValidRegionGuard.Require(context,image,[box]);
            using(var domain=image.GetDomain())
            using(var requested=new HRegion(Math.Floor(box.RowBegin),Math.Floor(box.ColumnBegin),Math.Ceiling(box.RowEnd),Math.Ceiling(box.ColumnEnd)))
            using(var missing=requested.Difference(domain))
            using(var area=missing.AreaCenter(out HTuple rows,out HTuple cols))
            using(rows) using(cols) using(var real=area.TupleReal())
                if(real.DArr.Sum()>0)throw new InvalidDataException("MEASUREMENT_INVALID_PIXELS：测量包围范围包含无效像素。");
            HMisc.DistanceSs(a.RowBegin,a.ColumnBegin,a.RowEnd,a.ColumnEnd,b.RowBegin,b.ColumnBegin,b.RowEnd,b.ColumnEnd,out double distance,out double maximum);
            var angle=Math.Abs(HMisc.AngleLl(a.RowBegin,a.ColumnBegin,a.RowEnd,a.ColumnEnd,b.RowBegin,b.ColumnBegin,b.RowEnd,b.ColumnEnd)*180/Math.PI)%180;
            if(angle>90)angle=180-angle;
            var closest=Closest(a,b);
            if(!double.IsFinite(distance)||distance<0||!double.IsFinite(angle)||Math.Abs(HMisc.DistancePp(closest.A.Row,closest.A.Column,closest.B.Row,closest.B.Column)-distance)>1e-7*Math.Max(1,maximum))
                throw new InvalidDataException("MEASUREMENT_INCONSISTENT：HALCON 距离与绘图端点不一致，未发布结果。");
            measurement=new(a,b,angle,distance,closest.A,closest.B);
            status=new(LinePairStatus.Measured,"线线测量完成 · 有限线段距离与0～90°小夹角");
        }
        context.Log(status.Message); token.ThrowIfCancellationRequested();
        HImage? preview = null;
        try
        {
            using var owned = new OwnedPreviewImage(image, token); preview = owned.TakeImage();
            if(measurement is not null)
            {
                if(showLines){PaintLine(measurement.LineA,[50,205,50]);PaintLine(measurement.LineB,[0,210,255]);}
                if(showConnector)PaintLine(new(measurement.ClosestA.Row,measurement.ClosestA.Column,measurement.ClosestB.Row,measurement.ClosestB.Column),[255,210,0]);
            }
            token.ThrowIfCancellationRequested();
            var overlay=new LinePairOverlay(measurement,showLines,showConnector);
            var ports=new Dictionary<string,JsonElement>{["angle"]=J(measurement?.AngleDegrees),["segmentDistance"]=J(measurement?.SegmentDistance),
                ["measurement"]=J(measurement),["overlay"]=J(overlay),["status"]=J(status),["preview"]=J(new{width,height,count=1,channels=3})};
            var runtime=new Dictionary<string,object?>{["angle"]=measurement?.AngleDegrees,["segmentDistance"]=measurement?.SegmentDistance,
                ["measurement"]=measurement,["overlay"]=overlay,["status"]=status,["preview"]=preview};
            preview = null; return ValueTask.FromResult(new NodeExecutionOutput(ports, runtime));
            void PaintLine(Line2D segment, int[] color) { if (segment.Length > 1e-12) Paint(preview!, [new(segment.RowBegin, segment.ColumnBegin), new(segment.RowEnd, segment.ColumnEnd)], color); }
        }
        finally { preview?.Dispose(); }

        bool Inside(double row, double col) => double.IsFinite(row) && double.IsFinite(col) && row >= 0 && col >= 0 && row <= height - 1 && col <= width - 1;
        void CheckLine(Line2D? line,string key)
        {
            if(line is null)return;
            if(!new[]{line.RowBegin,line.ColumnBegin,line.RowEnd,line.ColumnEnd,line.Length}.All(double.IsFinite)||line.Length<=1e-9||!Inside(line.RowBegin,line.ColumnBegin)||!Inside(line.RowEnd,line.ColumnEnd))
                throw new InvalidDataException("测量直线端点无效、重合或越界："+key);
            Origin(key);
        }
        T? Read<T>(string key) where T : class
        {
            var present = context.RuntimeInputs.TryGetValue(key, out var raw);
            if (present && raw is null) return null;
            if (raw is T typed) return typed;
            if (present && raw is not JsonElement) throw new InvalidDataException("测量端口类型错误：" + key);
            var json = raw is JsonElement value ? value : context.Inputs.GetValueOrDefault(key);
            if (json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            if (json.ValueKind != JsonValueKind.Object) throw new InvalidDataException("测量端口需要几何数据：" + key);
            return json.Deserialize<T>();
        }
        void Origin(string key)
        {
            var origin = context.InputFrames?.GetValueOrDefault(key);
            if (origin is null || origin.RunId != context.RunId || origin.FrameId != frame.FrameId || origin.SpaceId != frame.SpaceId ||
                origin.SourceNodeId != frame.SourceNodeId || origin.SourcePortId != frame.SourcePortId || origin.Width != width || origin.Height != height ||
                origin.Count != 1 || origin.ImageIndex != 1 || origin.Annotated || origin.Corrected != frame.Corrected)
                throw new InvalidDataException("MEASUREMENT_FRAME_MISMATCH：两条直线与原图必须来自本轮同帧及坐标空间：" + key);
        }
        bool Flag(string key) => !context.Node.Parameters.TryGetValue(key, out var value) ? true : value.ValueKind == JsonValueKind.True ? true :
            value.ValueKind == JsonValueKind.False ? false : throw new InvalidDataException("测量显示开关无效：" + key);
    }
    // For disjoint planar segments, a closest pair contains an endpoint.
    // Intersecting segments share a point; no extension is used for distance.
    private static (Point2D A,Point2D B) Closest(Line2D a,Line2D b)
    {
        var p=new Point2D(a.RowBegin,a.ColumnBegin);var q=new Point2D(b.RowBegin,b.ColumnBegin);
        var ur=a.RowEnd-a.RowBegin;var uc=a.ColumnEnd-a.ColumnBegin;var vr=b.RowEnd-b.RowBegin;var vc=b.ColumnEnd-b.ColumnBegin;
        var cross=ur*vc-uc*vr;
        if(cross!=0)
        {
            var t=((q.Row-p.Row)*vc-(q.Column-p.Column)*vr)/cross;
            var u=((q.Row-p.Row)*uc-(q.Column-p.Column)*ur)/cross;
            if(t>=0&&t<=1&&u>=0&&u<=1){var point=new Point2D(p.Row+t*ur,p.Column+t*uc);return(point,point);}
        }
        var endA=new Point2D(a.RowEnd,a.ColumnEnd);var endB=new Point2D(b.RowEnd,b.ColumnEnd);
        return new[]{(A:p,B:Project(p,b)),(A:endA,B:Project(endA,b)),(A:Project(q,a),B:q),(A:Project(endB,a),B:endB)}
            .MinBy(pair=>Math.Pow(pair.A.Row-pair.B.Row,2)+Math.Pow(pair.A.Column-pair.B.Column,2));
        static Point2D Project(Point2D point,Line2D line)
        {
            var dr=line.RowEnd-line.RowBegin;var dc=line.ColumnEnd-line.ColumnBegin;
            var t=Math.Clamp(((point.Row-line.RowBegin)*dr+(point.Column-line.ColumnBegin)*dc)/(dr*dr+dc*dc),0,1);
            return new(line.RowBegin+t*dr,line.ColumnBegin+t*dc);
        }
    }
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static void Paint(HImage image, IReadOnlyList<Point2D> points, int[] color)
    {
        using var rows = new HTuple(points.Select(p => p.Row).ToArray()); using var columns = new HTuple(points.Select(p => p.Column).ToArray());
        using var contour = new HXLDCont(); contour.GenContourPolygonXld(rows, columns);
        using var border = contour.GenRegionContourXld("margin"); using var thick = border.DilationCircle(1d); using var rgb = new HTuple(color);
        image.OverpaintRegion(thick, rgb, "fill");
    }
}
