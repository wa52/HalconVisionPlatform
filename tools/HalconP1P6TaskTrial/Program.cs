using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;
using HalconVision.Halcon;
using HalconVision.Persistence;
using HalconVision.App;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly List<string> Checks = [];
    private static readonly List<object> Runs = [];
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); Checks.Add(message); }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is < 5 or > 7 || args[4] is not ("cache-probe" or "cache-lifecycle" or "create-ui" or "memory-probe" or "preprocess-probe" or "preprocess-check" or "verify") || (args[4] is "verify" or "create-ui" or "memory-probe" or "preprocess-probe") && args.Length != 6 || args[4]=="preprocess-check" && args.Length!=7)
        { Console.Error.WriteLine("用法：<四节点1111方案> <正常原图> <负样本原图> <全新输出目录> <cache-probe|cache-lifecycle|create-ui|verify|memory-probe|preprocess-probe|preprocess-check> [既有标签审查summary.json] [preprocess-check所需基线preprocessing.json]"); return 2; }
        var output = Path.GetFullPath(args[3]);
        if (Directory.Exists(output) || File.Exists(output)) { Console.Error.WriteLine("拒绝覆盖已有输出。"); return 2; }
        Directory.CreateDirectory(output);
        try
        {
            var source = Path.GetFullPath(args[0]); var positive = Path.GetFullPath(args[1]); var negative = Path.GetFullPath(args[2]);
            var store = new ProjectStore(); var original = store.OpenProject(source); var old = original.Project.Flows.Single();
            var detector = old.Nodes.Single(n => n.TypeId == HalconNodeProvider.ObjectDetection);
            var reader = old.Nodes.Single(n => n.TypeId == HalconNodeProvider.ImageRead);
            var corrected = old.Nodes.Single(n => n.TypeId == HalconNodeProvider.PositionCorrection);
            var matcher = old.Nodes.Single(n => n.TypeId == HalconNodeProvider.QuickMatch);
            var package = detector.Parameters["modelPackage"].GetString()!;
            var readOnly = new[] { source, positive, negative, matcher.Parameters["templateImage"].GetString()!,
                Path.Combine(package,"model.hdl"), Path.Combine(package,"preprocess.hdict"), Path.Combine(package,"manifest.json") }.ToDictionary(p => p, Hash);
            if(args[4] is "verify" or "create-ui" or "memory-probe" or "preprocess-probe" or "preprocess-check")
            {
                using var proof=JsonDocument.Parse(File.ReadAllText(args[5]));
                var audited=proof.RootElement.GetProperty("hashes").EnumerateObject().ToDictionary(p=>Path.GetFullPath(p.Name),p=>p.Value.GetString()!,StringComparer.OrdinalIgnoreCase);
                Check(new[]{source,positive,negative,Path.Combine(package,"model.hdl"),Path.Combine(package,"preprocess.hdict"),Path.Combine(package,"manifest.json")}.All(p=>audited.TryGetValue(p,out var hash)&&Hash(p)==hash),"本轮方案、正负样本和模型与既有标签审查逐文件哈希一致");
                Check(audited.All(p=>File.Exists(p.Key)&&Hash(p.Key)==p.Value),"原始标签、训练裁图及对应分类裁图均保持审查时哈希");
                foreach(var item in audited)readOnly.TryAdd(item.Key,item.Value);
                readOnly.TryAdd(Path.GetFullPath(args[5]),Hash(args[5]));
            }
            var rois = DetectionRoi.Parse(detector.Parameters["rois"]).Where(r => r.Enabled).ToArray();
            Check(rois.Select(r => r.Name).SequenceEqual(new[] { "P1","P2","P3","P4","P5","P6" }), "沿用原 P1–P6 六区域顺序");
            var nodes = rois.Select((roi,i) => detector with { Id=Guid.NewGuid(), DisplayName=roi.Name+" 检测", X=850, Y=30+i*190,
                Parameters=new(detector.Parameters){["rois"]=J(new[]{roi})} }).ToArray();
            // Explicit defaults on the second node must share the same runtime as omitted defaults.
            nodes[1]=nodes[1] with{Parameters=new(nodes[1].Parameters){["deviceRuntime"]=J("gpu"),["deviceIndex"]=J(0)}};
            var regions=rois.Select(r=>new InspectionRegion(r.Id,r.Name,corrected.Id,"image",new(r.RowBegin,r.ColumnBegin,r.RowEnd,r.ColumnEnd))).ToList();
            var tasks=regions.Select((r,i)=>new InspectionTask(Guid.NewGuid(),r.Name+" 必检",r.Id,[nodes[i].Id],
                [new(Guid.NewGuid(),nodes[i].Id,"decision",InspectionRuleKind.DetectionDecision,"/Code")])).ToList();
            var flow=old with{SchemaVersion=2,Name="P1–P6 独立检测任务",Nodes=old.Nodes.Where(n=>n.Id!=detector.Id).Concat(nodes).ToList(),
                Edges=old.Edges.Where(e=>e.ToNodeId!=detector.Id&&e.FromNodeId!=detector.Id).Concat(nodes.Select(n=>new FlowEdge(Guid.NewGuid(),corrected.Id,"image",n.Id,"image"))).ToList(),
                InspectionRegions=regions,InspectionTasks=tasks,
                DetectionPanels=[new(Guid.NewGuid(),corrected.Id,Contents:[new(Guid.NewGuid(),corrected.Id,Overlays:nodes.Select(n=>new DetectionOverlaySettings(n.Id,true,false,[])).ToList())])]};
            var configurationIdentity=InspectionPlan.ConfigurationIdentity(flow);
            Check(configurationIdentity!="INVALID","独立任务配置身份有效");
            using var shapes=new ShapeMatchModelCache(1); using var sessions=new DlModelSessionCache(2); using var camera=new HalconCameraManager();
            var registry=BuiltInNodeCatalog.Create();
            registry.Register(new HalconNodeProvider(),[new HalconImageReadExecutor(),new HalconCameraCaptureExecutor(camera),new HalconThresholdExecutor(),new HalconBlobAnalysisExecutor(),
                new HalconStraightLineFindExecutor(),new HalconSemanticSegmentationExecutor(sessions),new HalconClassificationExecutor(sessions),
                new HalconObjectDetectionExecutor(sessions),new HalconQuickMatchExecutor(shapes),new HalconPositionCorrectionExecutor()]);
            var validator=new WorkflowGraphValidator(registry); var engine=new WorkflowExecutionEngine(registry,validator);
            Check(validator.Validate(flow).Count==0,"九节点六必检任务图校验通过");

            WorkflowExecutionRun Execute(FlowDocument configured,string image,CancellationToken token=default,IProgress<NodeExecutionResult>? progress=null)
            {
                var currentReader=configured.Nodes.Single(n=>n.TypeId==HalconNodeProvider.ImageRead).Id;
                var prepared=configured with{Nodes=configured.Nodes.Select(n=>n.Id==currentReader?n with{Parameters=new(n.Parameters){["path"]=J(image)}}:n).ToList()};
                return engine.RunAsync(prepared,progress,token,configured).GetAwaiter().GetResult();
            }
            FlowDocument Only(int count)=>flow with{Nodes=flow.Nodes.Where(n=>n.TypeId!=HalconNodeProvider.ObjectDetection||nodes.Take(count).Any(x=>x.Id==n.Id)).ToList(),
                Edges=flow.Edges.Where(e=>e.FromNodeId!=corrected.Id||nodes.Take(count).Any(n=>n.Id==e.ToNodeId)).ToList(),InspectionTasks=tasks.Take(count).ToList()};
            void Release(WorkflowExecutionRun run)
            {
                var iconic=run.RuntimeOutputs?.Values.SelectMany(v=>v.Values).OfType<HObject>().ToArray()??[];
                run.Dispose(); Check(iconic.All(o=>!o.IsInitialized()),"本轮全部 native 输出释放");
            }
            RoiObjectDetectionResult Row(WorkflowExecutionRun run,NodeInstance node)=>run.NodeResults.Single(n=>n.NodeId==node.Id).Outputs["roiResults"].Deserialize<RoiObjectDetectionResult[]>()!.Single();
            void CheckDetectionPreviews(WorkflowExecutionRun run)
            {
                var sourceImage=(HImage)run.RuntimeOutputs![corrected.Id]["image"]!;var sourcePixels=PreviewHash(sourceImage);
                foreach(var node in nodes)
                {
                    var overlay=(MultiRoiOverlay)run.RuntimeOutputs[node.Id]["roiOverlay"]!;var expected=sourceImage.CopyImage();
                    try
                    {
                        foreach(var label in overlay.Rois)
                        {
                            var box=label.Bounds;using var region=new HRegion(box.RowBegin,box.ColumnBegin,box.RowEnd,box.ColumnEnd);
                            using var color=new HTuple(label.Decision!.Code switch{ClassificationDecision.Ok=>new[]{50,205,50},ClassificationDecision.DetectedNg=>new[]{30,144,255},_=>new[]{255,0,0}});
                            var next=expected.PaintRegion(region,color,"margin");expected.Dispose();expected=next;
                        }
                        Check(PreviewHash((HImage)run.RuntimeOutputs[node.Id]["preview"]!)==PreviewHash(expected),"真实检测节点预览与原RGB PaintRegion路径像素一致："+node.DisplayName);
                    }
                    finally{expected.Dispose();}
                }
                Check(PreviewHash(sourceImage)==sourcePixels,"六节点预览绘制不污染矫正底图");
            }
            void Record(WorkflowExecutionRun run,string name)
            {
                var rows=nodes.Where(n=>run.NodeResults.Any(r=>r.NodeId==n.Id&&r.Status==NodeExecutionStatus.Success)).Select(n=>new{node=n.DisplayName,row=Row(run,n),
                    modelInfo=run.NodeResults.Single(r=>r.NodeId==n.Id).Outputs["modelInfo"]}).ToArray();
                Runs.Add(new{name,run.Status,inspection=run.Inspection,ms=(run.EndedAtUtc-run.StartedAtUtc).TotalMilliseconds,rows});
                Console.WriteLine($"{name}: {run.Status} / 产品 {run.Inspection?.Label}; "+string.Join("; ",rows.Select(r=>$"{r.row.RoiName}={r.row.Decision.Code} cache={r.modelInfo.GetProperty("cacheHit").GetBoolean()}")));
            }
            if(args[4] is "preprocess-probe" or "preprocess-check")
            {
                var samples = new List<object>(); var times = new List<double>();
                var processingType=typeof(DlModelSessionCache).Assembly.GetType("HalconVision.Halcon.DlPreprocessing",true)!;
                var apply=processingType.GetMethod("Apply",BindingFlags.Public|BindingFlags.Static|BindingFlags.Instance)!;
                var setup=Stopwatch.StartNew();var processor=apply.IsStatic?null:Activator.CreateInstance(processingType);setup.Stop();
                using var input=new HImage(positive); using var empty=new HTuple(); using var preprocess=new HDict(Path.Combine(package,"preprocess.hdict"),empty,empty);
                string Pixels(HDict sample)
                {
                    using var processed=sample.GetDictObject("image");HOperatorSet.CountChannels(processed,out var channels);using(channels)
                    {
                        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        for(var channel=1;channel<=channels.I;channel++)
                        {
                            HOperatorSet.AccessChannel(processed,out var plane,channel);using(plane)
                            {
                                HOperatorSet.GetImagePointer1(plane,out var pointer,out var pixelType,out var w,out var h);
                                using(pointer)using(pixelType)using(w)using(h)
                                {
                                    Check(pixelType.S=="real","官方预处理输出real像素");var data=new byte[checked(w.I*h.I*4)];
                                    Marshal.Copy(new IntPtr(pointer.L),data,0,data.Length);hash.AppendData(data);
                                }
                            }
                        }
                        return Convert.ToHexString(hash.GetHashAndReset());
                    }
                }
                try
                {
                    for(var round=0;round<24;round++)
                    {
                        var roi=rois[round%6];using var cropped=input.CropRectangle1(roi.RowBegin,roi.ColumnBegin,roi.RowEnd,roi.ColumnEnd);
                        using var sample=new HDict();sample.SetDictObject(cropped,"image");var watch=Stopwatch.StartNew();
                        apply.Invoke(processor,[sample,preprocess,CancellationToken.None]);watch.Stop();times.Add(watch.Elapsed.TotalMilliseconds);
                        samples.Add(new{round=round+1,roi=roi.Name,ms=watch.Elapsed.TotalMilliseconds,hash=Pixels(sample)});
                    }
                    var measurements=new{setupMs=setup.Elapsed.TotalMilliseconds,coldMs=times[0],warmMedianMs=times.Skip(6).Order().ElementAt(9),samples};
                    File.WriteAllText(Path.Combine(output,"preprocessing.json"),JsonSerializer.Serialize(measurements,Json));
                    Console.WriteLine("预处理 "+JsonSerializer.Serialize(new{measurements.setupMs,measurements.coldMs,measurements.warmMedianMs}));
                    if(args[4]=="preprocess-check")
                    {
                        using var baseline=JsonDocument.Parse(File.ReadAllText(args[6]));
                        var actual=JsonSerializer.SerializeToElement(samples).EnumerateArray().Select(s=>s.GetProperty("hash").GetString()).ToArray();
                        var expectedHashes=baseline.RootElement.GetProperty("samples").EnumerateArray().Select(s=>s.GetProperty("hash").GetString()).ToArray();
                        Check(actual.SequenceEqual(expectedHashes),"24个实际ROI的预处理real像素逐字节与基线一致");
                        Check(measurements.warmMedianMs < baseline.RootElement.GetProperty("warmMedianMs").GetDouble()*.5,"热预处理中位耗时低于同机基线的一半");
                        Check(processor is IDisposable,"预处理过程必须有明确的模型会话所有者");
                        using(var invalid=new HDict())
                        {
                            var cancelled=false;try{apply.Invoke(processor,[invalid,preprocess,new CancellationToken(true)]);}catch(TargetInvocationException ex)when(ex.InnerException is OperationCanceledException){cancelled=true;}
                            Check(cancelled,"预处理取消先于缺失图像访问");
                            var failed=false;try{apply.Invoke(processor,[invalid,preprocess,CancellationToken.None]);}catch(TargetInvocationException ex)when(ex.InnerException is HDevEngineException){failed=true;}
                            Check(failed,"损坏样本明确报错");
                        }
                        string Reuse(object? owner)
                        {
                            var roi=rois[0];using var crop=input.CropRectangle1(roi.RowBegin,roi.ColumnBegin,roi.RowEnd,roi.ColumnEnd);
                            using var sample=new HDict();sample.SetDictObject(crop,"image");apply.Invoke(owner,[sample,preprocess,CancellationToken.None]);
                            return Pixels(sample);
                        }
                        Check(Reuse(processor)==actual[0],"失败后新调用不残留旧样本或状态");
                        Check(Task.Run(()=>Reuse(processor)).GetAwaiter().GetResult()==actual[0],"顺序切换执行线程保持实际预处理一致");
                        var second=Activator.CreateInstance(processingType)!;
                        try
                        {
                            Check(Reuse(second)==actual[0],"两个独立模型会话可分别持有官方过程");
                            ((IDisposable)processor!).Dispose();((IDisposable)processor).Dispose();
                            Check(Reuse(second)==actual[0],"释放一个模型的过程不影响另一个会话");
                            var closed=false;try{Reuse(processor);}catch(TargetInvocationException ex)when(ex.InnerException is ObjectDisposedException){closed=true;}
                            Check(closed,"关闭的预处理过程拒绝再次使用且重复释放安全");
                        }
                        finally{((IDisposable)second).Dispose();}
                    }
                }
                finally{(processor as IDisposable)?.Dispose();}
            }
            else if(args[4]=="memory-probe")
            {
                var samples = new List<object>(); var process = Process.GetCurrentProcess();
                void Sample(string phase, int round, double elapsed)
                {
                    process.Refresh();
                    long Cached(string key) { using var query = new HTuple(key); HOperatorSet.GetSystem(query, out var value); using(value) return (long)value.ToDArr().Sum(); }
                    samples.Add(new { phase, round, elapsedMs = elapsed, privateBytes = process.PrivateMemorySize64,
                        workingSet = process.WorkingSet64, managedBytes = GC.GetTotalMemory(false), allocatedBytes = GC.GetTotalAllocatedBytes(),
                        gen2Collections = GC.CollectionCount(2), sessions = sessions.Count, loads = sessions.ModelLoads,
                        threadTemporaryCache = Cached("tsp_temporary_mem_cache_block_sizes"), globalTemporaryReservoir = Cached("temporary_mem_reservoir_block_sizes") });
                    File.WriteAllText(Path.Combine(output,"memory.json"), JsonSerializer.Serialize(samples,Json));
                    Console.WriteLine($"内存采样 {phase} {round}: private={process.PrivateMemorySize64 / 1048576} MiB managed={GC.GetTotalMemory(false)/1048576} MiB");
                }
                Sample("start",0,0);
                for(var round=1;round<=60;round++)
                {
                    var normal=round%2!=0; var run=Execute(flow,normal?positive:negative);
                    try
                    {
                        Check(run.Inspection?.Verdict==(normal?InspectionVerdict.Ok:InspectionVerdict.Ng),"内存检查保持实际产品判定");
                        Check(nodes.Select((n,i)=>Row(run,n).Decision.Code==((normal||i is 1 or 2)?ClassificationDecision.Ok:ClassificationDecision.NotDetected)).All(x=>x),"内存检查六区域输出正确");
                        if(round is 1 or 60)Record(run,"memory-round-"+round);
                    }
                    finally{Release(run);}
                    Check(sessions.Count==1&&sessions.ModelLoads==1,"内存检查保持单一模型且不重载");
                    if(round==1||round%5==0)Sample("released",round,(run.EndedAtUtc-run.StartedAtUtc).TotalMilliseconds);
                }
                sessions.Clear();shapes.Clear();Sample("models-cleared",60,0);
                // Diagnostic comparison only: never introduce forced GC into the application's run loop.
                GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Sample("diagnostic-gc",60,0);
            }
            else if(args[4]=="create-ui")
            {
                var frames=Path.Combine(output,"inputs");Directory.CreateDirectory(frames);
                var copiedPositive=Path.Combine(frames,Path.GetFileName(positive));var copiedNegative=Path.Combine(frames,Path.GetFileName(negative));
                File.Copy(positive,copiedPositive);File.Copy(negative,copiedNegative);
                Check(Hash(copiedPositive)==Hash(positive)&&Hash(copiedNegative)==Hash(negative),"界面检查使用原图的逐字节副本");
                flow=flow with{Nodes=flow.Nodes.Select(n=>n.Id==reader.Id?n with{Parameters=new(n.Parameters){["path"]=J(frames)}}:n).ToList()};
                var upstream=flow with{Nodes=flow.Nodes.Where(n=>n.Id==reader.Id||n.Id==matcher.Id).ToList(),Edges=flow.Edges.Where(e=>e.ToNodeId==matcher.Id).ToList(),InspectionTasks=[],InspectionRegions=[],DetectionPanels=[]};
                var teaching=Execute(upstream,copiedNegative);PositionReference taught;
                try
                {
                    Check(teaching.Status==WorkflowRunStatus.Success,"为新的两图来源实际运行匹配示教");
                    taught=HalconPositionCorrectionExecutor.Teach((PoseSet)teaching.RuntimeOutputs![matcher.Id]["poses"]!);
                    var previous=corrected.Parameters["reference"].Deserialize<PositionReference>()!;
                    Check(Math.Abs(taught.Pose.Row-previous.Pose.Row)<.25&&Math.Abs(taught.Pose.Column-previous.Pose.Column)<.25&&Math.Abs(taught.Pose.AngleDegrees-previous.Pose.AngleDegrees)<.02,"同一基准原图重示教几何与原方案一致");
                }
                finally{Release(teaching);}
                flow=flow with{Nodes=flow.Nodes.Select(n=>n.Id==corrected.Id?n with{Parameters=new(n.Parameters){["reference"]=J(taught)}}:n).ToList()};
                foreach(var (image,normal) in new[]{(copiedNegative,false),(copiedPositive,true)})
                {
                    var run=Execute(flow,image);
                    try
                    {
                        Check(run.Inspection?.Verdict==(normal?InspectionVerdict.Ok:InspectionVerdict.Ng),"两图检查方案产品判定正确");
                        Check(nodes.Select((n,i)=>Row(run,n).Decision.Code==((normal||i is 1 or 2)?ClassificationDecision.Ok:ClassificationDecision.NotDetected)).All(x=>x),"两图方案六任务结果保持已核对标签");
                        Record(run,normal?"ui-positive":"ui-negative");
                    }
                    finally{Release(run);}
                }
                CopyAssets(original.RootPath,output,flow.AssetReferences);
                var projectPath=Path.Combine(output,"1111_P1-P6独立任务.hvproj");
                store.SaveProject(new(output,original.Project with{Id=Guid.NewGuid(),Name="1111 · P1–P6 独立任务"},projectPath),[flow]);
                Check(store.OpenProject(projectPath).Project.Flows.Single().InspectionTasks.Count==6,"可直接打开的两图方案保存六个必检任务");
                Console.WriteLine("界面检查方案："+projectPath);
            }
            else if(args.ElementAtOrDefault(4)=="cache-probe")
            {
                var probe=Execute(Only(2),positive,progress:new InlineProgress(r=>{if(r.Status!=NodeExecutionStatus.Running)Console.WriteLine(r.NodeName+" "+r.Status+" "+r.ElapsedMilliseconds.ToString("F1")+" ms");}));
                try
                {
                    Record(probe,"two-node-cache");
                    Check(probe.Status==WorkflowRunStatus.Success,string.Join(";",probe.Errors));
                    var first=probe.NodeResults.Single(n=>n.NodeId==nodes[0].Id).Outputs["modelInfo"].GetProperty("cacheHit").GetBoolean();
                    var second=probe.NodeResults.Single(n=>n.NodeId==nodes[1].Id).Outputs["modelInfo"].GetProperty("cacheHit").GetBoolean();
                    Check(!first&&second&&sessions.Count==1,"同模型不同节点必须共享一个会话，第二节点应命中（含显式/省略默认设备参数）");
                }
                finally{Release(probe);}
            }
            else if(args[4]=="cache-lifecycle")
            {
                string ClonePackage(string name)
                {
                    var copy=Path.Combine(output,name);Directory.CreateDirectory(copy);
                    foreach(var file in new[]{"model.hdl","preprocess.hdict","manifest.json","classes.json","checksums.json"})
                        if(File.Exists(Path.Combine(package,file)))File.Copy(Path.Combine(package,file),Path.Combine(copy,file));
                    return copy;
                }
                var a=ClonePackage("model-a");var b=ClonePackage("model-b");
                void Load(string directory,bool expectedHit,string name)
                {
                    var single=Only(1);single=single with{Nodes=single.Nodes.Select(n=>n.Id==nodes[0].Id?n with{Parameters=new(n.Parameters){["modelPackage"]=J(directory)}}:n).ToList()};
                    var run=Execute(single,positive);
                    try{Check(run.Inspection?.Verdict==InspectionVerdict.Ok,name+"真实推理OK");Check(run.NodeResults.Single(n=>n.NodeId==nodes[0].Id).Outputs["modelInfo"].GetProperty("cacheHit").GetBoolean()==expectedHit,name+"缓存命中符合预期");Check(sessions.Count<=2,name+"模型容量不超过2");Record(run,name);}
                    finally{Release(run);}
                }
                Load(a,false,"package-a");Load(b,false,"package-b");Load(a,true,"package-a-hit");Load(package,false,"original-evicts-b");Load(b,false,"package-b-reload");
                var manifestCopy=Path.Combine(b,"manifest.json");File.SetLastWriteTimeUtc(manifestCopy,File.GetLastWriteTimeUtc(manifestCopy).AddSeconds(2));
                Load(b,false,"metadata-version-reload");Check(sessions.ModelLoads==5,"LRU与文件元数据变化触发精确次数重载");
                var differentTask=Only(1);differentTask=differentTask with{Nodes=differentTask.Nodes.Select(n=>n.Id==nodes[0].Id?n with{TypeId=HalconNodeProvider.Classification,Parameters=new(n.Parameters){["modelPackage"]=J(b)}}:n).ToList()};
                var wrong=Execute(differentTask,positive);
                try{Check(wrong.Inspection?.Verdict==InspectionVerdict.Error&&wrong.NodeResults.Single(n=>n.NodeId==nodes[0].Id).Error!.Contains("模型任务类型不匹配"),"相同目录不同任务类型不能误用检测会话");Check(!wrong.RuntimeOutputs!.ContainsKey(nodes[0].Id),"类型错误不产生旧检测输出");}
                finally{Release(wrong);}
                sessions.Clear();Check(sessions.Count==0,"版本切换后全部会话可清空");
                Load(b,false,"reload-after-clear");Check(sessions.ModelLoads==6,"清空后真实重载一次");
            }
            else
            {
                void Inspect(WorkflowExecutionRun run,bool normal,bool expectWarm)
                {
                    Check(run.Status==WorkflowRunStatus.Success,string.Join(";",run.Errors));
                    Check(run.Inspection?.Verdict==(normal?InspectionVerdict.Ok:InspectionVerdict.Ng),"真实样本统一产品判定");
                    Check(run.Inspection!.Tasks.Count==6,"六个必检任务均有本轮结论");
                    foreach(var (node,index) in nodes.Select((n,i)=>(n,i)))
                    {
                        var row=Row(run,node); var expected=normal||index is 1 or 2;
                        Check(row.RoiId==regions[index].Id&&row.RoiName==regions[index].Name&&row.ExpectedClass==row.RoiName,"节点绑定命名ROI及期望类别正确");
                        Check(row.Decision.Code==(expected?ClassificationDecision.Ok:ClassificationDecision.NotDetected)&&row.Objects.Count==(expected?1:0),"逐区域判定符合已核对标签");
                        var frame=run.SpatialOutputs![corrected.Id]["image"]; var previewFrame=run.SpatialOutputs![node.Id]["preview"];
                        Check(frame.Corrected&&frame.SpaceId==previewFrame.SpaceId&&frame.FrameId==previewFrame.FrameId,"检测保持矫正图的同帧坐标空间");
                        Check(row.Objects.All(o=>InBounds(o.Bounds,row.Bounds)),"检测框位于对应原尺寸ROI");
                        if(expectWarm||index>0)Check(run.NodeResults.Single(n=>n.NodeId==node.Id).Outputs["modelInfo"].GetProperty("cacheHit").GetBoolean(),"同模型六分支共享缓存命中");
                    }
                    Check(sessions.Count==1,"六节点只保留一个DL模型会话");
                }
                var positiveRun=Execute(flow,positive);try{Inspect(positiveRun,true,false);CheckDetectionPreviews(positiveRun);Record(positiveRun,"positive-cold");}finally{Release(positiveRun);}
                var negativeRun=Execute(flow,negative);try{Inspect(negativeRun,false,true);CheckDetectionPreviews(negativeRun);Record(negativeRun,"negative-warm");}finally{Release(negativeRun);}
                var threshold=flow with{Nodes=flow.Nodes.Select(n=>n.Id==nodes[0].Id?n with{Parameters=new(n.Parameters){["minimumConfidence"]=J(1d)}}:n).ToList()};
                var thresholdRun=Execute(threshold,positive);try{Check(thresholdRun.Inspection?.Verdict==InspectionVerdict.Ng&&Row(thresholdRun,nodes[0]).Objects.Count==0,"P1独立阈值1导致NG");Check(nodes.Skip(1).All(n=>Row(thresholdRun,n).Decision.Code==ClassificationDecision.Ok),"共享会话每节点重设阈值，不污染P2–P6");Record(thresholdRun,"threshold-isolation");}finally{Release(thresholdRun);}
                var reference=corrected.Parameters["reference"].Deserialize<PositionReference>()!;
                var blankPath=Path.Combine(output,"blank.png");using(var blank=new HImage()){blank.GenImageConst("byte",reference.Width,reference.Height);blank.WriteImage("png",0,blankPath);}
                var missing=Execute(flow,blankPath);try{Check(missing.Inspection?.Verdict==InspectionVerdict.Error&&missing.NodeResults.Single(n=>n.NodeId==corrected.Id).Error!.Contains("POSE_NOT_FOUND"),"无可信定位时产品ERROR");Check(nodes.All(n=>missing.NodeResults.Single(r=>r.NodeId==n.Id).Status==NodeExecutionStatus.Skipped&&!missing.RuntimeOutputs!.ContainsKey(n.Id)),"六分支跳过且无旧检测输出");Record(missing,"pose-error");}finally{Release(missing);}
                using(var cancel=new CancellationTokenSource())
                {
                    var interrupted=Execute(flow,positive,cancel.Token,new InlineProgress(r=>{if(r.NodeId==nodes[0].Id&&r.Status==NodeExecutionStatus.Success)cancel.Cancel();}));
                    try{Check(interrupted.Status==WorkflowRunStatus.Cancelled&&interrupted.Inspection?.Verdict==InspectionVerdict.Error,"完成首任务后停止不能保留产品OK");Check(nodes.Skip(1).All(n=>!interrupted.RuntimeOutputs!.ContainsKey(n.Id)),"停止后不发布后续任务旧输出");Record(interrupted,"cancel-mid-flow");}finally{Release(interrupted);}
                }
                var memory=new List<object>(); var process=Process.GetCurrentProcess();
                for(var round=0;round<30;round++)
                {
                    var run=Execute(flow,round%2==0?positive:negative);try{Inspect(run,round%2==0,true);}finally{Release(run);}
                    if(round%5==4){process.Refresh();memory.Add(new{round=round+1,privateBytes=process.PrivateMemorySize64,workingSet=process.WorkingSet64,sessions=sessions.Count});Console.WriteLine("生命周期 "+(round+1)+"/30");}
                }
                File.WriteAllText(Path.Combine(output,"memory.json"),JsonSerializer.Serialize(memory,Json));
                var invalid=Only(1) with{Nodes=Only(1).Nodes.Select(n=>n.Id==nodes[0].Id?n with{Parameters=new(n.Parameters){["expectedClass"]=J("__INVALID_CLASS__")}}:n).ToList()};
                var failed=Execute(invalid,positive);try{Check(failed.Inspection?.Verdict==InspectionVerdict.Error&&!failed.RuntimeOutputs!.ContainsKey(nodes[0].Id)&&sessions.Count==0,"推理配置异常无旧输出并淘汰共享会话");Record(failed,"inference-error");}finally{Release(failed);}
                var recovery=Execute(flow,positive);try{Inspect(recovery,true,false);Record(recovery,"recovery-reload");}finally{Release(recovery);}
                CopyAssets(original.RootPath,output,flow.AssetReferences);
                var projectPath=Path.Combine(output,"1111_P1-P6独立任务.hvproj");
                store.SaveProject(new(output,original.Project with{Id=Guid.NewGuid(),Name="1111 · P1–P6 独立任务"},projectPath),[flow]);
                var reopened=store.OpenProject(projectPath);
                Check(reopened.Project.SchemaVersion==2&&InspectionPlan.ConfigurationIdentity(reopened.Project.Flows.Single())==configurationIdentity,"保存重开保持全部任务和配置身份");
                var restored=Execute(reopened.Project.Flows.Single(),positive);try{Inspect(restored,true,true);Record(restored,"reopened");}finally{Release(restored);}
                var copied=store.CopyFlow(reopened,flow,"复制检查");
                Check(validator.Validate(copied).Count==0&&copied.InspectionTasks.All(t=>t.NodeIds.All(id=>copied.Nodes.Any(n=>n.Id==id))),"复制后六任务引用重映射");
                var exported=store.ExportFlow(flow,Path.Combine(output,"tasks.flow.json"));
                var imported=store.ImportFlow(reopened,exported);
                var importedRun=Execute(imported,positive);
                try{Check(importedRun.Inspection?.Verdict==InspectionVerdict.Ok&&importedRun.NodeResults.Count==9,"导入后九节点六任务真实执行仍OK："+string.Join(";",importedRun.Errors));}
                finally{Release(importedRun);}
                Check(sessions.ModelLoads==2,"30轮及参数切换只初次加载、异常淘汰后重载各一次");
                VerifyWorkspace(projectPath,positive,negative,reader.Id,nodes[0].Id);
                Console.WriteLine("检查方案："+projectPath);
            }
            shapes.Clear();sessions.Clear();Check(shapes.Count==0&&sessions.Count==0,"检查结束释放形状与DL缓存");
            Check(readOnly.All(p=>Hash(p.Key)==p.Value),"源项目、图片、模板、模型和预处理文件哈希未变");
            File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{checks=Checks.Count,passed=Checks,runs=Runs,readOnlyHashes=readOnly,note="标签同源正负样本联调，不是独立精度报告；现有模型不含NG类别。"},Json));
            Console.WriteLine("PASS "+Checks.Count+" 项 P1–P6 独立任务检查");return 0;
        }
        catch(Exception error)
        {
            File.WriteAllText(Path.Combine(output,"error.txt"),error.ToString());
            File.WriteAllText(Path.Combine(output,"failed-summary.json"),JsonSerializer.Serialize(new{checks=Checks,runs=Runs,error=error.ToString()},Json));
            Console.Error.WriteLine(error);return 1;
        }
    }
    private static string Hash(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
    private static string PreviewHash(HImage image)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);using var channels=image.CountChannels();
        for(var i=1;i<=channels.I;i++)
        {
            using var plane=image.AccessChannel(i);var pointer=plane.GetImagePointer1(out string type,out int width,out int height);
            if(type!="byte")throw new InvalidDataException("本轮RGB预览对照只接受实际byte输入。");
            var data=new byte[width*height];Marshal.Copy(pointer,data,0,data.Length);hash.AppendData(data);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static void VerifyWorkspace(string project,string positive,string negative,Guid reader,Guid firstDetector)
    {
        var workspace=new WorkflowWorkspaceViewModel();
        var sessions=(DlModelSessionCache)typeof(WorkflowWorkspaceViewModel).GetField("_dlSessions",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(workspace)!;
        FlowDocument Prepared(string image)=>workspace.SelectedFlow!.Document with{Nodes=workspace.SelectedFlow.Document.Nodes.Select(n=>n.Id==reader?n with{Parameters=new(n.Parameters){["path"]=J(image)}}:n).ToList()};
        HObject[] Native(WorkflowExecutionRun run)=>run.RuntimeOutputs?.Values.SelectMany(v=>v.Values).OfType<HObject>().ToArray()??[];
        try
        {
            workspace.OpenProject(project);
            var good=workspace.RunSelectedFlowAsync(flowOverride:Prepared(positive)).GetAwaiter().GetResult();workspace.SetExecutionRun(good);var previous=Native(good);
            Check(workspace.ProductVerdictText.StartsWith("产品：OK")&&sessions.Count==1,"实际工作区六节点显示产品OK且仅一个会话");
            var bad=workspace.RunSelectedFlowAsync(flowOverride:Prepared(negative)).GetAwaiter().GetResult();workspace.SetExecutionRun(bad);
            Check(workspace.ProductVerdictText.StartsWith("产品：NG")&&previous.All(o=>!o.IsInitialized()),"工作区正负切换释放旧结果并显示NG");
            workspace.ReleaseModelSessions();Check(sessions.Count==0&&ReferenceEquals(workspace.LastRun,bad),"停止释放模型，同时保留末轮结果供查看");
            var restart=workspace.RunSelectedFlowAsync(flowOverride:Prepared(positive)).GetAwaiter().GetResult();workspace.SetExecutionRun(restart);previous=Native(restart);
            Check(restart.Inspection?.Verdict==InspectionVerdict.Ok&&sessions.Count==1,"停止后重新启动恢复模型与产品OK");
            workspace.SaveAll();workspace.OpenProject(project);
            Check(workspace.LastRun is null&&sessions.Count==0&&previous.All(o=>!o.IsInitialized()),"保存重开及切换项目清除模型与旧输出");
            using var reached=new ManualResetEventSlim();using var resume=new ManualResetEventSlim();
            var active=workspace.RunSelectedFlowAsync(new InlineProgress(r=>{if(r.NodeId==firstDetector&&r.Status==NodeExecutionStatus.Running){reached.Set();if(!resume.Wait(TimeSpan.FromSeconds(30)))throw new TimeoutException("关闭检查未恢复执行。");}}),Prepared(positive));
            Check(reached.Wait(TimeSpan.FromSeconds(30)),"运行中关闭检查到达首检测任务");
            var closing=workspace.ShutdownAsync();resume.Set();closing.GetAwaiter().GetResult();var stopped=active.GetAwaiter().GetResult();
            Check(stopped.Inspection?.Verdict==InspectionVerdict.Error&&Native(stopped).All(o=>!o.IsInitialized())&&sessions.Count==0&&workspace.LastRun is null,"运行中关闭等待退出、产品ERROR且资源清空");
            var rejected=false;try{workspace.OpenProject(project);}catch(ObjectDisposedException){rejected=true;}Check(rejected,"关闭的工作区拒绝再次获取资源");
        }
        finally{workspace.ShutdownAsync().GetAwaiter().GetResult();}
    }
    private static bool InBounds(Rectangle2D box,Rectangle2D roi)=>box.RowBegin>=roi.RowBegin-.51&&box.ColumnBegin>=roi.ColumnBegin-.51&&box.RowEnd<=roi.RowEnd+.51&&box.ColumnEnd<=roi.ColumnEnd+.51;
    private static void CopyAssets(string source,string destination,IEnumerable<string> assets)
    {
        foreach(var asset in assets){var origin=Path.GetFullPath(Path.Combine(source,asset));var target=Path.GetFullPath(Path.Combine(destination,asset));
            if(!origin.StartsWith(Path.GetFullPath(source)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!target.StartsWith(destination+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("资源路径越界。");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(origin,target);}
    }
    private sealed class InlineProgress(Action<NodeExecutionResult> action):IProgress<NodeExecutionResult>{public void Report(NodeExecutionResult value)=>action(value);}
}
