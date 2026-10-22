using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HalconDotNet;
using HalconVision.Halcon;

internal static class Program
{
    private static readonly List<string> Checks = [];
    private static readonly List<object> Metrics = [];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Type Builder = typeof(DlModelSessionCache).Assembly.GetType("HalconVision.Halcon.OwnedPreviewImage", true)!;
    private static readonly MethodInfo Paint = Builder.GetMethod("PaintMargin")!;
    private static readonly MethodInfo Take = Builder.GetMethod("TakeImage")!;
    private static readonly int[][] Colors = [[50,205,50],[255,0,0],[30,144,255],[255,220,0]];
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); Checks.Add(message); }

    private static int Main(string[] args)
    {
        if (args.Length is < 2 or > 3 || args.Length == 3 && args[2] != "--performance") return 2;
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output) || File.Exists(output)) { Console.Error.WriteLine("拒绝覆盖已有输出目录。"); return 2; }
        Directory.CreateDirectory(output);
        var originalHash = FileHash(args[0]);
        try
        {
            using var original = new HImage(args[0]); using var count = original.CountChannels();
            Check(count.I == 3, "本轮真实原图为RGB");
            using var small = original.CropRectangle1(0,0,127,127); using var gray = small.Rgb1ToGray();
            using var aliased = gray.Compose3(gray, gray); using var real = gray.ConvertImageType("real");
            using var sixteen = gray.ConvertImageType("uint2");
            using var domain = new HRegion(12d,16d,111d,100d); using var reduced = small.ReduceDomain(domain);
            using (var legacy = Reference(gray,[new(20,20,80,80,Colors[0])]))
            using (var current = Render(gray,[new(20,20,80,80,Colors[0])]))
            using (var oldPixel = legacy.GetGrayval(20,20))
            using (var newPixel = current.GetGrayval(20,20))
            {
                Metrics.Add(new { diagnostic="gray-green-margin", legacy=oldPixel.ToDArr(), current=newPixel.ToDArr() });
                Console.WriteLine("灰度输入绿色边界：旧="+string.Join(",",oldPixel.ToDArr())+"，当前="+string.Join(",",newPixel.ToDArr()));
                Check(newPixel.ToDArr().SequenceEqual(new double[]{50,205,50}),"灰度输入的绿色框必须保留RGB三通道颜色");
            }
            var cases = new[] { ("rgb",small),("gray",gray),("aliased-rgb",aliased),("real",real),("uint2",sixteen),("reduced-domain",reduced) };
            foreach (var (name,source) in cases)
            {
                var before = Fingerprint(source); var marks = Marks(128,128,64);
                using var expected = Reference(source, marks, independentChannels:true); using var actual = Render(source, marks);
                Check(Fingerprint(actual) == Fingerprint(expected), name+"：64个重叠/越界/四色边界的全部像素与独立通道PaintRegion对照一致");
                if(name is "rgb" or "reduced-domain")
                {
                    using var legacy=Reference(source,marks);
                    Check(Fingerprint(actual)==Fingerprint(legacy),name+"：普通RGB保持旧路径逐像素结果");
                }
                foreach(var color in Colors)
                {
                    using var colorResult=Render(source,[new(20,20,80,80,color)]);using var pixel=colorResult.GetGrayval(20,20);
                    Check(pixel.ToDArr().SequenceEqual(color.Select(v=>(double)v)),name+"：独立四色边界值正确");
                }
                Check(Fingerprint(source) == before, name+"：生成预览不修改上游图像或域");
                using var empty = Render(source, []);
                using var emptyReference = ReferenceOwned(source);
                Check(Fingerprint(empty) == Fingerprint(emptyReference), name+"：无标注仍保留原像素和域");
                using var black = new HRegion(0d,0d,127d,127d);using var red = new HTuple(new[]{255,0,0});
                empty.OverpaintRegion(black,red,"fill");
                Check(Fingerprint(source)==before, name+"：转移后的空预览也不与上游共享像素");
                Check(Fingerprint(actual)==Fingerprint(expected), name+"：另一份预览的修改不会污染已有结果");
            }
            using(var ownedSource=small.CopyImage())
            {
                var owner=Create(ownedSource,CancellationToken.None); ownedSource.Dispose();
                using var image=(HImage)Take.Invoke(owner,null)!;
                ((IDisposable)owner).Dispose();((IDisposable)owner).Dispose();
                Check(image.IsInitialized()&&Fingerprint(image)==Fingerprint(small),"输入先释放、所有者重复释放不影响已转移预览");
                var rejected=false;try{Take.Invoke(owner,null);}catch(TargetInvocationException ex)when(ex.InnerException is ObjectDisposedException){rejected=true;}
                Check(rejected,"转移后拒绝重复取走输出");
            }
            {
                var owner=Create(small,CancellationToken.None);
                var native=(HImage)Builder.GetField("_image",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(owner)!;
                ((IDisposable)owner).Dispose();((IDisposable)owner).Dispose();
                Check(!native.IsInitialized()&&small.IsInitialized(),"未转移的预览在所有者关闭时真正释放，上游仍有效");
            }
            using(var cancellation=new CancellationTokenSource())
            {
                var before=Fingerprint(small);var owner=Create(small,cancellation.Token);
                try
                {
                    using var region=new HRegion(1d,1d,20d,20d);using var color=new HTuple(Colors[0]);
                    Paint.Invoke(owner,[region,color]);cancellation.Cancel();
                    var rejected=false;try{Paint.Invoke(owner,[region,color]);}catch(TargetInvocationException ex)when(ex.InnerException is OperationCanceledException){rejected=true;}
                    Check(rejected&&Fingerprint(small)==before,"绘制阶段取消不修改上游");
                    rejected=false;try{Take.Invoke(owner,null);}catch(TargetInvocationException ex)when(ex.InnerException is OperationCanceledException){rejected=true;}
                    Check(rejected,"取消后不发布半幅预览");
                }
                finally{((IDisposable)owner).Dispose();}
            }
            var wasCancelled=false;try{Create(small,new CancellationToken(true));}catch(TargetInvocationException ex)when(ex.InnerException is OperationCanceledException){wasCancelled=true;}
            Check(wasCancelled,"创建前取消不分配预览");
            using(var r1=new HRegion(5d,5d,50d,50d))using(var r2=new HRegion(60d,60d,120d,120d))using(var regions=r1.ConcatObj(r2))using(var color=new HTuple(Colors[0]))
            {
                var owner=Create(small,CancellationToken.None);
                try
                {
                    Paint.Invoke(owner,[regions,color]);using var actual=(HImage)Take.Invoke(owner,null)!;
                    using var expected=small.PaintRegion(regions,color,"margin");
                    Check(Fingerprint(actual)==Fingerprint(expected),"分割多个区域对象一次绘制与PaintRegion一致");
                }
                finally{((IDisposable)owner).Dispose();}
            }
            foreach(var labelCount in new[]{2,64})
            {
                original.GetImageSize(out int w,out int h);var marks=Marks(w,h,labelCount);
                // Warm both paths, then alternate so the same process and HALCON caches are used.
                using(var a=Reference(original,marks))using(var b=Render(original,marks))Check(Fingerprint(a)==Fingerprint(b),"真实尺寸预览逐像素一致："+labelCount);
                var baseline=new List<double>();var production=new List<double>();
                for(var i=0;i<10;i++)
                {
                    var timer=Stopwatch.StartNew();using(var a=Reference(original,marks)){}timer.Stop();if(i>=2)baseline.Add(timer.Elapsed.TotalMilliseconds);
                    timer.Restart();using(var b=Render(original,marks)){}timer.Stop();if(i>=2)production.Add(timer.Elapsed.TotalMilliseconds);
                }
                var oldMedian=Median(baseline);var newMedian=Median(production);
                Metrics.Add(new{labels=labelCount,width=w,height=h,referenceMedianMs=oldMedian,productionMedianMs=newMedian,baseline,production});
                Console.WriteLine($"{labelCount}个标注：旧路径 {oldMedian:F3} ms，产品路径 {newMedian:F3} ms");
                Save(output,originalHash);
                if(args.Length==3&&labelCount==64)Check(newMedian<oldMedian*.5,"64标注预览耗时低于旧路径一半");
            }
            Check(FileHash(args[0])==originalHash,"原始图片文件哈希不变");Save(output,originalHash);
            Console.WriteLine("PASS "+Checks.Count+" 项真实HALCON预览检查");return 0;
        }
        catch(Exception error)
        {
            Save(output,originalHash);File.WriteAllText(Path.Combine(output,"error.txt"),error.ToString());Console.Error.WriteLine(error);return 1;
        }
    }

    private static object Create(HImage source,CancellationToken token)=>Activator.CreateInstance(Builder,[source,token])!;
    private static HImage Render(HImage source,IReadOnlyList<Mark> marks)
    {
        var owner=Create(source,CancellationToken.None);
        try
        {
            foreach(var mark in marks){using var region=new HRegion(mark.R1,mark.C1,mark.R2,mark.C2);using var color=new HTuple(mark.Color);Paint.Invoke(owner,[region,color]);}
            return (HImage)Take.Invoke(owner,null)!;
        }
        finally{((IDisposable)owner).Dispose();}
    }
    private static HImage ReferenceOwned(HImage source)
    {
        using var channels=source.CountChannels();
        return channels.I==3?source.CopyImage():source.Compose3(source,source);
    }
    private static HImage Reference(HImage source,IReadOnlyList<Mark> marks,bool independentChannels=false)
    {
        HImage preview;
        if(independentChannels)
        {
            using var channels=source.CountChannels();
            using var r=source.AccessChannel(1);using var g=source.AccessChannel(channels.I==1?1:2);using var b=source.AccessChannel(channels.I==1?1:3);
            using var rr=r.CopyImage();using var gg=g.CopyImage();using var bb=b.CopyImage();preview=rr.Compose3(gg,bb);
        }
        else preview=ReferenceOwned(source);
        try
        {
            foreach(var mark in marks)
            {
                using var region=new HRegion(mark.R1,mark.C1,mark.R2,mark.C2);using var color=new HTuple(mark.Color);
                var next=preview.PaintRegion(region,color,"margin");preview.Dispose();preview=next;
            }
            return preview;
        }
        catch{preview.Dispose();throw;}
    }
    private static Mark[] Marks(int w,int h,int count)=>Enumerable.Range(0,count).Select(i=>
        new Mark((i%8)*h/10d-4,(i/8)*w/10d-3,(i%8)*h/10d+h/4d,(i/8)*w/10d+w/4d,Colors[i%4])).ToArray();
    private sealed record Mark(double R1,double C1,double R2,double C2,int[] Color);
    private static string Fingerprint(HImage image)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);using var channels=image.CountChannels();
        for(var channel=1;channel<=channels.I;channel++)
        {
            using var plane=image.AccessChannel(channel);var pointer=plane.GetImagePointer1(out string type,out int w,out int h);
            var size=type switch{"byte"=>1,"uint2"=>2,"real"=>4,_=>throw new InvalidDataException(type)};
            hash.AppendData(Encoding.UTF8.GetBytes($"{w}:{h}:{type}:"));var bytes=new byte[checked(w*h*size)];Marshal.Copy(pointer,bytes,0,bytes.Length);hash.AppendData(bytes);
        }
        using var domain=image.GetDomain();HOperatorSet.GetRegionRuns(domain,out var rows,out var left,out var right);
        using(rows)using(left)using(right)
            foreach(var tuple in new[]{rows,left,right}){var data=tuple.ToIArr();var bytes=new byte[data.Length*4];Buffer.BlockCopy(data,0,bytes,0,bytes.Length);hash.AppendData(bytes);}
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static double Median(List<double> values){var ordered=values.Order().ToArray();return (ordered[ordered.Length/2-1]+ordered[ordered.Length/2])*.5;}
    private static string FileHash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file));}
    private static void Save(string directory,string inputHash)=>File.WriteAllText(Path.Combine(directory,"summary.json"),JsonSerializer.Serialize(new{checks=Checks,metrics=Metrics,inputHash},Json));
}
