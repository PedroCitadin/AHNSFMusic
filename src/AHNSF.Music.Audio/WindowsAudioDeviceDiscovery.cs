using AHNSF.Music.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AHNSF.Music.Audio;

/// <summary>Windows endpoint notifications plus snapshots. No mixer/default-device writes.</summary>
public sealed class WindowsAudioDeviceDiscovery : IAudioDeviceDiscovery, IMMNotificationClient
{
    private readonly object gate = new();
    private readonly MMDeviceEnumerator enumerator = new();
    private bool disposed;
    public event EventHandler? Changed;
    public WindowsAudioDeviceDiscovery() => enumerator.RegisterEndpointNotificationCallback(this);
    public IReadOnlyList<AudioOutputDevice> Enumerate()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            string? defaultId = null;
            if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            { using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); defaultId = device.ID; }
            var devices = new List<AudioOutputDevice>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All))
            {
                using (device)
                {
                    var id = device.ID;
                    devices.Add(ReadEndpoint(id, () => device.FriendlyName, device.State == DeviceState.Active, id == defaultId));
                }
            }
            return devices;
        }
    }
    // Removed endpoints can retain IDs while their property store raises E000020B.
    public static AudioOutputDevice ReadEndpoint(string id, Func<string> readName, bool available, bool isDefault)
    {
        string name;
        try { name = readName(); }
        catch (System.Runtime.InteropServices.COMException) { name = "Dispositivo sem nome disponível (" + id + ")"; }
        return new(id, name, available, isDefault);
    }
    private void Signal() { if (!disposed) Changed?.Invoke(this, EventArgs.Empty); }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Signal();
    public void OnDeviceAdded(string deviceId) => Signal();
    public void OnDeviceRemoved(string deviceId) => Signal();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string deviceId) { if (flow == DataFlow.Render && role == Role.Multimedia) Signal(); }
    public void OnPropertyValueChanged(string deviceId, PropertyKey key) => Signal();
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            try { enumerator.UnregisterEndpointNotificationCallback(this); } finally { enumerator.Dispose(); }
        }
    }
}
