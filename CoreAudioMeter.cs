using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AudioPttLatch;

/// <summary>
/// Thin wrapper around Windows Core Audio endpoint metering.
/// It enumerates capture/render devices and reads a simple peak level from the selected endpoint.
/// </summary>
public sealed class CoreAudioMeter : IDisposable
{
    private const int DeviceStateActive = 0x00000001;
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");

    // Active COM interfaces for the currently opened endpoint.
    private IAudioMeterInformation? _meter;
    private IMMDevice? _device;

    /// <summary>
    /// Returns all active input or output endpoints available to the current user session.
    /// </summary>
    public static IReadOnlyList<AudioEndpoint> Enumerate(AudioDeviceKind kind)
    {
        var endpoints = EnumerateFromRegistry(kind);
        if (endpoints.Count > 0)
        {
            return endpoints;
        }

        // Registry metadata should normally be available. Keep the COM path as a
        // fallback for unusual installs where the registry view is not readable.
        return EnumerateFromCoreAudio(kind);
    }

    /// <summary>
    /// Reads endpoint IDs and display names from the Windows MMDevices registry keys.
    /// </summary>
    private static IReadOnlyList<AudioEndpoint> EnumerateFromRegistry(AudioDeviceKind kind)
    {
        var results = new List<AudioEndpoint>();
        var flowKey = kind == AudioDeviceKind.Input ? "Capture" : "Render";
        var path = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\{flowKey}";

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var audioKey = baseKey.OpenSubKey(path);
        if (audioKey == null)
        {
            return results;
        }

        foreach (var endpointId in audioKey.GetSubKeyNames())
        {
            using var endpointKey = audioKey.OpenSubKey(endpointId);
            if (endpointKey == null || !IsRegistryEndpointActive(endpointKey))
            {
                continue;
            }

            using var propertiesKey = endpointKey.OpenSubKey("Properties");
            var displayName = BuildRegistryEndpointDisplayName(propertiesKey) ?? endpointId;
            results.Add(new AudioEndpoint(BuildCoreAudioEndpointId(kind, endpointId), displayName, kind));
        }

        return results
            .OrderBy(endpoint => endpoint.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Converts an MMDevices registry key name into the endpoint ID expected by IMMDeviceEnumerator.GetDevice.
    /// </summary>
    private static string BuildCoreAudioEndpointId(AudioDeviceKind kind, string registryEndpointId)
    {
        if (registryEndpointId.StartsWith("{0.0.", StringComparison.OrdinalIgnoreCase))
        {
            return registryEndpointId;
        }

        var flowPrefix = kind == AudioDeviceKind.Input ? "{0.0.1.00000000}" : "{0.0.0.00000000}";
        return $"{flowPrefix}.{registryEndpointId}";
    }

    /// <summary>
    /// Determines whether an MMDevices registry endpoint is currently active.
    /// </summary>
    private static bool IsRegistryEndpointActive(RegistryKey endpointKey)
    {
        return endpointKey.GetValue("DeviceState") is int state && state == DeviceStateActive;
    }

    /// <summary>
    /// Builds a descriptive endpoint label from MMDevices property values.
    /// </summary>
    private static string? BuildRegistryEndpointDisplayName(RegistryKey? propertiesKey)
    {
        if (propertiesKey == null)
        {
            return null;
        }

        var endpointName = ReadRegistryString(propertiesKey, "{a45c254e-df1c-4efd-8020-67d146a850e0},2");
        var parentDeviceName = ReadRegistryString(propertiesKey, "{b3f8fa53-0004-438e-9003-51a46e139bfc},6")
            ?? ReadRegistryString(propertiesKey, "{a45c254e-df1c-4efd-8020-67d146a850e0},14");

        if (!string.IsNullOrWhiteSpace(endpointName) &&
            !string.IsNullOrWhiteSpace(parentDeviceName) &&
            !string.Equals(endpointName, parentDeviceName, StringComparison.CurrentCultureIgnoreCase))
        {
            return $"{endpointName} ({parentDeviceName})";
        }

        return endpointName ?? parentDeviceName;
    }

    /// <summary>
    /// Reads and trims one registry string value.
    /// </summary>
    private static string? ReadRegistryString(RegistryKey propertiesKey, string valueName)
    {
        return propertiesKey.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    /// <summary>
    /// Returns all active input or output endpoints using the Core Audio enumerator.
    /// </summary>
    private static IReadOnlyList<AudioEndpoint> EnumerateFromCoreAudio(AudioDeviceKind kind)
    {
        var results = new List<AudioEndpoint>();
        var enumerator = CreateEnumerator();
        IMMDeviceCollection? collection = null;
        IntPtr collectionPointer = IntPtr.Zero;

        try
        {
            // Some machines/drivers are picky about automatic COM interface marshalling
            // here. Request the raw pointer first, then wrap it ourselves.
            ThrowIfFailed(enumerator.EnumAudioEndpoints(ToFlow(kind), DeviceStateActive, out collectionPointer));
            collection = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(collectionPointer);
            ThrowIfFailed(collection.GetCount(out var count));

            for (var i = 0; i < count; i++)
            {
                ThrowIfFailed(collection.Item(i, out var devicePointer));
                var device = (IMMDevice)Marshal.GetObjectForIUnknown(devicePointer);
                try
                {
                    ThrowIfFailed(device.GetId(out var id));
                    results.Add(new AudioEndpoint(id, ReadFriendlyName(device), kind));
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                    Marshal.Release(devicePointer);
                }
            }
        }
        finally
        {
            if (collection != null)
            {
                Marshal.ReleaseComObject(collection);
            }

            if (collectionPointer != IntPtr.Zero)
            {
                Marshal.Release(collectionPointer);
            }

            Marshal.ReleaseComObject(enumerator);
        }

        return results;
    }

    /// <summary>
    /// Gets the current Windows default endpoint ID for the requested input/output kind.
    /// </summary>
    public static string? GetDefaultDeviceId(AudioDeviceKind kind)
    {
        var enumerator = CreateEnumerator();
        try
        {
            ThrowIfFailed(enumerator.GetDefaultAudioEndpoint(ToFlow(kind), ERole.eConsole, out var device));
            try
            {
                ThrowIfFailed(device.GetId(out var id));
                return id;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// Opens the configured endpoint and activates IAudioMeterInformation for it.
    /// </summary>
    public void Open(AudioDeviceKind kind, string? deviceId)
    {
        // Only one endpoint is monitored at a time. Release the old COM objects
        // before activating the meter for the newly selected device.
        Dispose();

        var enumerator = CreateEnumerator();
        try
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                ThrowIfFailed(enumerator.GetDefaultAudioEndpoint(ToFlow(kind), ERole.eConsole, out _device));
            }
            else
            {
                ThrowIfFailed(enumerator.GetDevice(deviceId, out _device));
            }

            var meterId = IID_IAudioMeterInformation;
            // IAudioMeterInformation exposes a simple 0..1 peak level for render
            // and capture endpoints without starting our own audio stream.
            ThrowIfFailed(_device.Activate(ref meterId, CLSCTX.CLSCTX_ALL, IntPtr.Zero, out var meter));
            _meter = (IAudioMeterInformation)meter;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// Reads the endpoint's current peak level as a float from 0.0 to 1.0.
    /// </summary>
    public float GetPeakValue()
    {
        if (_meter == null)
        {
            return 0;
        }

        try
        {
            ThrowIfFailed(_meter.GetPeakValue(out var value));
            return value;
        }
        catch
        {
            // Audio devices can disappear while the app is running. Treat that as
            // silence; pressing Apply/Refresh can bind to another endpoint.
            return 0;
        }
    }

    /// <summary>
    /// Releases COM objects for the currently opened endpoint.
    /// </summary>
    public void Dispose()
    {
        if (_meter != null)
        {
            Marshal.ReleaseComObject(_meter);
            _meter = null;
        }

        if (_device != null)
        {
            Marshal.ReleaseComObject(_device);
            _device = null;
        }
    }

    /// <summary>
    /// Maps the app's input/output setting to the Core Audio data-flow enum.
    /// </summary>
    private static EDataFlow ToFlow(AudioDeviceKind kind) =>
        kind == AudioDeviceKind.Input ? EDataFlow.eCapture : EDataFlow.eRender;

    /// <summary>
    /// Instantiates Windows' MMDeviceEnumerator COM object.
    /// </summary>
    private static IMMDeviceEnumerator CreateEnumerator()
    {
        var type = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator, throwOnError: true)!;
        return (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
    }

    /// <summary>
    /// Converts Core Audio HRESULT failures into regular .NET exceptions at the call site.
    /// </summary>
    private static void ThrowIfFailed(int hresult)
    {
        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }
    }

    /// <summary>
    /// Reads the friendly display name for a Core Audio endpoint.
    /// </summary>
    private static string ReadFriendlyName(IMMDevice device)
    {
        ThrowIfFailed(device.OpenPropertyStore(STGM.STGM_READ, out var store));
        try
        {
            // PKEY_Device_FriendlyName is the same name Windows shows in Sound settings.
            var key = PropertyKeys.DeviceFriendlyName;
            ThrowIfFailed(store.GetValue(ref key, out var value));
            try
            {
                return value.ValueAsString() ?? "Unknown device";
            }
            finally
            {
                NativeMethods.PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    // These COM definitions are the small subset of Windows Core Audio needed
    // here, avoiding a third-party wrapper package.
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-C0C9303461D1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out int count);

        [PreserveSig]
        int Item(int index, out IntPtr device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, CLSCTX clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);

        [PreserveSig]
        int OpenPropertyStore(STGM access, out IPropertyStore properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out int propertyCount);

        [PreserveSig]
        int GetAt(int propertyIndex, out PROPERTYKEY key);

        [PreserveSig]
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);

        [PreserveSig]
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);

        [PreserveSig]
        int Commit();
    }

    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig]
        int GetPeakValue(out float peak);

        [PreserveSig]
        int GetMeteringChannelCount(out int channelCount);

        [PreserveSig]
        int GetChannelsPeakValues(int channelCount, [Out] float[] peakValues);

        [PreserveSig]
        int QueryHardwareSupport(out int hardwareSupportMask);
    }

    private enum EDataFlow
    {
        eRender,
        eCapture,
        eAll
    }

    private enum ERole
    {
        eConsole,
        eMultimedia,
        eCommunications
    }

    [Flags]
    private enum CLSCTX
    {
        CLSCTX_INPROC_SERVER = 0x1,
        CLSCTX_INPROC_HANDLER = 0x2,
        CLSCTX_LOCAL_SERVER = 0x4,
        CLSCTX_REMOTE_SERVER = 0x10,
        CLSCTX_ALL = CLSCTX_INPROC_SERVER | CLSCTX_INPROC_HANDLER | CLSCTX_LOCAL_SERVER | CLSCTX_REMOTE_SERVER
    }

    private enum STGM
    {
        STGM_READ = 0x00000000
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        private readonly ushort vt;
        private readonly ushort wReserved1;
        private readonly ushort wReserved2;
        private readonly ushort wReserved3;
        private readonly IntPtr p;
        private readonly int p2;

        public string? ValueAsString() => vt == 31 ? Marshal.PtrToStringUni(p) : null;
    }

    private static class PropertyKeys
    {
        public static PROPERTYKEY DeviceFriendlyName => new()
        {
            fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            pid = 14
        };
    }

    private static class NativeMethods
    {
        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PROPVARIANT pvar);
    }
}
