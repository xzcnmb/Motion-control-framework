#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HandyControl.Controls;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Navigation.Regions;
using Sophon.Contracts;
using Sophon.Infrastructure.Config;
using Sophon.Infrastructure.Motion.Axis;

namespace Sophon.UI.ViewModels
{
    public sealed class CatalogOption<T>
    {
        public CatalogOption(T value, string display)
        {
            Value = value;
            Display = display;
        }

        public T Value { get; }
        public string Display { get; }
        public override string ToString() => Display;
    }

    public class MotionCardConfigViewModel : BindableBase, INavigationAware
    {
        private readonly MotionCardProfileStore _store;
        private readonly AxisManager? _axisManager;
        private bool _syncing;

        public ObservableCollection<MotionCardProfile> Profiles { get; } = new();

        private MotionCardProfile? _selectedProfile;
        public MotionCardProfile? SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                if (SetProperty(ref _selectedProfile, value))
                {
                    OnSelectedProfileChanged();
                }
            }
        }

        private AxisDefinition? _selectedAxis;
        public AxisDefinition? SelectedAxis
        {
            get => _selectedAxis;
            set => SetProperty(ref _selectedAxis, value);
        }

        public ObservableCollection<AxisDefinition> Axes { get; } = new();

        public ObservableCollection<CatalogOption<MotionVendor>> VendorOptions { get; } = new();
        public ObservableCollection<CatalogOption<MotionCommandInterface>> InterfaceOptions { get; } = new();
        public ObservableCollection<CatalogOption<string>> SeriesOptions { get; } = new();
        public ObservableCollection<MotionCardModelDescriptor> ModelOptions { get; } = new();

        private CatalogOption<MotionVendor>? _selectedVendor;
        public CatalogOption<MotionVendor>? SelectedVendor
        {
            get => _selectedVendor;
            set
            {
                if (SetProperty(ref _selectedVendor, value) && !_syncing)
                {
                    RebuildInterfaces(selectFirst: true);
                }
            }
        }

        private CatalogOption<MotionCommandInterface>? _selectedInterface;
        public CatalogOption<MotionCommandInterface>? SelectedInterface
        {
            get => _selectedInterface;
            set
            {
                if (SetProperty(ref _selectedInterface, value) && !_syncing)
                {
                    RebuildSeries(selectFirst: true);
                }
            }
        }

        private CatalogOption<string>? _selectedSeries;
        public CatalogOption<string>? SelectedSeries
        {
            get => _selectedSeries;
            set
            {
                if (SetProperty(ref _selectedSeries, value) && !_syncing)
                {
                    RebuildModels(selectFirst: true);
                }
            }
        }

        private MotionCardModelDescriptor? _selectedModel;
        public MotionCardModelDescriptor? SelectedModel
        {
            get => _selectedModel;
            set
            {
                if (SetProperty(ref _selectedModel, value))
                {
                    if (!_syncing)
                    {
                        ApplySelectedModel();
                    }
                    RaiseModelHints();
                }
            }
        }

        public Array HomingModes => Enum.GetValues(typeof(HomingMode));
        public Array HomeDirections => Enum.GetValues(typeof(HomeDirection));

        private string _validationStatus = "未校验";
        public string ValidationStatus
        {
            get => _validationStatus;
            set => SetProperty(ref _validationStatus, value);
        }

        private string _validationColor = "#8C8C8C";
        public string ValidationColor
        {
            get => _validationColor;
            set => SetProperty(ref _validationColor, value);
        }

        private string _platformHint = string.Empty;
        public string PlatformHint
        {
            get => _platformHint;
            set => SetProperty(ref _platformHint, value);
        }

        private string _adapterWarning = string.Empty;
        public string AdapterWarning
        {
            get => _adapterWarning;
            set => SetProperty(ref _adapterWarning, value);
        }

        public bool ShowAdapterWarning => !string.IsNullOrEmpty(AdapterWarning);
        public bool ShowConnectionString => SelectedModel?.UsesConnectionString == true;
        public bool ShowConfigFile => SelectedModel?.RequiresConfigFile == true;
        public string ConnectionStringLabel => SelectedModel?.HostLink == MotionHostLink.Ethernet
            ? "控制器 IP / 连接字符串"
            : "连接字符串";
        public string ConfigFileLabel => "配置文件路径（固高 GTS 的 *.cfg）";
        public string NativeLibraryText => SelectedModel == null
            ? string.Empty
            : $"SDK：{SelectedModel.NativeLibrary}　主机接口：{MotionCardCatalog.Display(SelectedModel.HostLink)}　最多 {SelectedModel.MaxAxes} 轴";

        public ObservableCollection<string> ValidationMessages { get; } = new();

        public DelegateCommand AddProfileCommand { get; }
        public DelegateCommand DeleteProfileCommand { get; }
        public DelegateCommand AddAxisCommand { get; }
        public DelegateCommand DeleteAxisCommand { get; }
        public DelegateCommand ValidateCommand { get; }
        public DelegateCommand SaveAndApplyCommand { get; }
        public DelegateCommand SetAsActiveCommand { get; }

        public MotionCardConfigViewModel(AxisManager? axisManager = null)
        {
            _axisManager = axisManager;
            _store = new MotionCardProfileStore();

            AddProfileCommand = new DelegateCommand(OnAddProfile);
            DeleteProfileCommand = new DelegateCommand(OnDeleteProfile, () => SelectedProfile != null).ObservesProperty(() => SelectedProfile);
            AddAxisCommand = new DelegateCommand(OnAddAxis, () => SelectedProfile != null).ObservesProperty(() => SelectedProfile);
            DeleteAxisCommand = new DelegateCommand(OnDeleteAxis, () => SelectedAxis != null).ObservesProperty(() => SelectedAxis);
            ValidateCommand = new DelegateCommand(OnValidate);
            SaveAndApplyCommand = new DelegateCommand(OnSaveAndApply);
            SetAsActiveCommand = new DelegateCommand(OnSetAsActive, () => SelectedProfile != null).ObservesProperty(() => SelectedProfile);

            foreach (var vendor in MotionCardCatalog.Vendors)
            {
                VendorOptions.Add(new CatalogOption<MotionVendor>(vendor, MotionCardCatalog.Display(vendor)));
            }

            LoadProfiles();
        }

        private void LoadProfiles()
        {
            Profiles.Clear();
            var list = _store.Load();
            if (list.Count == 0)
            {
                list = MotionCardProfileStore.SeedDefaults();
                _store.Save(list);
            }

            foreach (var p in list)
            {
                Profiles.Add(p);
            }

            var active = _store.GetActive();
            SelectedProfile = (active != null ? Profiles.FirstOrDefault(p => p.ProfileName == active.ProfileName) : null)
                              ?? Profiles.FirstOrDefault();
        }

        private void OnSelectedProfileChanged()
        {
            Axes.Clear();
            if (SelectedProfile != null)
            {
                foreach (var a in SelectedProfile.Axes)
                {
                    Axes.Add(a);
                }
                SelectedAxis = Axes.FirstOrDefault();
            }
            SyncCascadeFromProfile();
            OnValidate();
        }

        private void SyncCascadeFromProfile()
        {
            _syncing = true;
            try
            {
                var profile = SelectedProfile;
                var desc = MotionCardCatalog.Resolve(profile);
                MotionVendor vendor = desc?.Vendor
                    ?? (profile != null && profile.Vendor != MotionVendor.Simulated ? profile.Vendor : MotionVendor.Googol);

                SelectedVendor = VendorOptions.FirstOrDefault(v => v.Value == vendor)
                                 ?? VendorOptions.FirstOrDefault();
                RebuildInterfaces(selectFirst: false);

                MotionCommandInterface iface = desc?.CommandInterface
                    ?? profile?.CommandInterface
                    ?? MotionCommandInterface.Pulse;
                SelectedInterface = InterfaceOptions.FirstOrDefault(i => i.Value == iface)
                                    ?? InterfaceOptions.FirstOrDefault();
                RebuildSeries(selectFirst: false);

                string series = desc?.Series ?? profile?.Series ?? string.Empty;
                SelectedSeries = SeriesOptions.FirstOrDefault(s => string.Equals(s.Value, series, StringComparison.OrdinalIgnoreCase))
                                 ?? SeriesOptions.FirstOrDefault();
                RebuildModels(selectFirst: false);

                SelectedModel = desc != null
                    ? ModelOptions.FirstOrDefault(m => string.Equals(m.Model, desc.Model, StringComparison.OrdinalIgnoreCase))
                    : ModelOptions.FirstOrDefault();

                if (profile != null && SelectedModel != null
                    && (profile.Vendor == MotionVendor.Simulated || string.IsNullOrWhiteSpace(profile.Series)))
                {
                    profile.ApplyModel(SelectedModel);
                }
            }
            finally
            {
                _syncing = false;
                RaiseModelHints();
            }
        }

        private void RebuildInterfaces(bool selectFirst)
        {
            InterfaceOptions.Clear();
            if (SelectedVendor == null)
            {
                return;
            }

            foreach (var iface in MotionCardCatalog.InterfacesFor(SelectedVendor.Value))
            {
                InterfaceOptions.Add(new CatalogOption<MotionCommandInterface>(iface, MotionCardCatalog.Display(iface)));
            }

            if (selectFirst)
            {
                SelectedInterface = InterfaceOptions.FirstOrDefault();
                RebuildSeries(selectFirst: true);
            }
        }

        private void RebuildSeries(bool selectFirst)
        {
            SeriesOptions.Clear();
            if (SelectedVendor == null || SelectedInterface == null)
            {
                return;
            }

            foreach (var series in MotionCardCatalog.SeriesFor(SelectedVendor.Value, SelectedInterface.Value))
            {
                SeriesOptions.Add(new CatalogOption<string>(series, MotionCardCatalog.SeriesDisplay(series)));
            }

            if (selectFirst)
            {
                SelectedSeries = SeriesOptions.FirstOrDefault();
                RebuildModels(selectFirst: true);
            }
        }

        private void RebuildModels(bool selectFirst)
        {
            ModelOptions.Clear();
            if (SelectedVendor == null || SelectedInterface == null || SelectedSeries == null)
            {
                return;
            }

            foreach (var model in MotionCardCatalog.ModelsFor(SelectedVendor.Value, SelectedInterface.Value, SelectedSeries.Value))
            {
                ModelOptions.Add(model);
            }

            if (selectFirst)
            {
                SelectedModel = ModelOptions.FirstOrDefault();
            }
        }

        private void ApplySelectedModel()
        {
            if (SelectedProfile == null || SelectedModel == null)
            {
                return;
            }

            SelectedProfile.ApplyModel(SelectedModel);
            RaisePropertyChanged(nameof(SelectedProfile));
            OnValidate();
        }

        private void RaiseModelHints()
        {
            var model = SelectedModel;
            if (model == null)
            {
                PlatformHint = "请按 厂商 → 脉冲/总线 → 系列 → 型号 选择控制卡。脉冲卡和总线卡不是同一套 SDK。";
                AdapterWarning = string.Empty;
            }
            else
            {
                string accel = model.Accel == AccelParamKind.AccelerationTime
                    ? "加减速按时间（秒）"
                    : "加减速按加速度值";
                string axisBase = $"轴号从 {model.AxisIndexBase} 起算";
                PlatformHint = $"{model.Notes} {axisBase}；{accel}。";
                AdapterWarning = model.IsImplemented
                    ? string.Empty
                    : MotionCardCatalog.NotImplementedMessage(model.Driver, model.Model);
            }

            RaisePropertyChanged(nameof(ShowAdapterWarning));
            RaisePropertyChanged(nameof(ShowConnectionString));
            RaisePropertyChanged(nameof(ShowConfigFile));
            RaisePropertyChanged(nameof(ConnectionStringLabel));
            RaisePropertyChanged(nameof(NativeLibraryText));
        }

        private void OnAddProfile()
        {
            int idx = Profiles.Count + 1;
            var newProfile = new MotionCardProfile
            {
                ProfileName = $"控制卡方案_{idx}",
                CardNo = 0,
                Axes = new List<AxisDefinition>
                {
                    new AxisDefinition { AxisId = 0, Name = "X轴", Unit = "mm", PulsePerUnit = 1000, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500, SoftLimitEnabled = true, SoftLimitMin = -100, SoftLimitMax = 500 },
                    new AxisDefinition { AxisId = 1, Name = "Y轴", Unit = "mm", PulsePerUnit = 1000, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500, SoftLimitEnabled = true, SoftLimitMin = -100, SoftLimitMax = 500 }
                }
            };
            var gts400 = MotionCardCatalog.Find("GTS-400");
            if (gts400 != null)
            {
                newProfile.ApplyModel(gts400);
            }
            Profiles.Add(newProfile);
            SelectedProfile = newProfile;
            Growl.Success($"已创建配置方案 '{newProfile.ProfileName}'");
        }

        private void OnDeleteProfile()
        {
            if (SelectedProfile != null && Profiles.Count > 1)
            {
                string name = SelectedProfile.ProfileName;
                Profiles.Remove(SelectedProfile);
                SelectedProfile = Profiles.FirstOrDefault();
                _store.Save(Profiles.ToList());
                Growl.Info($"已删除方案 '{name}'");
            }
            else
            {
                Growl.Warning("至少需保留一个控制卡配置方案！");
            }
        }

        private void OnAddAxis()
        {
            if (SelectedProfile == null) return;
            int maxAxes = SelectedModel?.MaxAxes ?? 64;
            if (SelectedProfile.Axes.Count >= maxAxes)
            {
                Growl.Warning($"型号 {SelectedModel?.Model} 最多 {maxAxes} 轴，不能再加。");
                return;
            }

            int nextId = SelectedProfile.Axes.Count > 0 ? SelectedProfile.Axes.Max(a => a.AxisId) + 1 : 0;
            var newAxis = new AxisDefinition
            {
                AxisId = nextId,
                Name = $"轴{nextId}",
                Unit = "mm",
                PulsePerUnit = 1000.0,
                MaxSpeed = 100.0,
                MaxAccel = 500.0,
                MaxDecel = 500.0,
                MaxJerk = 2000.0,
                SoftLimitEnabled = true,
                SoftLimitMin = 0.0,
                SoftLimitMax = 300.0
            };
            SelectedProfile.Axes.Add(newAxis);
            Axes.Add(newAxis);
            SelectedAxis = newAxis;
            OnValidate();
        }

        private void OnDeleteAxis()
        {
            if (SelectedProfile != null && SelectedAxis != null)
            {
                SelectedProfile.Axes.Remove(SelectedAxis);
                Axes.Remove(SelectedAxis);
                SelectedAxis = Axes.FirstOrDefault();
                OnValidate();
            }
        }

        private void OnValidate()
        {
            ValidationMessages.Clear();
            if (SelectedProfile == null)
            {
                ValidationStatus = "未选中方案";
                ValidationColor = "#8C8C8C";
                return;
            }

            var errs = MotionProfileValidator.Validate(SelectedProfile);
            if (errs.Count == 0)
            {
                ValidationStatus = "校验通过 (合规)";
                ValidationColor = "#52C41A";
            }
            else
            {
                ValidationStatus = $"校验未通过 ({errs.Count}项不合规)";
                ValidationColor = "#FF4D4F";
                foreach (var err in errs)
                {
                    ValidationMessages.Add(err);
                }
            }
        }

        private void OnSetAsActive()
        {
            if (SelectedProfile == null) return;
            _store.SetActive(SelectedProfile.ProfileName);
            Growl.Success($"已将方案 '{SelectedProfile.ProfileName}' 设为当前运行方案！");
        }

        private void OnSaveAndApply()
        {
            if (SelectedProfile == null) return;

            OnValidate();
            if (ValidationMessages.Count > 0)
            {
                Growl.Warning("当前配置存在不合规项，请先修正后再保存！");
                return;
            }

            SelectedProfile.Axes = Axes.ToList();
            _store.Save(Profiles.ToList());
            _store.SetActive(SelectedProfile.ProfileName);

            Growl.Success($"控制卡与轴参数方案 '{SelectedProfile.ProfileName}' 已成功保存并落盘！");
        }

        public void OnNavigatedTo(NavigationContext navigationContext)
        {
            LoadProfiles();
        }

        public bool IsNavigationTarget(NavigationContext navigationContext) => true;

        public void OnNavigatedFrom(NavigationContext navigationContext)
        {
        }
    }
}
