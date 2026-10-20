using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;
using HalconVision.Halcon;
using HalconVision.Persistence;

internal static class Program
{
    private static int _checks;
    private static readonly List<string> Passed = [];
    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("检查失败：" + name);
        _checks++; Passed.Add(name);
    }
    private static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("用法：<新的检查输出目录>"); return 2; }
        var output = Path.GetFullPath(args[0]);
        if (Directory.Exists(output)) { Console.Error.WriteLine("输出目录已存在，避免覆盖检查文件。"); return 2; }
        Directory.CreateDirectory(output);
        try
        {
            CheckPolicy();
            var input = Path.Combine(output, "input"); Directory.CreateDirectory(input);
            var templatePath = Path.Combine(output, "template.png");
            using var blank = new HImage(); blank.GenImageConst("byte", 600, 480);
            using var upright = new HRegion(); upright.GenRectangle1(110d, 120d, 190d, 145d);
            using var baseBar = new HRegion(); baseBar.GenRectangle1(170d, 120d, 190d, 205d);
            using var dot = new HRegion(); dot.GenCircle(130d, 182d, 10d);
            using var shape1 = blank.PaintRegion(upright, 210d, "fill");
            using var shape2 = shape1.PaintRegion(baseBar, 210d, "fill");
            using var template = shape2.PaintRegion(dot, 210d, "fill");
            template.WriteImage("png", 0, templatePath);
            var hom = new HHomMat2D(); hom.VectorAngleToRigid(152.5, 162.5, 0d, 280d, 380d, 22 * Math.PI / 180);
            using var target = template.AffineTransImage(hom, "constant", "false");
            var targetPath = Path.Combine(input, "01-rotated.png"); target.WriteImage("png", 0, targetPath);
            var blankPath = Path.Combine(input, "02-empty.png"); blank.WriteImage("png", 0, blankPath);
            var secondHom = new HHomMat2D(); secondHom.VectorAngleToRigid(152.5, 162.5, 0d, 140d, 220d, -25 * Math.PI / 180);
            using var second = template.AffineTransImage(secondHom, "constant", "false");
            // MaxImage intersects domains; restore full domains so one transformed frame
            // does not cut away the second target when composing the test fixture.
            using var targetFull = target.FullDomain(); using var secondFull = second.FullDomain();
            using var multi = targetFull.MaxImage(secondFull);
            var multiPath = Path.Combine(output, "multiple.png"); multi.WriteImage("png", 0, multiPath);
            using var timeoutImage = multi.ZoomImageFactor(8, 8, "constant");
            var timeoutPath = Path.Combine(output,"timeout-input.png"); timeoutImage.WriteImage("png",0,timeoutPath);

            using var cache = new ShapeMatchModelCache(2); using var camera = new HalconCameraManager();
            var registry = BuiltInNodeCatalog.Create();
            registry.Register(new HalconNodeProvider(), [new HalconImageReadExecutor(), new HalconCameraCaptureExecutor(camera),
                new HalconThresholdExecutor(), new HalconBlobAnalysisExecutor(), new HalconStraightLineFindExecutor(),
                new HalconSemanticSegmentationExecutor(), new HalconClassificationExecutor(), new HalconObjectDetectionExecutor(), new HalconQuickMatchExecutor(cache), new HalconPositionCorrectionExecutor()]);
            var definition = registry.Definitions.Single(item => item.TypeId == HalconNodeProvider.QuickMatch);
            var reader = new NodeInstance { TypeId = HalconNodeProvider.ImageRead, DisplayName = "读取测试图像", X = 60, Y = 110,
                Parameters = new() { ["path"] = JsonSerializer.SerializeToElement(input) } };
            var match = new NodeInstance { TypeId = HalconNodeProvider.QuickMatch, DisplayName = "快捷匹配", X = 340, Y = 110,
                Parameters = definition.Parameters.Where(item => item.DefaultValue.HasValue).ToDictionary(item => item.Id, item => item.DefaultValue!.Value) };
            match.Parameters["templateImage"] = JsonSerializer.SerializeToElement(templatePath);
            match.Parameters["templateRoi"] = JsonSerializer.SerializeToElement("90,100,215,225");
            var flow = new FlowDocument { Name = "快捷匹配检查", Nodes = [reader, match], Edges = [new(Guid.NewGuid(), reader.Id, "image", match.Id, "image")],
                DetectionPanels = [new(Guid.NewGuid(), reader.Id, Contents: [new(Guid.NewGuid(), reader.Id, Overlays: [new(match.Id)])])] };
            var engine = new WorkflowExecutionEngine(registry, new WorkflowGraphValidator(registry));

            WorkflowExecutionRun Run(string path, params (string Key, object Value)[] options)
            {
                var changed = match with { Parameters = new(match.Parameters) };
                foreach (var (key, value) in options) changed.Parameters[key] = JsonSerializer.SerializeToElement(value);
                return engine.RunAsync(flow with { Nodes = [reader with { Parameters = new() { ["path"] = JsonSerializer.SerializeToElement(path) } }, changed] }).GetAwaiter().GetResult();
            }
            NodeExecutionResult Result(WorkflowExecutionRun run)
            { Check(run.Status == WorkflowRunStatus.Success, "实际流程成功：" + string.Join(";", run.Errors)); return run.NodeResults.Single(item => item.NodeId == match.Id); }
            void DisposeRun(WorkflowExecutionRun run)
            {
                var objects = run.RuntimeOutputs?.Values.SelectMany(row => row.Values).OfType<HObject>().ToArray() ?? [];
                run.Dispose(); Check(objects.All(item => !item.IsInitialized()), "运行期图像释放");
            }
            var first = Run(targetPath); var found = Result(first);
            var pose = found.Outputs["matches"].Deserialize<QuickMatchPose[]>()!.Single();
            Check(Math.Abs(pose.Row - 280) < 3 && Math.Abs(pose.Column - 380) < 3, "平移位置正确");
            Check(Math.Abs(pose.AngleDegrees - 22) < 2, "旋转角度正确");
            Check(pose.Score >= .6 && found.Outputs["status"].Deserialize<QuickMatchStatus>()!.Code == QuickMatchStatus.Found, "找到状态与评分");
            Check(!found.Outputs["modelInfo"].GetProperty("cacheHit").GetBoolean(), "首次创建模型");
            ((HImage)first.RuntimeOutputs![match.Id]["preview"]!).WriteImage("png", 0, Path.Combine(output, "matched-preview.png"));
            File.WriteAllText(Path.Combine(output, "first-result.json"), JsonSerializer.Serialize(found, new JsonSerializerOptions { WriteIndented = true }));
            DisposeRun(first);
            var empty = Run(blankPath); var absent = Result(empty);
            Check(absent.Outputs["count"].GetInt32() == 0 && absent.Outputs["matches"].GetArrayLength() == 0 && absent.Outputs["row"].ValueKind == JsonValueKind.Null,
                "空结果无旧坐标和假零坐标");
            Check(absent.Outputs["status"].Deserialize<QuickMatchStatus>()!.Code == QuickMatchStatus.NotFound, "未找到状态明确");
            DisposeRun(empty);
            var strict = Run(targetPath, ("minimumScore", 1d));
            Check(Result(strict).Outputs["count"].GetInt32() == 0, "匹配评分阈值生效"); DisposeRun(strict);
            var many = Run(multiPath, ("maximumMatches", 2));
            File.WriteAllText(Path.Combine(output, "multiple-result.json"), JsonSerializer.Serialize(Result(many), new JsonSerializerOptions { WriteIndented = true }));
            Check(Result(many).Outputs["count"].GetInt32() == 2, "多个目标匹配"); DisposeRun(many);
            var ambiguous = Run(multiPath); var rejected = Result(ambiguous);
            Check(rejected.Outputs["status"].Deserialize<QuickMatchStatus>()!.Code == QuickMatchStatus.Ambiguous &&
                rejected.Outputs["rawCandidates"].GetArrayLength() >= 2 && rejected.Outputs["matches"].GetArrayLength() == 0 &&
                rejected.Outputs["row"].ValueKind == JsonValueKind.Null && ((PoseSet)ambiguous.RuntimeOutputs![match.Id]["poses"]!).Images.Single().Matches.Count == 0,
                "双目标保留候选但无可信位姿及标量");
            File.WriteAllText(Path.Combine(output,"ambiguous-result.json"),JsonSerializer.Serialize(rejected,new JsonSerializerOptions{WriteIndented=true})); DisposeRun(ambiguous);
            var outside = Run(targetPath,("acceptanceRoi","0,0,100,100"));
            Check(Result(outside).Outputs["status"].Deserialize<QuickMatchStatus>()!.Code == QuickMatchStatus.OutOfRange &&
                Result(outside).Outputs["count"].GetInt32() == 0,"实际匹配位置越界拒绝"); DisposeRun(outside);
            var excluded = Run(targetPath, ("roi", "0,0,80,80"));
            Check(Result(excluded).Outputs["count"].GetInt32() == 0, "搜索范围排除目标"); DisposeRun(excluded);
            var included = Run(targetPath, ("roi", "220,300,350,470"));
            Check(Result(included).Outputs["count"].GetInt32() == 1, "搜索范围包含目标"); DisposeRun(included);
            var batch = Run(input); var batches = Result(batch);
            Check(batches.Outputs["imageResults"].GetArrayLength() == 2 && batches.Outputs["row"].ValueKind == JsonValueKind.Null, "逐图结果与多图标量为空");
            Check(((HImage)batch.RuntimeOutputs![match.Id]["preview"]!).CountObj() == 2, "批量预览数量"); DisposeRun(batch);
            Check(cache.ModelLoads == 1, "参数调整与多轮执行复用模型");
            var modern=match;
            match=match with{Parameters=match.Parameters.Where(p=>p.Key is not ("candidateLimit" or "candidateMinimumScore" or "acceptanceRoi" or "minimumScoreGap" or "candidatePositionTolerance" or "candidateAngleTolerance")).ToDictionary(p=>p.Key,p=>p.Value)};
            var legacy=Run(targetPath); Check(Result(legacy).Outputs["matches"].GetArrayLength()==1,"旧项目未存可靠性参数时使用兼容默认值");DisposeRun(legacy);match=modern;
            for (var index = 0; index < 50; index++)
            {
                var repeated = Run(targetPath); var value = Result(repeated);
                Check(value.Outputs["count"].GetInt32() == 1 && value.Outputs["modelInfo"].GetProperty("cacheHit").GetBoolean(), "连续匹配缓存复用");
                DisposeRun(repeated);
            }
            Check(cache.Count == 1 && cache.ModelLoads == 1, "缓存数量保持有界");
            var invalid = Run(targetPath, ("roi", "-100,-100,-20,-20"));
            Check(invalid.Status == WorkflowRunStatus.Failed && !invalid.RuntimeOutputs!.ContainsKey(match.Id) && cache.Count == 0, "错误释放模型且无旧输出"); DisposeRun(invalid);
            var recovered = Run(targetPath); Result(recovered); DisposeRun(recovered);
            Check(cache.ModelLoads == 2, "错误后重新创建模型");
            var retained = Run(targetPath); Result(retained);
            cache.Clear(); Check(cache.Count == 0, "停止清理模型");
            Check(((HImage)retained.RuntimeOutputs![match.Id]["preview"]!).IsInitialized(), "清理模型后保留结果图仍有效"); DisposeRun(retained);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            using var cancelled = engine.RunAsync(flow, cancellationToken: cancellation.Token).GetAwaiter().GetResult();
            Check(cancelled.Status == WorkflowRunStatus.Cancelled && cache.Count == 0, "取消后不创建模型");
            var changedRoi = Run(targetPath, ("templateRoi", "92,102,213,223"));
            Check(!Result(changedRoi).Outputs["modelInfo"].GetProperty("cacheHit").GetBoolean(), "修改模板范围后重建模型"); DisposeRun(changedRoi);
            var realPath = Path.Combine(Environment.GetEnvironmentVariable("HALCONIMAGES") ?? "", "pcb.png");
            if (!File.Exists(realPath)) throw new FileNotFoundException("真实示例验证需要 HALCONIMAGES/pcb.png。", realPath);
            using var pcb = new HImage(realPath); pcb.GetImageSize(out int pcbWidth, out int pcbHeight);
            var top = pcbHeight / 5; var left = pcbWidth / 5; var bottom = pcbHeight * 3 / 5; var right = pcbWidth * 3 / 5;
            var real = Run(realPath, ("templateImage", realPath), ("templateRoi", $"{top},{left},{bottom},{right}"), ("angleStart", -15d), ("angleExtent", 30d));
            var realInitial=Result(real);
            File.WriteAllText(Path.Combine(output,"pcb-full-search.json"),JsonSerializer.Serialize(realInitial,new JsonSerializerOptions{WriteIndented=true}));
            DisposeRun(real);
            var centerRow=(top+bottom)/2d; var centerCol=(left+right)/2d;
            real = Run(realPath, ("templateImage",realPath),("templateRoi",$"{top},{left},{bottom},{right}"),
                ("roi",$"{centerRow-15},{centerCol-15},{centerRow+15},{centerCol+15}"),("angleStart",-15d),("angleExtent",30d));
            File.WriteAllText(Path.Combine(output,"pcb-scoped-result.json"),JsonSerializer.Serialize(Result(real),new JsonSerializerOptions{WriteIndented=true}));
            var realPose = Result(real).Outputs["matches"].Deserialize<QuickMatchPose[]>()!.Single();
            Check(Math.Abs(realPose.Row - (top + bottom) / 2d) < 3 && Math.Abs(realPose.Column - (left + right) / 2d) < 3 && Math.Abs(realPose.AngleDegrees) < 2,
                "HALCON 官方 PCB 真实图像定位");
            ((HImage)real.RuntimeOutputs![match.Id]["preview"]!).WriteImage("png", 0, Path.Combine(output, "pcb-preview.png"));
            File.WriteAllText(Path.Combine(output, "pcb-result.json"), JsonSerializer.Serialize(real.NodeResults.Single(item => item.NodeId == match.Id), new JsonSerializerOptions { WriteIndented = true }));
            DisposeRun(real); Check(cache.Count == 1, "有效模板仍在缓存供下一轮复用");
            var missingModel=Run(targetPath,("templateImage",Path.Combine(output,"does-not-exist.png")));
            Check(Result(missingModel).Outputs["status"].Deserialize<QuickMatchStatus>()!.Code==QuickMatchStatus.ModelInvalid&&
                Result(missingModel).Outputs["row"].ValueKind==JsonValueKind.Null&&cache.Count==0,"缺失模板结构化拒绝且不复用旧位姿"); DisposeRun(missingModel);
            var timeoutRun=Run(timeoutPath,("timeoutMs",1));
            File.WriteAllText(Path.Combine(output,"timeout-result.json"),JsonSerializer.Serialize(Result(timeoutRun),new JsonSerializerOptions{WriteIndented=true}));
            Check(Result(timeoutRun).Outputs["status"].Deserialize<QuickMatchStatus>()!.Code==QuickMatchStatus.Timeout&&
                Result(timeoutRun).Outputs["row"].ValueKind==JsonValueKind.Null&&cache.Count==0,"真实 HALCON 超时不输出位姿并清理模型");DisposeRun(timeoutRun);

            var store = new ProjectStore(); var projectPath = Path.Combine(output, "快捷匹配检查.hvproj");
            var workspace = store.CreateProject(projectPath, "快捷匹配检查");
            store.SaveProject(workspace with { Project = workspace.Project with { Flows = [flow], DefaultFlowId = flow.Id.ToString("N") } });
            var restored = store.OpenProject(projectPath).Project.Flows.Single();
            Check(restored.Nodes.Single(item => item.Id == match.Id).Parameters.All(pair => pair.Value.GetRawText() == match.Parameters[pair.Key].GetRawText()) && restored.Edges.Count == 1,
                "项目保存恢复模板路径、ROI和参数");
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { checks = _checks, passed = Passed, pose, modelLoads = cache.ModelLoads,
                note = "使用实际 HALCON 算子和可控几何图像验证，不代表用户产品精度或长期稳定性验收。" }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {_checks} 项；位置({pose.Row:F2},{pose.Column:F2})，角度 {pose.AngleDegrees:F2}°，评分 {pose.Score:F3}");
            return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "error.txt"), error.ToString()); Console.Error.WriteLine(error); return 1; }
    }

    private static void CheckPolicy()
    {
        var policy=new MatchCandidatePolicy(.6,1,5,2,2,.05,-180,360);
        QuickMatchPose P(double score=.9,double row=100,double col=100,double angle=0)=>new(row,col,angle,score);
        QuickMatchImageResult E(params QuickMatchPose[] p)=>MatchCandidateEvaluator.Evaluate(1,p,policy,600,480);
        void Reject(QuickMatchImageResult r,string code,string label)=>Check(r.Status!.Code==code&&r.Matches.Count==0,label);
        Check(E(P()).Matches.Count==1,"独立可靠候选接受");
        Reject(E(),QuickMatchStatus.NotFound,"空候选拒绝");
        Reject(E(P(.59)),QuickMatchStatus.NotFound,"低评分拒绝");
        Reject(E(P(),P(.88,200)),QuickMatchStatus.Ambiguous,"相似干扰候选拒绝");
        Reject(E(P(),P(.65,200)),QuickMatchStatus.Ambiguous,"额外合格目标即使评分差大也拒绝");
        Reject(E(P(.62),P(.59,200)),QuickMatchStatus.Ambiguous,"阈值附近竞争候选拒绝");
        Check(E(P(),P(.5,200)).Matches.Count==1,"低质量弱候选不干扰");
        Check(E(P(),P(.88,101,101,1)).Matches.Count==1,"同位置近角度重复候选合并");
        Reject(E(P(),P(.88,100,100,180)),QuickMatchStatus.Ambiguous,"同位置对称方向拒绝");
        Check(MatchCandidateEvaluator.AngleDistance(-179,179)==2,"跨正负180度角差正确");
        Reject(E(P(row:-1)),QuickMatchStatus.OutOfRange,"图像外坐标拒绝");
        Reject(E(P(row:double.NaN)),QuickMatchStatus.Rejected,"无效原始候选不进入序列化");
        Reject(MatchCandidateEvaluator.Evaluate(1,[P(angle:11)],policy with{AngleStart=-10,AngleExtent=20},600,480),QuickMatchStatus.OutOfRange,"原生细化越出搜索角度再次检查");
        Reject(MatchCandidateEvaluator.Evaluate(1,[P(),P(.9,200)],policy with{ExpectedCount=3},600,480),QuickMatchStatus.NotFound,"目标数量不足拒绝");
        Reject(MatchCandidateEvaluator.Evaluate(1,[P(),P(.9,100,100,180)],policy with{ExpectedCount=2},600,480),QuickMatchStatus.Ambiguous,"多个方向不能当两个实体");
        Reject(MatchCandidateEvaluator.Evaluate(1,[P(),P(.5,200)],policy with{CandidateLimit=2},600,480),QuickMatchStatus.Ambiguous,"搜索截断不能证明唯一");
    }
}
