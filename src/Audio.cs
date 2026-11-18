// dock.txt. Audio.cs — общая громкость Windows (Core Audio, описано руками, без сторонних библиотек) и заряд батареи.
// Громкость: устройство вывода по умолчанию, его уровень и «без звука»; об изменениях Windows сообщает сама,
// опрашивать её не нужно. Если что-то из этого не получилось — виджета громкости просто нет.
using System;
using System.Runtime.InteropServices;

namespace DevTxt.DockApp
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float db, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }

    // Все вызовы — из потока окна. Об изменении громкости Windows сообщает в своём потоке: оттуда только зовём changed.
    sealed class Audio : IAudioEndpointVolumeCallback
    {
        const int Render = 0, Multimedia = 1, ClsCtxAll = 23;
        readonly IMMDeviceEnumerator devices;
        readonly Action changed;
        IAudioEndpointVolume volume;
        string deviceId;

        Audio(IMMDeviceEnumerator devices, Action changed) { this.devices = devices; this.changed = changed; }

        // null — звуковой системы нет или она не отвечает
        public static Audio Open(Action changed)
        {
            try
            {
                var a = new Audio((IMMDeviceEnumerator)new MMDeviceEnumeratorCom(), changed);
                a.Rebind();
                return a;
            }
            catch { return null; }
        }

        public bool Has { get { return volume != null; } }

        // Устройство по умолчанию могли сменить (включили наушники): раз в несколько секунд проверяем и перецепляемся
        public void Rebind()
        {
            try
            {
                IMMDevice device;
                string id = null;
                if (devices.GetDefaultAudioEndpoint(Render, Multimedia, out device) != 0 || device == null) { Drop(); return; }
                try
                {
                    if (device.GetId(out id) != 0) id = null;
                    if (volume != null && id != null && id == deviceId) return;
                    Drop();
                    Guid iid = typeof(IAudioEndpointVolume).GUID;
                    object o;
                    if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out o) != 0 || o == null) return;
                    volume = (IAudioEndpointVolume)o;
                    deviceId = id;
                    volume.RegisterControlChangeNotify(this);
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            catch { Drop(); }
        }

        void Drop()
        {
            if (volume == null) return;
            try { volume.UnregisterControlChangeNotify(this); } catch { }
            try { Marshal.ReleaseComObject(volume); } catch { }
            volume = null;
            deviceId = null;
        }

        public void Close() { Drop(); }

        public bool Read(out double level, out bool muted)
        {
            level = 0; muted = false;
            try
            {
                float f;
                if (volume == null || volume.GetMasterVolumeLevelScalar(out f) != 0 || volume.GetMute(out muted) != 0) return false;
                level = f;
                return true;
            }
            catch { return false; }
        }

        public void Set(double level)
        {
            Guid none = Guid.Empty;
            try { if (volume != null) volume.SetMasterVolumeLevelScalar((float)Math.Max(0, Math.Min(1, level)), ref none); } catch { }
        }

        public void Mute(bool on)
        {
            Guid none = Guid.Empty;
            try { if (volume != null) volume.SetMute(on, ref none); } catch { }
        }

        public int OnNotify(IntPtr data)
        {
            try { if (changed != null) changed(); } catch { }
            return 0;
        }
    }

    static class Power
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Status
        {
            public byte AcLine, BatteryFlag, BatteryPercent, SystemFlag;
            public int LifeTime, FullLifeTime;
        }

        [DllImport("kernel32.dll")]
        static extern bool GetSystemPowerStatus(out Status status);

        public static BatteryInfo Read()
        {
            try
            {
                Status s;
                if (GetSystemPowerStatus(out s)) return BatteryInfo.From(s.AcLine, s.BatteryFlag, s.BatteryPercent);
            }
            catch { }
            return BatteryInfo.From(0, 255, 255);
        }
    }
}
