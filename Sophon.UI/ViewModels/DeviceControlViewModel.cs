#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using HandyControl.Controls;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Navigation.Regions;
using Sophon.Contracts;
using Sophon.Core.Device;
using Sophon.Infrastructure.Config;

namespace Sophon.UI.ViewModels
{
    public class CylinderCardVm : BindableBase
    {
        public CylinderDefinition Definition { get; }
        private readonly CylinderService _service;
        private readonly IIoController? _io;

        public string Name => Definition.Name;
        public string ValveText => Definition.Valve == ValveType.DoubleCoil ? "双电控脉冲" : "单电控自保持";
        public string InterlockGroup => string.IsNullOrWhiteSpace(Definition.InterlockGroup) ? "无" : Definition.InterlockGroup;

        private CylinderPosition _position = CylinderPosition.Unknown;
        public CylinderPosition Position
        {
            get => _position;
            set
            {
                if (SetProperty(ref _position, value))
                {
                    RaisePropertyChanged(nameof(PositionText));
                    RaisePropertyChanged(nameof(PositionColor));
                }
            }
        }

        public string PositionText => Position switch
        {
            CylinderPosition.Work => "已伸出 / 张开",
            CylinderPosition.Home => "已缩回 / 夹紧",
            _ => "位置未知"
        };

        public string PositionColor => Position switch
        {
            CylinderPosition.Work => "#52C41A",
            CylinderPosition.Home => "#1890FF",
            _ => "#FAAD14"
        };

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        public bool WorkSensorActive => !string.IsNullOrWhiteSpace(Definition.WorkSensorDiName) && (_io?.ReadDi(Definition.WorkSensorDiName) ?? false);
        public bool HomeSensorActive => !string.IsNullOrWhiteSpace(Definition.HomeSensorDiName) && (_io?.ReadDi(Definition.HomeSensorDiName) ?? false);

        public DelegateCommand MoveWorkCommand { get; }
        public DelegateCommand MoveHomeCommand { get; }

        public CylinderCardVm(CylinderDefinition def, CylinderService service, IIoController? io)
        {
            Definition = def;
            _service = service;
            _io = io;

            MoveWorkCommand = new DelegateCommand(async () => await ExecuteMove(CylinderPosition.Work), () => !IsBusy);
            MoveHomeCommand = new DelegateCommand(async () => await ExecuteMove(CylinderPosition.Home), () => !IsBusy);

            UpdateSensors();
        }

        public void UpdateSensors()
        {
            Position = _service.GetPosition(Definition);
            RaisePropertyChanged(nameof(WorkSensorActive));
            RaisePropertyChanged(nameof(HomeSensorActive));
        }

        private async Task ExecuteMove(CylinderPosition target)
        {
            IsBusy = true;
            try
            {
                var res = await _service.MoveToAsync(Definition, target);
                if (res.Success)
                {
                    Growl.Success($"气缸 '{Name}' {res.Message}");
                }
                else
                {
                    Growl.Error($"气缸 '{Name}' 执行失败: {res.Message}");
                }
                UpdateSensors();
            }
            catch (Exception ex)
            {
                Growl.Error($"执行异常: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    public class TagItemVm : BindableBase
    {
        public DeviceTag Tag { get; }
        public string Name => Tag.Name;
        public string Area => Tag.Area.ToString();
        public ushort Address => Tag.Address;
        public string DataType => Tag.DataType.ToString();
        public bool Writable => Tag.Writable;
        public string Unit => Tag.Unit;

        private double _value;
        public double Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }

        public TagItemVm(DeviceTag tag)
        {
            Tag = tag;
        }
    }

    public class DeviceControlViewModel : BindableBase, INavigationAware, IDisposable
    {
        private readonly CylinderService _cylinderService;
        private readonly PeripheralDeviceService _peripheralService;
        private readonly IIoController? _io;
        private readonly DispatcherTimer _refreshTimer;
        private bool _isDisposed;

        public ObservableCollection<CylinderCardVm> Cylinders { get; } = new();
        public ObservableCollection<PeripheralDeviceConfig> Devices { get; } = new();
        public ObservableCollection<TagItemVm> SelectedDeviceTags { get; } = new();

        private PeripheralDeviceConfig? _selectedDevice;
        public PeripheralDeviceConfig? SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (SetProperty(ref _selectedDevice, value))
                {
                    OnSelectedDeviceChanged();
                }
            }
        }

        private TagItemVm? _selectedTag;
        public TagItemVm? SelectedTag
        {
            get => _selectedTag;
            set => SetProperty(ref _selectedTag, value);
        }

        private double _writeTargetValue;
        public double WriteTargetValue
        {
            get => _writeTargetValue;
            set => SetProperty(ref _writeTargetValue, value);
        }

        private string _pollStatus = "未连接采集（不会自动打开串口）";
        public string PollStatus
        {
            get => _pollStatus;
            set => SetProperty(ref _pollStatus, value);
        }

        public DelegateCommand WriteTagCommand { get; }
        public DelegateCommand RefreshCommand { get; }
        public DelegateCommand ConnectPollCommand { get; }
        public DelegateCommand DisconnectPollCommand { get; }

        public DeviceControlViewModel(
            IIoController? io = null,
            CylinderService? cylinderService = null,
            PeripheralDeviceService? peripheralService = null)
        {
            _io = io;
            _cylinderService = cylinderService ?? new CylinderService(io ?? new DummyIo());
            _peripheralService = peripheralService ?? new PeripheralDeviceService();

            WriteTagCommand = new DelegateCommand(async () => await OnWriteTag(), () => SelectedTag?.Writable == true)
                .ObservesProperty(() => SelectedTag);
            RefreshCommand = new DelegateCommand(OnRefresh);
            ConnectPollCommand = new DelegateCommand(OnConnectPoll, () => SelectedDevice != null).ObservesProperty(() => SelectedDevice);
            DisconnectPollCommand = new DelegateCommand(OnDisconnectPoll);

            _peripheralService.TagUpdated += OnTagUpdated;

            try
            {
                LoadConfigurations();
            }
            catch (Exception ex)
            {
                Growl.Error($"加载外设/气缸档案失败: {ex.Message}");
            }

            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _refreshTimer.Tick += (s, e) =>
            {
                foreach (var cyl in Cylinders)
                {
                    cyl.UpdateSensors();
                }
            };
        }

        private void LoadConfigurations()
        {
            // 加载气缸配置
            var cylStore = new CylinderConfigStore();
            var cylList = cylStore.Load();
            if (cylList.Count == 0)
            {
                cylList = CylinderConfigStore.SeedDefaults();
                cylStore.Save(cylList);
            }
            Cylinders.Clear();
            foreach (var c in cylList)
            {
                Cylinders.Add(new CylinderCardVm(c, _cylinderService, _io));
            }

            // 加载外设配置
            var devStore = new PeripheralDeviceConfigStore();
            var devList = devStore.Load();
            if (devList.Count == 0)
            {
                devList = PeripheralDeviceConfigStore.SeedDefaults();
                devStore.Save(devList);
            }
            Devices.Clear();
            foreach (var d in devList)
            {
                Devices.Add(d);
            }
            SelectedDevice = Devices.FirstOrDefault();
            PollStatus = Devices.Count == 0
                ? "无外设档案，请先到「外设与气缸配置」添加"
                : "已加载档案，点「连接采集」才会打开串口/TCP";
        }

        private void OnConnectPoll()
        {
            if (SelectedDevice == null)
            {
                Growl.Warning("请先选择外设，或到「外设与气缸配置」填写串口/从站");
                return;
            }

            try
            {
                _peripheralService.StartPolling(SelectedDevice);
                PollStatus = $"正在采集：{SelectedDevice.Name}  {SelectedDevice.Transport} {SelectedDevice.PortName}";
                Growl.Success($"已开始采集 {SelectedDevice.Name}");
            }
            catch (Exception ex)
            {
                PollStatus = $"连接失败：{ex.Message}";
                Growl.Error($"打开 {SelectedDevice.PortName} 失败: {ex.Message}");
            }
        }

        private void OnDisconnectPoll()
        {
            try
            {
                _peripheralService.StopAll();
                PollStatus = "已停止采集";
                Growl.Info("已停止全部外设采集");
            }
            catch (Exception ex)
            {
                Growl.Error($"停止采集失败: {ex.Message}");
            }
        }

        private void OnSelectedDeviceChanged()
        {
            SelectedDeviceTags.Clear();
            if (SelectedDevice != null)
            {
                foreach (var tag in SelectedDevice.Tags)
                {
                    var item = new TagItemVm(tag);
                    if (_peripheralService.TryGetTagValue(SelectedDevice.Name, tag.Name, out double val))
                    {
                        item.Value = val;
                    }
                    SelectedDeviceTags.Add(item);
                }
                SelectedTag = SelectedDeviceTags.FirstOrDefault(t => t.Writable);
            }
        }

        private void OnTagUpdated(string devName, string tagName, double val)
        {
            if (SelectedDevice != null && string.Equals(SelectedDevice.Name, devName, StringComparison.OrdinalIgnoreCase))
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    var tagVm = SelectedDeviceTags.FirstOrDefault(t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase));
                    if (tagVm != null)
                    {
                        tagVm.Value = val;
                    }
                });
            }
        }

        private async Task OnWriteTag()
        {
            if (SelectedDevice == null || SelectedTag == null) return;

            var (ok, msg) = await _peripheralService.WriteTagAsync(SelectedDevice, SelectedTag.Name, WriteTargetValue);
            if (ok)
            {
                Growl.Success($"写入成功: {SelectedTag.Name} = {WriteTargetValue} {SelectedTag.Unit}");
                SelectedTag.Value = WriteTargetValue;
            }
            else
            {
                Growl.Error($"写入失败: {msg}");
            }
        }

        private void OnRefresh()
        {
            foreach (var cyl in Cylinders)
            {
                cyl.UpdateSensors();
            }
            OnSelectedDeviceChanged();
        }

        public void OnNavigatedTo(NavigationContext navigationContext)
        {
            try
            {
                LoadConfigurations();
            }
            catch (Exception ex)
            {
                Growl.Error($"刷新外设档案失败: {ex.Message}");
            }
            _refreshTimer.Start();
        }

        public bool IsNavigationTarget(NavigationContext navigationContext) => true;

        public void OnNavigatedFrom(NavigationContext navigationContext)
        {
            _refreshTimer.Stop();
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                _refreshTimer.Stop();
                _peripheralService.TagUpdated -= OnTagUpdated;
                _peripheralService.Dispose();
            }
        }

        private class DummyIo : IIoController
        {
            public bool ReadDi(string pointName) => false;
            public void WriteDo(string pointName, bool value) { }
            public IReadOnlyDictionary<string, bool> SnapshotDi() => new Dictionary<string, bool>();
            public IReadOnlyDictionary<string, bool> SnapshotDo() => new Dictionary<string, bool>();
            public event Action<string, bool>? DiChanged;
            public IReadOnlyList<string> DiPointNames => Array.Empty<string>();
            public IReadOnlyList<string> DoPointNames => Array.Empty<string>();
        }
    }
}
