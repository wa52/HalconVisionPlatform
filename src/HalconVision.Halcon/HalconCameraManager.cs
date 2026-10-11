using HalconDotNet;

namespace HalconVision.Halcon;

public sealed record HalconAcquisitionInterface(string Id, string DisplayName);
public sealed record HalconCameraDevice(string InterfaceId, string Id, string Name, string DeviceParameter);

/// <summary>Discovers HALCON acquisition interfaces and owns one connected framegrabber.</summary>
public sealed class HalconCameraManager : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> InterfaceNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["DirectShow"] = "DirectShow · Windows 摄像头",
        ["MediaFoundation"] = "MediaFoundation · Windows 媒体设备",
        ["GigEVision2"] = "GigEVision2 · GigE 工业相机",
        ["USB3Vision"] = "USB3Vision · USB3 工业相机",
        ["GenICamTL"] = "GenICamTL · GenICam 相机",
        ["RealSense"] = "RealSense · Intel RealSense"
    };

    private readonly object _sync = new();
    private HFramegrabber? _framegrabber;
    private string? _connectedInterfaceId;
    private string? _connectedDeviceId;

    public string? ConnectedInterfaceId { get { lock (_sync) return _connectedInterfaceId; } }
    public string? ConnectedDeviceId { get { lock (_sync) return _connectedDeviceId; } }

    public IReadOnlyList<HalconAcquisitionInterface> GetInstalledInterfaces()
    {
        var root = Environment.GetEnvironmentVariable("HALCONROOT");
        var arch = Environment.GetEnvironmentVariable("HALCONARCH");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(arch))
            throw new InvalidOperationException("未设置 HALCONROOT 或 HALCONARCH，无法读取 HALCON 采集接口目录。");

        var bin = Path.Combine(root, "bin", arch);
        if (!Directory.Exists(bin)) throw new DirectoryNotFoundException($"HALCON 采集接口目录不存在：{bin}");
        return Directory.EnumerateFiles(bin, "hAcq*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null && !name.EndsWith("xl", StringComparison.OrdinalIgnoreCase))
            .Select(name => name![4..])
            .Where(InterfaceNames.ContainsKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(IsInterfaceAvailable)
            .OrderBy(name => name == "DirectShow" ? 0 : name == "MediaFoundation" ? 1 : 2)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new HalconAcquisitionInterface(name, InterfaceNames[name]))
            .ToArray();
    }

    public IReadOnlyList<HalconCameraDevice> Scan(string interfaceId)
    {
        lock (_sync)
        {
            HOperatorSet.InfoFramegrabber(interfaceId, "device", out HTuple information, out HTuple devices);
            try
            {
                if (devices.Length == 0) return [];
                return devices.SArr.Select(value =>
                {
                    var closeBracket = value.IndexOf(']');
                    var id = value.StartsWith('[') && closeBracket > 1 ? value[1..closeBracket] :
                        ReadDeviceField(value, "unique_name") ?? ReadDeviceField(value, "device") ?? value;
                    var name = value.StartsWith('[') && closeBracket >= 0 ? value[(closeBracket + 1)..].Trim() :
                        ReadDeviceField(value, "friendly_name") ?? ReadDeviceField(value, "model") ?? ReadDeviceField(value, "device") ?? value.Trim();
                    var parameter = interfaceId == "DirectShow" ? id : value;
                    return new HalconCameraDevice(interfaceId, id, name, parameter);
                }).ToArray();
            }
            finally { information.Dispose(); devices.Dispose(); }
        }
    }

    private static bool IsInterfaceAvailable(string interfaceId)
    {
        HTuple? information = null;
        HTuple? values = null;
        try
        {
            HOperatorSet.InfoFramegrabber(interfaceId, "general", out information, out values);
            return true;
        }
        catch (HalconException) { return false; }
        finally { information?.Dispose(); values?.Dispose(); }
    }

    public HImage ConnectAndGrabPreview(string interfaceId, string deviceId, string deviceParameter)
    {
        lock (_sync)
        {
            if (_framegrabber is null || _connectedInterfaceId != interfaceId || _connectedDeviceId != deviceId)
            {
                DisconnectCore();
                _framegrabber = Open(interfaceId, deviceParameter);
                _connectedInterfaceId = interfaceId;
                _connectedDeviceId = deviceId;
            }

            try { return _framegrabber.GrabImage(); }
            catch
            {
                DisconnectCore();
                throw;
            }
        }
    }

    public HImage GrabFrame(string interfaceId, string deviceId)
    {
        lock (_sync)
        {
            if (_framegrabber is null || _connectedInterfaceId != interfaceId || _connectedDeviceId != deviceId)
                throw new InvalidOperationException("相机尚未连接，或当前流程的相机接口/设备与已连接设备不一致。请在相机面板选择并连接对应设备。");
            return _framegrabber.GrabImage();
        }
    }

    private static HFramegrabber Open(string interfaceId, string deviceParameter)
    {
        return interfaceId switch
        {
            "DirectShow" => new HFramegrabber(interfaceId, 1, 1, 0, 0, 0, 0, "default", 8, "rgb", -1,
                "false", "default", deviceParameter, -1, 1),
            "MediaFoundation" => new HFramegrabber(interfaceId, 0, 0, 0, 0, 0, 0, "progressive", -1, "default", -1,
                "false", "default", deviceParameter, 0, -1),
            "GigEVision2" => new HFramegrabber(interfaceId, 1, 1, 0, 0, 0, 0, "default", -1, "default", -1,
                "false", "default", deviceParameter, -1, -1),
            "USB3Vision" => new HFramegrabber(interfaceId, 0, 0, 0, 0, 0, 0, "default", -1, "default", -1,
                "false", "default", deviceParameter, 0, -1),
            "GenICamTL" => new HFramegrabber(interfaceId, 1, 1, 0, 0, 0, 0, "default", -1, "default", -1,
                "false", "default", deviceParameter, 0, -1),
            "RealSense" => new HFramegrabber(interfaceId, 1, 1, 0, 0, 0, 0, "default", -1, "default", -1,
                "default", "default", deviceParameter, -1, -1),
            _ => throw new NotSupportedException($"当前相机类别尚未配置 HALCON 采集参数：{interfaceId}")
        };
    }

    private static string? ReadDeviceField(string source, string key)
    {
        foreach (var part in source.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf(':');
            if (separator > 0 && string.Equals(part[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase))
                return part[(separator + 1)..].Trim();
        }
        return null;
    }

    public void Disconnect()
    {
        lock (_sync) DisconnectCore();
    }

    private void DisconnectCore()
    {
        _framegrabber?.Dispose();
        _framegrabber = null;
        _connectedInterfaceId = null;
        _connectedDeviceId = null;
    }

    public void Dispose() => Disconnect();
}
