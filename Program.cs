using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.ComponentModel;
using Microsoft.Win32;

[assembly: AssemblyTitle("星耀电源模式")]
[assembly: AssemblyDescription("根据插电或电池状态自动切换屏幕刷新率和机械革命性能模式")]
[assembly: AssemblyCompany("AkiaPolliCamelia")]
[assembly: AssemblyProduct("星耀电源模式")]
[assembly: AssemblyCopyright("Copyright © 2026 AkiaPolliCamelia")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace MechrevoPowerSwitcher
{
    internal static class Localization
    {
        private static readonly bool Chinese = string.Equals(
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            "zh",
            StringComparison.OrdinalIgnoreCase);

        public static string Text(string simplifiedChinese, string english)
        {
            return Chinese ? simplifiedChinese : english;
        }
    }

    internal enum PerformanceMode
    {
        Office = 0,
        Balanced = 1,
        Performance = 2
    }

    internal sealed class Profile
    {
        public int RefreshRate;
        public PerformanceMode Mode;

        public Profile(int refreshRate, PerformanceMode mode)
        {
            RefreshRate = refreshRate;
            Mode = mode;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                RunSelfTest();
                return;
            }

            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\MechrevoPowerSwitcher-5F82B84A", out created))
            {
                if (!created)
                    return;

                DpiSupport.Enable();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (TrayApplication app = new TrayApplication())
                    Application.Run(app);
            }
        }

        private static void RunSelfTest()
        {
            bool ac = PowerSource.IsPluggedIn();
            string device = DisplayController.FindInternalDisplay();
            int current = DisplayController.GetCurrentRefreshRate(device);
            IList<int> rates = DisplayController.GetSupportedRefreshRates(device);
            Console.WriteLine("Power=" + (ac ? "AC" : "Battery"));
            Console.WriteLine("Display=" + (device ?? "Primary fallback"));
            Console.WriteLine("CurrentHz=" + current);
            Console.WriteLine("SupportedHz=" + string.Join(",", ToStrings(rates)));
            Console.WriteLine("GCUBridge=" + (MqttController.CanConnect() ? "OK" : "Unavailable"));
        }

        private static string[] ToStrings(IList<int> values)
        {
            string[] result = new string[values.Count];
            for (int i = 0; i < values.Count; i++)
                result[i] = values[i].ToString();
            return result;
        }
    }

    internal sealed class TrayApplication : ApplicationContext
    {
        private readonly NotifyIcon notifyIcon;
        private readonly ContextMenuStrip menu;
        private readonly ToolStripMenuItem acMenu;
        private readonly ToolStripMenuItem batteryMenu;
        private readonly ToolStripMenuItem startupItem;
        private readonly ToolStripMenuItem powerStatusItem;
        private readonly ToolStripMenuItem refreshStatusItem;
        private readonly ToolStripMenuItem modeStatusItem;
        private readonly Icon acIcon;
        private readonly Icon batteryIcon;
        private readonly System.Windows.Forms.Timer debounceTimer;
        private readonly SynchronizationContext uiContext;
        private readonly MqttStatusMonitor statusMonitor;
        private readonly object applyLock = new object();
        private bool? lastPowerState;
        private PerformanceMode? currentMode;
        private bool disposed;

        public TrayApplication()
        {
            uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            acIcon = IconFactory.Create(true);
            batteryIcon = IconFactory.Create(false);

            menu = new ContextMenuStrip();
            powerStatusItem = CreateStatusItem(Localization.Text("当前供电：读取中", "Power source: Loading"));
            refreshStatusItem = CreateStatusItem(Localization.Text("当前刷新率：读取中", "Refresh rate: Loading"));
            modeStatusItem = CreateStatusItem(Localization.Text("当前性能模式：读取中", "Performance mode: Loading"));
            menu.Items.Add(powerStatusItem);
            menu.Items.Add(refreshStatusItem);
            menu.Items.Add(modeStatusItem);
            menu.Items.Add(new ToolStripSeparator());

            acMenu = new ToolStripMenuItem(Localization.Text("插电设置", "AC settings"));
            batteryMenu = new ToolStripMenuItem(Localization.Text("电池设置", "Battery settings"));
            startupItem = new ToolStripMenuItem(Localization.Text("开机自启动", "Start with Windows"));
            startupItem.CheckOnClick = true;
            startupItem.Checked = Settings.IsAutoStartEnabled();
            startupItem.Click += delegate { Settings.SetAutoStart(startupItem.Checked); };

            BuildSettingsMenu(acMenu, true);
            BuildSettingsMenu(batteryMenu, false);
            menu.Items.Add(acMenu);
            menu.Items.Add(batteryMenu);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem exitItem = new ToolStripMenuItem(Localization.Text("退出", "Exit"));
            exitItem.Click += delegate { ExitThread(); };
            menu.Items.Add(exitItem);

            notifyIcon = new NotifyIcon();
            notifyIcon.ContextMenuStrip = menu;
            notifyIcon.Visible = true;
            menu.Opening += OnMenuOpening;

            debounceTimer = new System.Windows.Forms.Timer();
            debounceTimer.Interval = 1200;
            debounceTimer.Tick += delegate
            {
                debounceTimer.Stop();
                UpdatePowerState(true);
            };

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            statusMonitor = new MqttStatusMonitor();
            statusMonitor.ModeChanged += OnPerformanceModeChanged;
            statusMonitor.Start();
            UpdateMenus();
            UpdatePowerState(true);
        }

        private void OnPerformanceModeChanged(PerformanceMode mode)
        {
            uiContext.Post(delegate
            {
                if (disposed) return;
                currentMode = mode;
                RefreshStatusMenu();
            }, null);
        }

        private static ToolStripMenuItem CreateStatusItem(string text)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Enabled = false;
            return item;
        }

        private void BuildSettingsMenu(ToolStripMenuItem parent, bool forAc)
        {
            ToolStripMenuItem refreshHeader = CreateStatusItem(Localization.Text("刷新率", "Refresh rate"));
            parent.DropDownItems.Add(refreshHeader);
            int[] rates = new int[] { 60, 120 };
            foreach (int rateValue in rates)
            {
                int rate = rateValue;
                ToolStripMenuItem item = new ToolStripMenuItem(rate + " Hz");
                item.Tag = new MenuChoice(true, rate);
                item.Click += delegate
                {
                    Profile profile = Settings.LoadProfile(forAc);
                    profile.RefreshRate = rate;
                    Settings.SaveProfile(forAc, profile);
                    UpdateMenus();
                    if (PowerSource.IsPluggedIn() == forAc)
                        ScheduleApply();
                };
                parent.DropDownItems.Add(item);
            }

            parent.DropDownItems.Add(new ToolStripSeparator());
            parent.DropDownItems.Add(CreateStatusItem(Localization.Text("性能模式", "Performance mode")));
            PerformanceMode[] modes = new PerformanceMode[]
            {
                PerformanceMode.Office, PerformanceMode.Balanced, PerformanceMode.Performance
            };
            foreach (PerformanceMode modeValue in modes)
            {
                PerformanceMode mode = modeValue;
                ToolStripMenuItem item = new ToolStripMenuItem(ModeName(mode));
                item.Tag = new MenuChoice(false, (int)mode);
                item.Click += delegate
                {
                    Profile profile = Settings.LoadProfile(forAc);
                    profile.Mode = mode;
                    Settings.SaveProfile(forAc, profile);
                    UpdateMenus();
                    if (PowerSource.IsPluggedIn() == forAc)
                        ScheduleApply();
                };
                parent.DropDownItems.Add(item);
            }
        }

        private void UpdateMenus()
        {
            Profile ac = Settings.LoadProfile(true);
            Profile dc = Settings.LoadProfile(false);
            CheckSettingItems(acMenu, ac);
            CheckSettingItems(batteryMenu, dc);
        }

        private static void CheckSettingItems(ToolStripMenuItem parent, Profile selected)
        {
            foreach (ToolStripItem raw in parent.DropDownItems)
            {
                ToolStripMenuItem item = raw as ToolStripMenuItem;
                if (item == null)
                    continue;
                MenuChoice choice = item.Tag as MenuChoice;
                item.Checked = choice != null && (choice.IsRefreshRate
                    ? choice.Value == selected.RefreshRate
                    : choice.Value == (int)selected.Mode);
            }
        }

        private static string ModeName(PerformanceMode mode)
        {
            if (mode == PerformanceMode.Office) return Localization.Text("办公模式", "Office mode");
            if (mode == PerformanceMode.Balanced) return Localization.Text("平衡模式", "Balanced mode");
            return Localization.Text("性能模式", "Performance mode");
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.StatusChange || e.Mode == PowerModes.Resume)
            {
                uiContext.Post(delegate
                {
                    if (disposed) return;
                    debounceTimer.Stop();
                    debounceTimer.Start();
                }, null);
            }
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            uiContext.Post(delegate
            {
                if (disposed) return;
                RefreshStatusMenu();
            }, null);
        }

        private void UpdatePowerState(bool forceApply)
        {
            bool ac = PowerSource.IsPluggedIn();
            notifyIcon.Icon = ac ? acIcon : batteryIcon;
            RefreshStatusMenu();

            if (forceApply || !lastPowerState.HasValue || lastPowerState.Value != ac)
            {
                lastPowerState = ac;
                QueueApply(ac);
            }
        }

        private void ScheduleApply()
        {
            debounceTimer.Stop();
            debounceTimer.Start();
        }

        private void QueueApply(bool ac)
        {
            bool powerState = ac;
            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (applyLock)
                {
                    Profile profile = Settings.LoadProfile(powerState);
                    string device = DisplayController.FindInternalDisplay();
                    DisplayController.SetRefreshRate(device, profile.RefreshRate);
                    bool modeConfirmed = MqttController.SetPerformanceMode(profile.Mode);
                    uiContext.Post(delegate
                    {
                        if (disposed) return;
                        currentMode = modeConfirmed ? (PerformanceMode?)profile.Mode : null;
                        RefreshStatusMenu();
                    }, null);
                }
            });
        }

        private void OnMenuOpening(object sender, CancelEventArgs e)
        {
            RefreshStatusMenu();
        }

        private void RefreshStatusMenu()
        {
            bool ac = PowerSource.IsPluggedIn();
            string device = DisplayController.FindInternalDisplay();
            int refresh = DisplayController.GetCurrentRefreshRate(device);
            powerStatusItem.Text = Localization.Text("当前供电：", "Power source: ") +
                (ac ? Localization.Text("已插电", "Plugged in") : Localization.Text("使用电池", "On battery"));
            refreshStatusItem.Text = Localization.Text("当前刷新率：", "Refresh rate: ") +
                (refresh > 0 ? refresh + " Hz" : Localization.Text("未知", "Unknown"));
            modeStatusItem.Text = Localization.Text("当前性能模式：", "Performance mode: ") +
                (currentMode.HasValue ? ModeName(currentMode.Value) : Localization.Text("读取中", "Loading"));
            string powerText = ac ? Localization.Text("已插电", "Plugged in") :
                Localization.Text("使用电池", "On battery");
            string refreshText = refresh > 0 ? refresh + " Hz" : Localization.Text("刷新率未知", "Refresh unknown");
            string modeText = currentMode.HasValue ? ModeName(currentMode.Value) :
                Localization.Text("性能模式读取中", "Mode loading");
            notifyIcon.Text = powerText + " | " + refreshText + " | " + modeText;
        }

        protected override void ExitThreadCore()
        {
            disposed = true;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            statusMonitor.ModeChanged -= OnPerformanceModeChanged;
            statusMonitor.Dispose();
            debounceTimer.Stop();
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            menu.Dispose();
            debounceTimer.Dispose();
            acIcon.Dispose();
            batteryIcon.Dispose();
            base.ExitThreadCore();
        }

        private sealed class MenuChoice
        {
            public readonly bool IsRefreshRate;
            public readonly int Value;
            public MenuChoice(bool isRefreshRate, int value)
            {
                IsRefreshRate = isRefreshRate;
                Value = value;
            }
        }
    }

    internal static class Settings
    {
        private const string SettingsKey = "Software\\MechrevoPowerSwitcher";
        private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string RunValue = "XingyaoPowerSwitcher";
        private const string LegacyRunValue = "MechrevoPowerSwitcher";
        private const string StartupApprovedRunKey =
            "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run";
        private const string StartupApprovedRun32Key =
            "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run32";

        public static Profile LoadProfile(bool ac)
        {
            int defaultRate = ac ? 120 : 60;
            PerformanceMode defaultMode = ac ? PerformanceMode.Performance : PerformanceMode.Office;
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
            {
                int rate = ReadInt(key, ac ? "AcRefreshRate" : "BatteryRefreshRate", defaultRate);
                int mode = ReadInt(key, ac ? "AcPerformanceMode" : "BatteryPerformanceMode", (int)defaultMode);
                if (rate != 60 && rate != 120) rate = defaultRate;
                if (mode < 0 || mode > 2) mode = (int)defaultMode;
                return new Profile(rate, (PerformanceMode)mode);
            }
        }

        public static void SaveProfile(bool ac, Profile profile)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
            {
                key.SetValue(ac ? "AcRefreshRate" : "BatteryRefreshRate", profile.RefreshRate, RegistryValueKind.DWord);
                key.SetValue(ac ? "AcPerformanceMode" : "BatteryPerformanceMode", (int)profile.Mode, RegistryValueKind.DWord);
            }
        }

        private static int ReadInt(RegistryKey key, string name, int fallback)
        {
            object value = key.GetValue(name);
            return value is int ? (int)value : fallback;
        }

        public static bool IsAutoStartEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                string value = key.GetValue(RunValue) as string;
                string legacyValue = key.GetValue(LegacyRunValue) as string;
                if (string.IsNullOrEmpty(value) && string.IsNullOrEmpty(legacyValue))
                    return false;

                string currentPath = QuotedCurrentPath();
                if (!string.Equals(value, currentPath, StringComparison.OrdinalIgnoreCase))
                    key.SetValue(RunValue, currentPath, RegistryValueKind.String);
                key.DeleteValue(LegacyRunValue, false);
                DeleteStartupApproval(LegacyRunValue);
                return true;
            }
        }

        public static void SetAutoStart(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled)
                    key.SetValue(RunValue, QuotedCurrentPath(), RegistryValueKind.String);
                else
                    key.DeleteValue(RunValue, false);
                key.DeleteValue(LegacyRunValue, false);
            }
            DeleteStartupApproval(RunValue);
            DeleteStartupApproval(LegacyRunValue);
        }

        private static string QuotedCurrentPath()
        {
            return "\"" + Path.GetFullPath(Application.ExecutablePath) + "\"";
        }

        private static void DeleteStartupApproval(string valueName)
        {
            DeleteRegistryValue(StartupApprovedRunKey, valueName);
            DeleteRegistryValue(StartupApprovedRun32Key, valueName);
        }

        private static void DeleteRegistryValue(string keyPath, string valueName)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath, true))
                {
                    if (key != null) key.DeleteValue(valueName, false);
                }
            }
            catch { }
        }
    }

    internal static class PowerSource
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte Reserved1;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        public static bool IsPluggedIn()
        {
            SYSTEM_POWER_STATUS status;
            return GetSystemPowerStatus(out status) && status.ACLineStatus == 1;
        }
    }

    internal static class DisplayController
    {
        private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        private const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11;
        private const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED = 13;
        private const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;
        private const int ENUM_CURRENT_SETTINGS = -1;
        private const uint DM_BITSPERPEL = 0x00040000;
        private const uint DM_PELSWIDTH = 0x00080000;
        private const uint DM_PELSHEIGHT = 0x00100000;
        private const uint DM_DISPLAYFREQUENCY = 0x00400000;
        private const uint CDS_TEST = 0x00000002;
        private const int DISP_CHANGE_SUCCESSFUL = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] data;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public uint type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint pathCount,
            [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint modeCount,
            [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topologyId);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        private static extern int DisplayConfigGetSourceDeviceName(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsEx(string deviceName, int modeNum, ref DEVMODE devMode, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode,
            IntPtr hwnd, uint flags, IntPtr lParam);

        public static string FindInternalDisplay()
        {
            try
            {
                uint pathCount, modeCount;
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount) != 0)
                    return null;
                DISPLAYCONFIG_PATH_INFO[] paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                DISPLAYCONFIG_MODE_INFO[] modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                for (int i = 0; i < modes.Length; i++) modes[i].data = new byte[48];
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                    return null;

                for (int i = 0; i < pathCount; i++)
                {
                    uint technology = paths[i].targetInfo.outputTechnology;
                    if (technology != DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED &&
                        technology != DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED &&
                        technology != DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL)
                        continue;

                    DISPLAYCONFIG_SOURCE_DEVICE_NAME name = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                    name.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                    name.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME));
                    name.header.adapterId = paths[i].sourceInfo.adapterId;
                    name.header.id = paths[i].sourceInfo.id;
                    if (DisplayConfigGetSourceDeviceName(ref name) == 0 && !string.IsNullOrEmpty(name.viewGdiDeviceName))
                        return name.viewGdiDeviceName;
                }
            }
            catch { }
            return null;
        }

        public static int GetCurrentRefreshRate(string deviceName)
        {
            DEVMODE mode = NewDevMode();
            if (!EnumDisplaySettingsEx(deviceName, ENUM_CURRENT_SETTINGS, ref mode, 0))
                return 0;
            return (int)mode.dmDisplayFrequency;
        }

        public static IList<int> GetSupportedRefreshRates(string deviceName)
        {
            List<int> result = new List<int>();
            DEVMODE current = NewDevMode();
            if (!EnumDisplaySettingsEx(deviceName, ENUM_CURRENT_SETTINGS, ref current, 0))
                return result;
            for (int index = 0; ; index++)
            {
                DEVMODE mode = NewDevMode();
                if (!EnumDisplaySettingsEx(deviceName, index, ref mode, 0))
                    break;
                if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight &&
                    !result.Contains((int)mode.dmDisplayFrequency))
                    result.Add((int)mode.dmDisplayFrequency);
            }
            result.Sort();
            return result;
        }

        public static bool SetRefreshRate(string deviceName, int refreshRate)
        {
            DEVMODE mode = NewDevMode();
            if (!EnumDisplaySettingsEx(deviceName, ENUM_CURRENT_SETTINGS, ref mode, 0))
                return false;
            if ((int)mode.dmDisplayFrequency == refreshRate || (refreshRate == 60 && mode.dmDisplayFrequency == 59))
                return true;

            mode.dmDisplayFrequency = (uint)refreshRate;
            mode.dmFields = DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            if (ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, CDS_TEST, IntPtr.Zero) != DISP_CHANGE_SUCCESSFUL)
                return false;
            return ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, 0, IntPtr.Zero) == DISP_CHANGE_SUCCESSFUL;
        }

        private static DEVMODE NewDevMode()
        {
            DEVMODE mode = new DEVMODE();
            mode.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE));
            return mode;
        }
    }

    internal static class MqttController
    {
        private const string Host = "127.0.0.1";
        private const int Port = 13688;

        public static bool CanConnect()
        {
            TcpClient client;
            NetworkStream stream;
            if (!TryConnect(out client, out stream)) return false;
            try { SendPacket(stream, 0xE0, new byte[0]); } catch { }
            client.Close();
            return true;
        }

        public static bool SetPerformanceMode(PerformanceMode mode)
        {
            TcpClient client;
            NetworkStream stream;
            if (!TryConnect(out client, out stream)) return false;
            try
            {
                Subscribe(stream, "Fan/Status");
                string action = mode == PerformanceMode.Office ? "OPERATING_OFFICE_MODE" :
                    mode == PerformanceMode.Balanced ? "OPERATING_GAMING_MODE" : "OPERATING_TURBO_MODE";
                string json = "{\"Action\":\"" + action + "\",\"ProfileIndex\":\"0\"}";
                Publish(stream, "Fan/Control", json);
                bool confirmed = WaitForMode(stream, (int)mode);
                try { SendPacket(stream, 0xE0, new byte[0]); } catch { }
                return confirmed;
            }
            catch { return false; }
            finally { client.Close(); }
        }

        private static bool TryConnect(out TcpClient connectedClient, out NetworkStream connectedStream)
        {
            connectedClient = null;
            connectedStream = null;
            int[] slots = new int[] { 8, 7, 6, 5, 4, 2, 1, 0, 3, 9 };
            foreach (int slot in slots)
            {
                TcpClient client = null;
                try
                {
                    client = new TcpClient();
                    IAsyncResult ar = client.BeginConnect(Host, Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(700)) { client.Close(); continue; }
                    client.EndConnect(ar);
                    NetworkStream stream = client.GetStream();
                    stream.ReadTimeout = 1800;
                    stream.WriteTimeout = 1800;

                    List<byte> body = new List<byte>();
                    AddMqttString(body, "MQTT");
                    body.Add(4);
                    body.Add(0xC2);
                    body.Add(0);
                    body.Add(30);
                    AddMqttString(body, "UWPClient_" + slot);
                    AddMqttString(body, "UWPClient_User_" + slot);
                    AddMqttString(body, "UWPClient_Pwd888881772688_" + slot);
                    SendPacket(stream, 0x10, body.ToArray());
                    MqttPacket response = ReadPacket(stream);
                    if (response != null && response.Type == 2 && response.Body.Length >= 2 && response.Body[1] == 0)
                    {
                        connectedClient = client;
                        connectedStream = stream;
                        return true;
                    }
                }
                catch { }
                if (client != null) client.Close();
            }
            return false;
        }

        private static void Subscribe(NetworkStream stream, string topic)
        {
            List<byte> body = new List<byte>();
            body.Add(0);
            body.Add(1);
            AddMqttString(body, topic);
            body.Add(0);
            SendPacket(stream, 0x82, body.ToArray());
            ReadPacket(stream);
        }

        private static void Publish(NetworkStream stream, string topic, string payload)
        {
            List<byte> body = new List<byte>();
            AddMqttString(body, topic);
            body.AddRange(Encoding.UTF8.GetBytes(payload));
            SendPacket(stream, 0x30, body.ToArray());
        }

        private static bool WaitForMode(NetworkStream stream, int expectedMode)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(1600);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    MqttPacket packet = ReadPacket(stream);
                    if (packet == null || packet.Type != 3) continue;
                    string topic, payload;
                    ParsePublish(packet, out topic, out payload);
                    if (topic == "Fan/Status" && payload.IndexOf("\"OperatingMode\":" + expectedMode, StringComparison.Ordinal) >= 0)
                        return true;
                }
                catch (IOException) { break; }
            }
            return false;
        }

        private static void ParsePublish(MqttPacket packet, out string topic, out string payload)
        {
            int topicLength = (packet.Body[0] << 8) | packet.Body[1];
            topic = Encoding.UTF8.GetString(packet.Body, 2, topicLength);
            int offset = 2 + topicLength;
            int qos = (packet.Header >> 1) & 3;
            if (qos > 0) offset += 2;
            payload = Encoding.UTF8.GetString(packet.Body, offset, packet.Body.Length - offset);
        }

        private static void SendPacket(NetworkStream stream, byte header, byte[] body)
        {
            List<byte> packet = new List<byte>();
            packet.Add(header);
            int remaining = body.Length;
            do
            {
                int digit = remaining % 128;
                remaining /= 128;
                if (remaining > 0) digit |= 0x80;
                packet.Add((byte)digit);
            } while (remaining > 0);
            packet.AddRange(body);
            byte[] bytes = packet.ToArray();
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private static MqttPacket ReadPacket(NetworkStream stream)
        {
            int header = stream.ReadByte();
            if (header < 0) return null;
            int multiplier = 1;
            int length = 0;
            int digit;
            do
            {
                digit = stream.ReadByte();
                if (digit < 0) return null;
                length += (digit & 127) * multiplier;
                multiplier *= 128;
            } while ((digit & 128) != 0);

            byte[] body = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = stream.Read(body, offset, length - offset);
                if (read <= 0) return null;
                offset += read;
            }
            return new MqttPacket((byte)header, body);
        }

        private static void AddMqttString(List<byte> target, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            target.Add((byte)(bytes.Length >> 8));
            target.Add((byte)(bytes.Length & 255));
            target.AddRange(bytes);
        }

        private sealed class MqttPacket
        {
            public readonly byte Header;
            public readonly byte[] Body;
            public int Type { get { return Header >> 4; } }
            public MqttPacket(byte header, byte[] body) { Header = header; Body = body; }
        }
    }

    internal sealed class MqttStatusMonitor : IDisposable
    {
        private const string Host = "127.0.0.1";
        private const int Port = 13688;
        private const int MaximumPacketSize = 65536;
        private readonly object sync = new object();
        private readonly List<byte> pending = new List<byte>();
        private TcpClient client;
        private NetworkStream stream;
        private System.Threading.Timer reconnectTimer;
        private bool connecting;
        private bool disposed;
        private int reconnectDelay = 2000;

        public event Action<PerformanceMode> ModeChanged;

        public void Start()
        {
            ScheduleReconnect(0);
        }

        private void ScheduleReconnect(int dueTime)
        {
            lock (sync)
            {
                if (disposed || connecting || stream != null || reconnectTimer != null)
                    return;
                reconnectTimer = new System.Threading.Timer(Connect, null, dueTime, Timeout.Infinite);
            }
        }

        private void Connect(object state)
        {
            lock (sync)
            {
                if (reconnectTimer != null)
                {
                    reconnectTimer.Dispose();
                    reconnectTimer = null;
                }
                if (disposed || connecting || stream != null)
                    return;
                connecting = true;
            }

            TcpClient newClient = null;
            NetworkStream newStream = null;
            bool opened = TryOpen(out newClient, out newStream);
            bool beginReceive = false;
            int retryAfter = 0;
            lock (sync)
            {
                connecting = false;
                if (disposed)
                {
                    if (newClient != null) newClient.Close();
                    return;
                }
                if (opened)
                {
                    client = newClient;
                    stream = newStream;
                    pending.Clear();
                    reconnectDelay = 2000;
                    beginReceive = true;
                }
                else
                {
                    retryAfter = reconnectDelay;
                    reconnectDelay = Math.Min(reconnectDelay * 2, 30000);
                }
            }

            if (beginReceive)
                BeginReceive(newStream);
            else
                ScheduleReconnect(retryAfter);
        }

        private static bool TryOpen(out TcpClient connectedClient, out NetworkStream connectedStream)
        {
            connectedClient = null;
            connectedStream = null;
            int[] slots = new int[] { 9, 8, 7, 6, 5, 4, 2, 1, 0, 3 };
            foreach (int slot in slots)
            {
                TcpClient candidate = null;
                try
                {
                    candidate = new TcpClient();
                    IAsyncResult ar = candidate.BeginConnect(Host, Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(700))
                    {
                        candidate.Close();
                        continue;
                    }
                    candidate.EndConnect(ar);
                    NetworkStream candidateStream = candidate.GetStream();
                    candidateStream.ReadTimeout = 1800;
                    candidateStream.WriteTimeout = 1800;

                    List<byte> body = new List<byte>();
                    AddMqttString(body, "MQTT");
                    body.Add(4);
                    body.Add(0xC2);
                    body.Add(0);
                    body.Add(0);
                    AddMqttString(body, "UWPClient_" + slot);
                    AddMqttString(body, "UWPClient_User_" + slot);
                    AddMqttString(body, "UWPClient_Pwd888881772688_" + slot);
                    SendPacket(candidateStream, 0x10, body.ToArray());
                    MqttPacket response = ReadPacket(candidateStream);
                    if (response == null || response.Type != 2 || response.Body.Length < 2 || response.Body[1] != 0)
                        throw new IOException("MQTT authentication failed");

                    Subscribe(candidateStream, "Fan/Status");
                    candidateStream.ReadTimeout = Timeout.Infinite;
                    connectedClient = candidate;
                    connectedStream = candidateStream;
                    return true;
                }
                catch { }
                if (candidate != null) candidate.Close();
            }
            return false;
        }

        private void BeginReceive(NetworkStream expectedStream)
        {
            try
            {
                ReadState state = new ReadState(expectedStream);
                expectedStream.BeginRead(state.Buffer, 0, state.Buffer.Length, ReadCompleted, state);
            }
            catch
            {
                DisconnectAndRetry(expectedStream);
            }
        }

        private void ReadCompleted(IAsyncResult result)
        {
            ReadState state = (ReadState)result.AsyncState;
            int count;
            try { count = state.Stream.EndRead(result); }
            catch
            {
                DisconnectAndRetry(state.Stream);
                return;
            }
            if (count <= 0)
            {
                DisconnectAndRetry(state.Stream);
                return;
            }

            List<PerformanceMode> receivedModes = new List<PerformanceMode>();
            bool invalidPacket = false;
            lock (sync)
            {
                if (disposed || stream != state.Stream)
                    return;
                for (int i = 0; i < count; i++) pending.Add(state.Buffer[i]);
                invalidPacket = !ExtractPackets(receivedModes);
            }
            if (invalidPacket)
            {
                DisconnectAndRetry(state.Stream);
                return;
            }

            Action<PerformanceMode> handler = ModeChanged;
            if (handler != null)
            {
                foreach (PerformanceMode mode in receivedModes)
                {
                    try { handler(mode); }
                    catch { }
                }
            }
            BeginReceive(state.Stream);
        }

        private bool ExtractPackets(List<PerformanceMode> receivedModes)
        {
            while (pending.Count >= 2)
            {
                int multiplier = 1;
                int remainingLength = 0;
                int index = 1;
                int digitCount = 0;
                bool completeLength = false;
                while (index < pending.Count && digitCount < 4)
                {
                    int digit = pending[index++];
                    digitCount++;
                    remainingLength += (digit & 127) * multiplier;
                    if ((digit & 128) == 0)
                    {
                        completeLength = true;
                        break;
                    }
                    multiplier *= 128;
                }
                if (!completeLength)
                    return digitCount < 4;
                if (remainingLength < 0 || remainingLength > MaximumPacketSize)
                    return false;
                int totalLength = index + remainingLength;
                if (pending.Count < totalLength)
                    return true;

                byte header = pending[0];
                byte[] body = pending.GetRange(index, remainingLength).ToArray();
                pending.RemoveRange(0, totalLength);
                if ((header >> 4) == 3)
                {
                    PerformanceMode mode;
                    if (TryParseStatusPublish(header, body, out mode))
                        receivedModes.Add(mode);
                }
            }
            return true;
        }

        private static bool TryParseStatusPublish(byte header, byte[] body, out PerformanceMode mode)
        {
            mode = PerformanceMode.Office;
            if (body.Length < 2) return false;
            int topicLength = (body[0] << 8) | body[1];
            int offset = 2 + topicLength;
            if (topicLength < 0 || offset > body.Length) return false;
            string topic = Encoding.UTF8.GetString(body, 2, topicLength);
            int qos = (header >> 1) & 3;
            if (qos > 0) offset += 2;
            if (offset > body.Length || topic != "Fan/Status") return false;
            string payload = Encoding.UTF8.GetString(body, offset, body.Length - offset);
            const string key = "\"OperatingMode\"";
            int keyIndex = payload.IndexOf(key, StringComparison.Ordinal);
            if (keyIndex < 0) return false;
            int colon = payload.IndexOf(':', keyIndex + key.Length);
            if (colon < 0) return false;
            int valueIndex = colon + 1;
            while (valueIndex < payload.Length && char.IsWhiteSpace(payload[valueIndex])) valueIndex++;
            if (valueIndex >= payload.Length || payload[valueIndex] < '0' || payload[valueIndex] > '2') return false;
            mode = (PerformanceMode)(payload[valueIndex] - '0');
            return true;
        }

        private void DisconnectAndRetry(NetworkStream expectedStream)
        {
            TcpClient oldClient = null;
            int retryAfter;
            lock (sync)
            {
                if (disposed || stream != expectedStream)
                    return;
                oldClient = client;
                client = null;
                stream = null;
                pending.Clear();
                retryAfter = reconnectDelay;
                reconnectDelay = Math.Min(reconnectDelay * 2, 30000);
            }
            try { if (oldClient != null) oldClient.Close(); }
            catch { }
            ScheduleReconnect(retryAfter);
        }

        public void Dispose()
        {
            TcpClient oldClient;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                if (reconnectTimer != null)
                {
                    reconnectTimer.Dispose();
                    reconnectTimer = null;
                }
                oldClient = client;
                client = null;
                stream = null;
                pending.Clear();
            }
            try { if (oldClient != null) oldClient.Close(); }
            catch { }
        }

        private static void Subscribe(NetworkStream target, string topic)
        {
            List<byte> body = new List<byte>();
            body.Add(0);
            body.Add(1);
            AddMqttString(body, topic);
            body.Add(0);
            SendPacket(target, 0x82, body.ToArray());
            MqttPacket response = ReadPacket(target);
            if (response == null || response.Type != 9)
                throw new IOException("MQTT subscription failed");
        }

        private static void SendPacket(NetworkStream target, byte header, byte[] body)
        {
            List<byte> packet = new List<byte>();
            packet.Add(header);
            int remaining = body.Length;
            do
            {
                int digit = remaining % 128;
                remaining /= 128;
                if (remaining > 0) digit |= 0x80;
                packet.Add((byte)digit);
            } while (remaining > 0);
            packet.AddRange(body);
            byte[] bytes = packet.ToArray();
            target.Write(bytes, 0, bytes.Length);
            target.Flush();
        }

        private static MqttPacket ReadPacket(NetworkStream target)
        {
            int header = target.ReadByte();
            if (header < 0) return null;
            int multiplier = 1;
            int length = 0;
            int digit;
            int digitCount = 0;
            do
            {
                digit = target.ReadByte();
                if (digit < 0) return null;
                digitCount++;
                if (digitCount > 4) return null;
                length += (digit & 127) * multiplier;
                multiplier *= 128;
            } while ((digit & 128) != 0);
            if (length > MaximumPacketSize) return null;

            byte[] body = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = target.Read(body, offset, length - offset);
                if (read <= 0) return null;
                offset += read;
            }
            return new MqttPacket((byte)header, body);
        }

        private static void AddMqttString(List<byte> target, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            target.Add((byte)(bytes.Length >> 8));
            target.Add((byte)(bytes.Length & 255));
            target.AddRange(bytes);
        }

        private sealed class ReadState
        {
            public readonly NetworkStream Stream;
            public readonly byte[] Buffer = new byte[4096];
            public ReadState(NetworkStream stream) { Stream = stream; }
        }

        private sealed class MqttPacket
        {
            public readonly byte Header;
            public readonly byte[] Body;
            public int Type { get { return Header >> 4; } }
            public MqttPacket(byte header, byte[] body) { Header = header; Body = body; }
        }
    }

    internal static class DpiSupport
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        public static void Enable()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4)))
                    return;
            }
            catch (EntryPointNotFoundException) { }
            try { SetProcessDPIAware(); }
            catch { }
        }
    }

    internal static class IconFactory
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon Create(bool ac)
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                using (Brush background = new SolidBrush(ac ? Color.FromArgb(39, 174, 96) : Color.FromArgb(52, 120, 246)))
                    graphics.FillEllipse(background, 1, 1, 30, 30);
                using (Pen pen = new Pen(Color.White, 2.8f))
                using (Brush white = new SolidBrush(Color.White))
                {
                    if (ac)
                    {
                        graphics.DrawLine(pen, 11, 8, 11, 14);
                        graphics.DrawLine(pen, 21, 8, 21, 14);
                        graphics.DrawRectangle(pen, 9, 13, 14, 7);
                        graphics.DrawLine(pen, 16, 20, 16, 25);
                    }
                    else
                    {
                        graphics.DrawRectangle(pen, 7, 10, 17, 13);
                        graphics.FillRectangle(white, 24, 14, 3, 5);
                        graphics.FillRectangle(white, 10, 13, 8, 7);
                    }
                }
                IntPtr handle = bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
    }
}
