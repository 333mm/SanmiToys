using System;
using System.Runtime.InteropServices;

namespace SanmiToys.Modules.SwiftVolume.Core;

public static class PolicyConfig
{
    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    internal class _PolicyConfigClient
    {
    }

    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat(string pszDeviceName, bool bDefault, IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
        [PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode(string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode(string pszDeviceName, IntPtr pMode);
        [PreserveSig] int GetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint(string pszDeviceName, ERole eRole);
        [PreserveSig] int SetEndpointVisibility(string pszDeviceName, bool bVisible);
    }

    internal enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    public static void SetDefaultDevice(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return;
        try
        {
            var client = new _PolicyConfigClient() as IPolicyConfig;
            if (client != null)
            {
                client.SetDefaultEndpoint(deviceId, ERole.eConsole);
                client.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                client.SetDefaultEndpoint(deviceId, ERole.eCommunications);
            }
        }
        catch { }
    }
}
