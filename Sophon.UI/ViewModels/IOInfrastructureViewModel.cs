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
using Sophon.Infrastructure.Config;
using Sophon.Infrastructure.Motion.Axis;

namespace Sophon.UI.ViewModels
{
    public class DiPointVm : BindableBase
    {
        public IoPointDefinition Definition { get; }

        public string LogicalName
        {
            get => Definition.LogicalName;
            set { Definition.LogicalName = value; RaisePropertyChanged(); }
        }

        public int CardNo
        {
            get => Definition.CardNo;
            set { Definition.CardNo = value; RaisePropertyChanged(); }
        }

        public int ChannelBit
        {
            get => Definition.ChannelBit;
            set { Definition.ChannelBit = value; RaisePropertyChanged(); }
        }

        public bool Invert
        {
            get => Definition.Invert;
            set { Definition.Invert = value; RaisePropertyChanged(); }
        }

        public SwitchType Switch
        {
            get => Definition.Switch;
            set { Definition.Switch = value; RaisePropertyChanged(); }
        }

        public int FilterMs
        {
            get => Definition.FilterMs;
            set { Definition.FilterMs = value; RaisePropertyChanged(); }
        }

        public string Category
        {
            get => Definition.Category;
            set { Definition.Category = value; RaisePropertyChanged(); }
        }

        public string Description
        {
            get => Definition.Description;
            set { Definition.Description = value; RaisePropertyChanged(); }
        }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
        }

        public DiPointVm(IoPointDefinition def)
        {
            Definition = def;
        }
    }

    public class DoPointVm : BindableBase
    {
        public IoPointDefinition Definition { get; }
        private readonly IIoController _ioController;

        public string LogicalName
        {
            get => Definition.LogicalName;
            set { Definition.LogicalName = value; RaisePropertyChanged(); }
        }

        public int CardNo
        {
            get => Definition.CardNo;
            set { Definition.CardNo = value; RaisePropertyChanged(); }
        }

        public int ChannelBit
        {
            get => Definition.ChannelBit;
            set { Definition.ChannelBit = value; RaisePropertyChanged(); }
        }

        public bool Invert
        {
            get => Definition.Invert;
            set { Definition.Invert = value; RaisePropertyChanged(); }
        }

        public string Category
        {
            get => Definition.Category;
            set { Definition.Category = value; RaisePropertyChanged(); }
        }

        public string Description
        {
            get => Definition.Description;
            set { Definition.Description = value; RaisePropertyChanged(); }
        }

        private bool _isOutputOn;
        public bool IsOutputOn
        {
            get => _isOutputOn;
            set => SetProperty(ref _isOutputOn, value);
        }

        public DelegateCommand ForceOnCommand { get; }
        public DelegateCommand ForceOffCommand { get; }
        public DelegateCommand PulseCommand { get; }

        public DoPointVm(IoPointDefinition def, IIoController ioController)
        {
            Definition = def;
            _ioController = ioController;

            ForceOnCommand = new DelegateCommand(() => SetOutput(true));
            ForceOffCommand = new DelegateCommand(() => SetOutput(false));
            PulseCommand = new DelegateCommand(async () => await ExecutePulse());
        }

        private void SetOutput(bool state)
        {
            try
            {
                _ioController.WriteDo(LogicalName, state);
                IsOutputOn = state;
            }
            catch (Exception ex)
            {
                Growl.Error($"输出点 '{LogicalName}' 写入异常: {ex.Message}");
            }
        }

        private async Task ExecutePulse()
        {
            try
            {
                _ioController.WriteDo(LogicalName, true);
                IsOutputOn = true;
                await Task.Delay(200);
                _ioController.WriteDo(LogicalName, false);
                IsOutputOn = false;
            }
            catch (Exception ex)
            {
                Growl.Error($"点动脉冲异常: {ex.Message}");
            }
        }
    }

    public class CylinderSettingVm : BindableBase
    {
        public CylinderDefinition Definition { get; }

        public string Name
        {
            get => Definition.Name;
            set { Definition.Name = value; RaisePropertyChanged(); }
        }

        public ValveType Valve
        {
            get => Definition.Valve;
            set { Definition.Valve = value; RaisePropertyChanged(); }
        }

        public string WorkDoName
        {
            get => Definition.WorkDoName;
            set { Definition.WorkDoName = value; RaisePropertyChanged(); }
        }

        public string? HomeDoName
        {
            get => Definition.HomeDoName;
            set { Definition.HomeDoName = value; RaisePropertyChanged(); }
        }

        public string? WorkSensorDiName
        {
            get => Definition.WorkSensorDiName;
            set { Definition.WorkSensorDiName = value; RaisePropertyChanged(); }
        }

        public string? HomeSensorDiName
        {
            get => Definition.HomeSensorDiName;
            set { Definition.HomeSensorDiName = value; RaisePropertyChanged(); }
        }

        public int PulseWidthMs
        {
            get => Definition.PulseWidthMs;
            set { Definition.PulseWidthMs = value; RaisePropertyChanged(); }
        }

        public int ConfirmTimeoutMs
        {
            get => Definition.ConfirmTimeoutMs;
            set { Definition.ConfirmTimeoutMs = value; RaisePropertyChanged(); }
        }

        public string? InterlockGroup
        {
            get => Definition.InterlockGroup;
            set { Definition.InterlockGroup = value; RaisePropertyChanged(); }
        }

        public bool ResetToHomeOnStart
        {
            get => Definition.ResetToHomeOnStart;
            set { Definition.ResetToHomeOnStart = value; RaisePropertyChanged(); }
        }

        public CylinderSettingVm(CylinderDefinition def)
        {
            Definition = def;
        }
    }

    public class IOInfrastructureViewModel : BindableBase, INavigationAware, IDisposable
    {
        private readonly IoPointConfigStore _ioStore;
        private readonly CylinderConfigStore _cylStore;
        private readonly IIoController _ioController;
        private readonly DispatcherTimer _scanTimer;
        private bool _isDisposed;

        public ObservableCollection<DiPointVm> DiPoints { get; } = new();
        public ObservableCollection<DoPointVm> DoPoints { get; } = new();
        public ObservableCollection<CylinderSettingVm> Cylinders { get; } = new();

        public ObservableCollection<string> AvailableDiNames { get; } = new();
        public ObservableCollection<string> AvailableDoNames { get; } = new();

        public Array SwitchTypes => Enum.GetValues(typeof(SwitchType));
        public Array ValveTypes => Enum.GetValues(typeof(ValveType));

        private DiPointVm? _selectedDi;
        public DiPointVm? SelectedDi
        {
            get => _selectedDi;
            set => SetProperty(ref _selectedDi, value);
        }

        private DoPointVm? _selectedDo;
        public DoPointVm? SelectedDo
        {
            get => _selectedDo;
            set => SetProperty(ref _selectedDo, value);
        }

        private CylinderSettingVm? _selectedCylinder;
        public CylinderSettingVm? SelectedCylinder
        {
            get => _selectedCylinder;
            set => SetProperty(ref _selectedCylinder, value);
        }

        // 命令
        public DelegateCommand AddDiCommand { get; }
        public DelegateCommand DeleteDiCommand { get; }
        public DelegateCommand SaveIoCommand { get; }

        public DelegateCommand AddDoCommand { get; }
        public DelegateCommand DeleteDoCommand { get; }

        public DelegateCommand AddCylinderCommand { get; }
        public DelegateCommand DeleteCylinderCommand { get; }
        public DelegateCommand SaveCylindersCommand { get; }

        public IOInfrastructureViewModel(IIoController? ioController = null)
        {
            _ioStore = new IoPointConfigStore();
            _cylStore = new CylinderConfigStore();
            _ioController = ioController ?? new IoMappingManager(_ioStore);

            AddDiCommand = new DelegateCommand(OnAddDi);
            DeleteDiCommand = new DelegateCommand(OnDeleteDi, () => SelectedDi != null).ObservesProperty(() => SelectedDi);
            SaveIoCommand = new DelegateCommand(OnSaveIo);

            AddDoCommand = new DelegateCommand(OnAddDo);
            DeleteDoCommand = new DelegateCommand(OnDeleteDo, () => SelectedDo != null).ObservesProperty(() => SelectedDo);

            AddCylinderCommand = new DelegateCommand(OnAddCylinder);
            DeleteCylinderCommand = new DelegateCommand(OnDeleteCylinder, () => SelectedCylinder != null).ObservesProperty(() => SelectedCylinder);
            SaveCylindersCommand = new DelegateCommand(OnSaveCylinders);

            LoadAllData();

            _scanTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(80)
            };
            _scanTimer.Tick += (s, e) => ScanIoStates();
            _scanTimer.Start();
        }

        private void LoadAllData()
        {
            // 加载 IO 配置
            var ioList = _ioStore.Load();
            if (ioList.Count == 0)
            {
                ioList = IoPointConfigStore.SeedDefaults();
                _ioStore.Save(ioList);
            }

            DiPoints.Clear();
            DoPoints.Clear();
            AvailableDiNames.Clear();
            AvailableDoNames.Clear();

            // 增加空选项供下拉选用
            AvailableDiNames.Add("");
            AvailableDoNames.Add("");

            foreach (var p in ioList)
            {
                if (p.Direction == IoDirection.DI)
                {
                    DiPoints.Add(new DiPointVm(p));
                    AvailableDiNames.Add(p.LogicalName);
                }
                else
                {
                    DoPoints.Add(new DoPointVm(p, _ioController));
                    AvailableDoNames.Add(p.LogicalName);
                }
            }

            SelectedDi = DiPoints.FirstOrDefault();
            SelectedDo = DoPoints.FirstOrDefault();

            // 加载气缸配置
            var cylList = _cylStore.Load();
            if (cylList.Count == 0)
            {
                cylList = CylinderConfigStore.SeedDefaults();
                _cylStore.Save(cylList);
            }

            Cylinders.Clear();
            foreach (var c in cylList)
            {
                Cylinders.Add(new CylinderSettingVm(c));
            }
            SelectedCylinder = Cylinders.FirstOrDefault();
        }

        private void ScanIoStates()
        {
            try
            {
                foreach (var di in DiPoints)
                {
                    di.IsActive = _ioController.ReadDi(di.LogicalName);
                }

                var doSnap = _ioController.SnapshotDo();
                foreach (var doItem in DoPoints)
                {
                    doItem.IsOutputOn = doSnap.TryGetValue(doItem.LogicalName, out var val) && val;
                }
            }
            catch { }
        }

        private void OnAddDi()
        {
            int idx = DiPoints.Count;
            var def = new IoPointDefinition
            {
                LogicalName = $"DI_CUSTOM_{idx}",
                Direction = IoDirection.DI,
                CardNo = 0,
                ChannelBit = idx,
                Switch = SwitchType.NormallyOpen,
                FilterMs = 20,
                Category = "自定义输入",
                Description = $"自定义数字量输入点 {idx}"
            };
            var vm = new DiPointVm(def);
            DiPoints.Add(vm);
            AvailableDiNames.Add(def.LogicalName);
            SelectedDi = vm;
        }

        private void OnDeleteDi()
        {
            if (SelectedDi != null)
            {
                AvailableDiNames.Remove(SelectedDi.LogicalName);
                DiPoints.Remove(SelectedDi);
                SelectedDi = DiPoints.FirstOrDefault();
            }
        }

        private void OnAddDo()
        {
            int idx = DoPoints.Count;
            var def = new IoPointDefinition
            {
                LogicalName = $"DO_CUSTOM_{idx}",
                Direction = IoDirection.DO,
                CardNo = 0,
                ChannelBit = idx,
                Category = "自定义输出",
                Description = $"自定义数字量输出点 {idx}"
            };
            var vm = new DoPointVm(def, _ioController);
            DoPoints.Add(vm);
            AvailableDoNames.Add(def.LogicalName);
            SelectedDo = vm;
        }

        private void OnDeleteDo()
        {
            if (SelectedDo != null)
            {
                AvailableDoNames.Remove(SelectedDo.LogicalName);
                DoPoints.Remove(SelectedDo);
                SelectedDo = DoPoints.FirstOrDefault();
            }
        }

        private void OnSaveIo()
        {
            var all = new List<IoPointDefinition>();
            all.AddRange(DiPoints.Select(d => d.Definition));
            all.AddRange(DoPoints.Select(d => d.Definition));

            // 校验点名唯一性
            var dup = all.GroupBy(x => x.LogicalName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (dup != null)
            {
                Growl.Warning($"保存失败：存在重复的逻辑点名 '{dup.Key}'！");
                return;
            }

            _ioStore.Save(all);
            if (_ioController is IoMappingManager mgr)
            {
                mgr.Reload();
            }

            Growl.Success($"IO 点位映射配置已成功保存并落盘 (共 {all.Count} 点)！");
        }

        private void OnAddCylinder()
        {
            int idx = Cylinders.Count + 1;
            var def = new CylinderDefinition
            {
                Name = $"气缸_{idx}",
                Valve = ValveType.DoubleCoil,
                WorkDoName = AvailableDoNames.FirstOrDefault(d => !string.IsNullOrEmpty(d)) ?? "",
                HomeDoName = AvailableDoNames.Skip(1).FirstOrDefault(d => !string.IsNullOrEmpty(d)) ?? "",
                PulseWidthMs = 200,
                ConfirmTimeoutMs = 1500
            };
            var vm = new CylinderSettingVm(def);
            Cylinders.Add(vm);
            SelectedCylinder = vm;
        }

        private void OnDeleteCylinder()
        {
            if (SelectedCylinder != null)
            {
                Cylinders.Remove(SelectedCylinder);
                SelectedCylinder = Cylinders.FirstOrDefault();
            }
        }

        private void OnSaveCylinders()
        {
            var list = Cylinders.Select(c => c.Definition).ToList();
            _cylStore.Save(list);
            Growl.Success($"气缸与执行机构配置已成功保存落盘 (共 {list.Count} 个气缸)！");
        }

        public void OnNavigatedTo(NavigationContext navigationContext)
        {
            LoadAllData();
            _scanTimer.Start();
        }

        public bool IsNavigationTarget(NavigationContext navigationContext) => true;

        public void OnNavigatedFrom(NavigationContext navigationContext)
        {
            _scanTimer.Stop();
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                _scanTimer.Stop();
            }
        }
    }
}
