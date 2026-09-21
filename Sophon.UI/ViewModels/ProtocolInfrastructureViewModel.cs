#nullable enable
using System;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using HandyControl.Controls;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Navigation.Regions;
using Sophon.Contracts;
using Sophon.Infrastructure.Config;

namespace Sophon.UI.ViewModels
{
    public class ProtocolInfrastructureViewModel : BindableBase, INavigationAware
    {
        private readonly PeripheralDeviceConfigStore _deviceStore = new();
        private readonly CylinderConfigStore _cylinderStore = new();

        public ObservableCollection<PeripheralDeviceConfig> Devices { get; } = new();
        public ObservableCollection<CylinderDefinition> Cylinders { get; } = new();
        public ObservableCollection<DeviceTag> SelectedTags { get; } = new();
        public ObservableCollection<string> AvailablePorts { get; } = new();

        public Array Transports { get; } = Enum.GetValues(typeof(DeviceTransport));
        public Array ValveKinds { get; } = Enum.GetValues(typeof(ValveType));
        public Array ModbusAreas { get; } = Enum.GetValues(typeof(ModbusArea));
        public Array DataTypes { get; } = Enum.GetValues(typeof(RegisterDataType));
        public string[] ParityOptions { get; } = { "None", "Even", "Odd" };
        public string[] StopBitOptions { get; } = { "One", "Two", "OnePointFive" };
        public int[] BaudOptions { get; } = { 9600, 19200, 38400, 57600, 115200 };

        private PeripheralDeviceConfig? _selectedDevice;
        public PeripheralDeviceConfig? SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (SetProperty(ref _selectedDevice, value))
                {
                    ReloadTags();
                    RaisePropertyChanged(nameof(IsSerialTransport));
                    RaisePropertyChanged(nameof(IsTcpTransport));
                }
            }
        }

        private DeviceTag? _selectedTag;
        public DeviceTag? SelectedTag
        {
            get => _selectedTag;
            set => SetProperty(ref _selectedTag, value);
        }

        private CylinderDefinition? _selectedCylinder;
        public CylinderDefinition? SelectedCylinder
        {
            get => _selectedCylinder;
            set => SetProperty(ref _selectedCylinder, value);
        }

        public bool IsSerialTransport =>
            SelectedDevice != null && SelectedDevice.Transport != DeviceTransport.ModbusTcp;

        public bool IsTcpTransport =>
            SelectedDevice != null && SelectedDevice.Transport == DeviceTransport.ModbusTcp;

        public DelegateCommand AddDeviceCommand { get; }
        public DelegateCommand DeleteDeviceCommand { get; }
        public DelegateCommand AddTagCommand { get; }
        public DelegateCommand DeleteTagCommand { get; }
        public DelegateCommand SaveDevicesCommand { get; }
        public DelegateCommand RefreshPortsCommand { get; }
        public DelegateCommand AddCylinderCommand { get; }
        public DelegateCommand DeleteCylinderCommand { get; }
        public DelegateCommand SaveCylindersCommand { get; }

        public ProtocolInfrastructureViewModel()
        {
            AddDeviceCommand = new DelegateCommand(OnAddDevice);
            DeleteDeviceCommand = new DelegateCommand(OnDeleteDevice, () => SelectedDevice != null).ObservesProperty(() => SelectedDevice);
            AddTagCommand = new DelegateCommand(OnAddTag, () => SelectedDevice != null).ObservesProperty(() => SelectedDevice);
            DeleteTagCommand = new DelegateCommand(OnDeleteTag, () => SelectedTag != null).ObservesProperty(() => SelectedTag);
            SaveDevicesCommand = new DelegateCommand(OnSaveDevices);
            RefreshPortsCommand = new DelegateCommand(RefreshPorts);
            AddCylinderCommand = new DelegateCommand(OnAddCylinder);
            DeleteCylinderCommand = new DelegateCommand(OnDeleteCylinder, () => SelectedCylinder != null).ObservesProperty(() => SelectedCylinder);
            SaveCylindersCommand = new DelegateCommand(OnSaveCylinders);
            Reload();
        }

        public void OnNavigatedTo(NavigationContext navigationContext) => Reload();
        public bool IsNavigationTarget(NavigationContext navigationContext) => true;
        public void OnNavigatedFrom(NavigationContext navigationContext) { }

        private void Reload()
        {
            RefreshPorts();

            Devices.Clear();
            var devices = _deviceStore.Load();
            if (devices.Count == 0)
            {
                devices = PeripheralDeviceConfigStore.SeedDefaults();
                devices[0].PortName = AvailablePorts.FirstOrDefault() ?? string.Empty;
                _deviceStore.Save(devices);
            }
            foreach (var d in devices) Devices.Add(d);
            SelectedDevice = Devices.FirstOrDefault();

            Cylinders.Clear();
            var cylinders = _cylinderStore.Load();
            if (cylinders.Count == 0)
            {
                cylinders = CylinderConfigStore.SeedDefaults();
                _cylinderStore.Save(cylinders);
            }
            foreach (var c in cylinders) Cylinders.Add(c);
            SelectedCylinder = Cylinders.FirstOrDefault();
        }

        private void RefreshPorts()
        {
            AvailablePorts.Clear();
            try
            {
                foreach (var p in SerialPort.GetPortNames().OrderBy(x => x))
                {
                    AvailablePorts.Add(p);
                }
            }
            catch
            {
            }

            if (AvailablePorts.Count == 0)
            {
                AvailablePorts.Add("COM1");
            }
        }

        private void ReloadTags()
        {
            SelectedTags.Clear();
            if (SelectedDevice?.Tags == null) return;
            foreach (var t in SelectedDevice.Tags) SelectedTags.Add(t);
            SelectedTag = SelectedTags.FirstOrDefault();
            RaisePropertyChanged(nameof(IsSerialTransport));
            RaisePropertyChanged(nameof(IsTcpTransport));
        }

        private void OnAddDevice()
        {
            var cfg = PeripheralDeviceConfigStore.SeedDefaults()[0];
            cfg.Name = $"外设{Devices.Count + 1}";
            cfg.PortName = AvailablePorts.FirstOrDefault() ?? string.Empty;
            Devices.Add(cfg);
            SelectedDevice = cfg;
        }

        private void OnDeleteDevice()
        {
            if (SelectedDevice == null) return;
            Devices.Remove(SelectedDevice);
            SelectedDevice = Devices.FirstOrDefault();
        }

        private void OnAddTag()
        {
            if (SelectedDevice == null) return;
            var tag = new DeviceTag
            {
                Name = $"点位{SelectedDevice.Tags.Count + 1}",
                Area = ModbusArea.HoldingRegister,
                Address = (ushort)SelectedDevice.Tags.Count,
                DataType = RegisterDataType.UInt16,
                Scale = 1.0
            };
            SelectedDevice.Tags.Add(tag);
            ReloadTags();
            SelectedTag = tag;
        }

        private void OnDeleteTag()
        {
            if (SelectedDevice == null || SelectedTag == null) return;
            SelectedDevice.Tags.Remove(SelectedTag);
            ReloadTags();
        }

        private void OnSaveDevices()
        {
            try
            {
                _deviceStore.Save(Devices.ToList());
                Growl.Success("485/Modbus 外设档案已保存。运行监视页不会自动打开串口，需手动「连接采集」。");
            }
            catch (Exception ex)
            {
                Growl.Error($"保存外设档案失败: {ex.Message}");
            }
        }

        private void OnAddCylinder()
        {
            var cyl = CylinderConfigStore.SeedDefaults()[0];
            cyl.Name = $"气缸{Cylinders.Count + 1}";
            Cylinders.Add(cyl);
            SelectedCylinder = cyl;
        }

        private void OnDeleteCylinder()
        {
            if (SelectedCylinder == null) return;
            Cylinders.Remove(SelectedCylinder);
            SelectedCylinder = Cylinders.FirstOrDefault();
        }

        private void OnSaveCylinders()
        {
            try
            {
                _cylinderStore.Save(Cylinders.ToList());
                Growl.Success("气缸档案已保存。点名需与「IO映射与监控」中的逻辑点一致。");
            }
            catch (Exception ex)
            {
                Growl.Error($"保存气缸档案失败: {ex.Message}");
            }
        }
    }
}
