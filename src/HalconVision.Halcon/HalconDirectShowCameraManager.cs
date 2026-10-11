using HalconDotNet;

namespace HalconVision.Halcon;

public sealed record HalconCameraDevice(string Id, string Name);

/// <summary>Owns one HALCON DirectShow connection and serializes enumeration, connection, and grabbing.</summary>
public sealed class HalconDirectShowCameraManager : IDisposable
{
    private readonly object _sync = new();
    private HFramegrabber? _framegrabber;
    private string? _connectedDeviceId;

    public string? ConnectedDeviceId
    {
        get { lock (_sync) return _connectedDeviceId; }
    }

    public IReadOnlyList<HalconCameraDevice> Scan()
    {
        lock (_sync)
        {
            HOperatorSet.InfoFramegrabber("DirectShow", "device", out HTuple information, out HTuple devices);
            try
            {
                return devices.SArr.Select(value =>
                {
                    var closeBracket = value.IndexOf(']');
                    var id = value.StartsWith('[') && closeBracket > 1 ? value[1..closeBracket] : value;
                    var name = value.StartsWith('[') && closeBracket >= 0 ? value[(closeBracket + 1)..].Trim() : value;
                    return new HalconCameraDevice(id, name);
                }).ToArray();
            }
            finally { information.Dispose(); devices.Dispose(); }
        }
    }

    public void Connect(string deviceId)
    {
        lock (_sync)
        {
            if (_framegrabber is not null && string.Equals(_connectedDeviceId, deviceId, StringComparison.Ordinal)) return;
            DisconnectCore();
            _framegrabber = new HFramegrabber(
                "DirectShow", 1, 1, 0, 0, 0, 0, "default", 8, "rgb", -1,
                "false", "default", deviceId, -1, 1);
            _connectedDeviceId = deviceId;
        }
    }

    public HImage GrabFrame(string deviceId)
    {
        lock (_sync)
        {
            if (_framegrabber is null || !string.Equals(_connectedDeviceId, deviceId, StringComparison.Ordinal))
                throw new InvalidOperationException("相机尚未连接或所选设备与已连接设备不一致，请先在相机连接面板连接设备。");
            return _framegrabber.GrabImage();
        }
    }

    public void Disconnect()
    {
        lock (_sync) DisconnectCore();
    }

    private void DisconnectCore()
    {
        _framegrabber?.Dispose();
        _framegrabber = null;
        _connectedDeviceId = null;
    }

    public void Dispose() => Disconnect();
}
