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
    /// <summary>级联下拉选项（值 + 中文显示文本）。</summary>
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
        private AxisManager? _axisManager;
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

        // 厂商 → 脉冲/总线 → 系列 → 型号 四级级联
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
                    if (value?.Value == MotionVendor.Simulated)
                    {
                        ApplySimulatedVendor();
                        return;
                    }

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

        private string _selectionSummary = "当前卡型：未选择　主机接口：—　适配状态：请按「厂商 → 接口类型 → 系列 → 型号」选择控制卡";

        /// <summary>
        /// 只读中文摘要：当前卡型 / 主机接口 / 适配状态。措辞全部取自
        /// <see cref="MotionCardCatalog.Display(...)"/>，不另建名词表、不改变目录匹配。
        /// </summary>
        public string SelectionSummary
        {
            get => _selectionSummary;
            private set => SetProperty(ref _selectionSummary, value);
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
        public bool HasValidationMessages => ValidationMessages.Count > 0;
        public string ConnectionStringLabel => SelectedModel?.HostLink == MotionHostLink.Ethernet
            ? "控制器 IP / 连接字符串"
            : "连接字符串";
        public string ConfigFileLabel => "配置文件路径（固高 GTS 的 *.cfg）";
        public string NativeLibraryText => SelectedModel == null
            ? (SelectedProfile?.Driver == DriverKind.Simulated
                ? "驱动：仿真控制器（无板卡，不加载任何厂商 SDK）"
                : string.Empty)
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

        /// <summary>
        /// 用档案反推四级级联选中项。旧档案只有 Driver + CardModel 时也能回显。
        /// </summary>
        private void SyncCascadeFromProfile()
        {
            _syncing = true;
            try
            {
                var profile = SelectedProfile;

                // 纯仿真方案（目录里没有对应控制卡型号）不强绑任何真卡型号：厂商选「仿真控制器（无板卡）」、
                // 型号列表留空，避免"先清空型号再取目录第一个"把旧档案改写成 GTS/DMC。
                if (profile != null && profile.Driver == DriverKind.Simulated && MotionCardCatalog.Resolve(profile) == null)
                {
                    SelectedVendor = VendorOptions.FirstOrDefault(v => v.Value == MotionVendor.Simulated);
                    InterfaceOptions.Clear();
                    SeriesOptions.Clear();
                    ModelOptions.Clear();
                    SelectedInterface = null;
                    SelectedSeries = null;
                    SelectedModel = null;
                    return;
                }

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

                // 型号是主路径：能对上目录就按当前选中的目录型号归一化（驱动、接口、系列、平台选项一起回填）。
                // 型号非空却不在目录时 SelectedModel 留空、档案保持原样，只让校验提示用户重新选择，
                // 禁止改写目录第一个型号。
                SelectedModel = desc != null
                    ? ModelOptions.FirstOrDefault(m => string.Equals(m.Model, desc.Model, StringComparison.OrdinalIgnoreCase))
                    : null;

                if (profile != null && desc != null
                    && !string.Equals(profile.CardModel, desc.Model, StringComparison.OrdinalIgnoreCase))
                {
                    profile.ApplyModel(desc);
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

        /// <summary>
        /// 选「仿真控制器（无板卡）」：不绑任何真实控制卡型号，也不要 SDK / cfg / 连接字符串，
        /// 按 Driver=Simulated 落盘即可离线跑通 Sim 链路。
        /// </summary>
        private void ApplySimulatedVendor()
        {
            _syncing = true;
            try
            {
                // 仿真没有真实的「脉冲/总线接口 → 系列 → 型号」，后三级级联留空。
                InterfaceOptions.Clear();
                SeriesOptions.Clear();
                ModelOptions.Clear();
                SelectedInterface = null;
                SelectedSeries = null;
                SelectedModel = null;
            }
            finally
            {
                _syncing = false;
            }

            var profile = SelectedProfile;
            if (profile != null)
            {
                profile.Driver = DriverKind.Simulated;
                profile.Vendor = MotionVendor.Simulated;
                profile.CommandInterface = MotionCommandInterface.Pulse;
                profile.Series = string.Empty;
                profile.CardModel = string.Empty;
                profile.ConnectionString = null;
                profile.ConfigFilePath = null;
                profile.Platform = MotionCardProfile.DefaultPlatformFor(DriverKind.Simulated);
                RaisePropertyChanged(nameof(SelectedProfile));
            }

            OnValidate();
            RaiseModelHints();
        }

        /// <summary>
        /// 型号确定后由型号反填驱动、接口、系列与平台选项（Platform 来自型号，而不是只看品牌）。
        /// </summary>
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
                PlatformHint = SelectedProfile?.Driver == DriverKind.Simulated
                    ? "当前为仿真控制器（无板卡）方案：不需要真实型号、cfg 与连接字符串，可离线跑通仿真链路。按「厂商 → 脉冲/总线 → 系列 → 型号」选择即可接入真实控制卡。"
                    : "请按「厂商 → 脉冲/总线 → 系列 → 型号」选择控制卡。脉冲卡和总线卡不是同一套 SDK，不能混用。";
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
            SelectionSummary = BuildSelectionSummary();
        }

        /// <summary>
        /// 组「当前卡型 / 主机接口 / 适配状态」中文摘要。
        /// 卡型与主机接口直接复用 MotionCardCatalog.Display，保证和级联下拉、驱动提示用词一致。
        /// </summary>
        private string BuildSelectionSummary()
        {
            var model = SelectedModel;
            if (model == null)
            {
                // 与 SyncCascadeFromProfile 同一判定：纯仿真方案（目录里没有对应控制卡型号）不绑真卡型号。
                if (SelectedProfile?.Driver == DriverKind.Simulated
                    && MotionCardCatalog.Resolve(SelectedProfile) == null)
                {
                    return "当前卡型：仿真控制器（无板卡）　主机接口：仿真　适配状态：离线仿真可用，不加载任何厂商 SDK";
                }

                if (!string.IsNullOrWhiteSpace(SelectedProfile?.CardModel))
                {
                    return $"当前卡型：未选择　主机接口：—　适配状态：档案型号 {SelectedProfile.CardModel} 不在选型目录中，请重新选择";
                }

                return "当前卡型：未选择　主机接口：—　适配状态：请按「厂商 → 接口类型 → 系列 → 型号」选择控制卡";
            }

            string adapter = model.IsImplemented
                ? "已接入适配器（未经真机验证）"
                : "未接入适配器（禁止当脉冲卡打开）";

            return $"当前卡型：{MotionCardCatalog.Display(model.CommandInterface)}"
                 + $"　主机接口：{MotionCardCatalog.Display(model.HostLink)}"
                 + $"　适配状态：{adapter}"
                 + $"　适配驱动：{MotionCardCatalog.Display(model.Driver)}";
        }

        private void OnAddProfile()
        {
            var newProfile = new MotionCardProfile
            {
                // 新方案默认「仿真控制器（无板卡）」：离线可保存、可运行，不硬编码任何真实控制卡型号。
                ProfileName = NextProfileName(),
                Driver = DriverKind.Simulated,
                Vendor = MotionVendor.Simulated,
                CommandInterface = MotionCommandInterface.Pulse,
                Series = string.Empty,
                CardModel = string.Empty,
                CardNo = 0,
                ConnectionString = null,
                ConfigFilePath = null,
                Platform = MotionCardProfile.DefaultPlatformFor(DriverKind.Simulated),
                Axes = new List<AxisDefinition>
                {
                    new AxisDefinition { AxisId = 0, Name = "X轴", Unit = "mm", PulsePerUnit = 1000, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500, SoftLimitEnabled = true, SoftLimitMin = -100, SoftLimitMax = 500 },
                    new AxisDefinition { AxisId = 1, Name = "Y轴", Unit = "mm", PulsePerUnit = 1000, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500, SoftLimitEnabled = true, SoftLimitMin = -100, SoftLimitMax = 500 }
                }
            };
            Profiles.Add(newProfile);
            SelectedProfile = newProfile;
            Growl.Success($"已创建配置方案 '{newProfile.ProfileName}'");
        }

        /// <summary>新建方案名取最小未占用序号：删过方案之后重用序号也不会重名。</summary>
        private string NextProfileName()
        {
            int idx = 1;
            while (Profiles.Any(p => string.Equals(p.ProfileName, $"控制卡方案_{idx}", StringComparison.OrdinalIgnoreCase)))
            {
                idx++;
            }

            return $"控制卡方案_{idx}";
        }

        private void OnDeleteProfile()
        {
            if (SelectedProfile == null)
            {
                return;
            }

            // 至少需保留一个控制卡配置方案。
            if (Profiles.Count <= 1)
            {
                Growl.Warning("至少需保留一个控制卡配置方案！");
                return;
            }

            var victim = SelectedProfile;
            string name = victim.ProfileName;
            int index = Profiles.IndexOf(victim);

            // 删除前记下磁盘上的当前 active 名称，删除后要保证它仍然指向剩余档案。
            string? activeNameBefore = _store.GetActive()?.ProfileName;

            // 先摘掉待删方案，再对剩余方案跑同一套校验：
            // 只要还有一个剩余方案不合规（未接入总线 / 未知型号 / 轴参数非法），就恢复原列表与选中项、不落盘。
            // 只增删集合、不改任何档案字段，因此不会把总线卡静默改写成仿真卡。
            Profiles.RemoveAt(index);
            var remainingInvalid = Profiles.FirstOrDefault(p => MotionProfileValidator.Validate(p).Count > 0);
            if (remainingInvalid != null)
            {
                Profiles.Insert(index, victim);
                Growl.Warning($"不能删除 '{name}'：删除后剩余方案 '{remainingInvalid.ProfileName}' 仍存在不合规项，请先修正该方案再删除！");
                return;
            }

            var remaining = Profiles.ToList();
            _store.Save(remaining);

            // 被删的正是 active、或 active 已不在剩余档案中时，落到第一条剩余方案，避免留下悬空 active 名称。
            bool activeDangling = string.IsNullOrWhiteSpace(activeNameBefore)
                || string.Equals(activeNameBefore, name, StringComparison.OrdinalIgnoreCase)
                || !remaining.Any(p => string.Equals(p.ProfileName, activeNameBefore, StringComparison.OrdinalIgnoreCase));
            if (activeDangling)
            {
                _store.SetActive(remaining[0].ProfileName);
            }

            SelectedProfile = Profiles.FirstOrDefault();
            Growl.Info($"已删除方案 '{name}'");
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
                RaisePropertyChanged(nameof(HasValidationMessages));
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

            RaisePropertyChanged(nameof(HasValidationMessages));
        }

        private void OnSetAsActive()
        {
            if (SelectedProfile == null) return;

            // 先把当前内存档案落盘（含级联选中的型号与轴参数），再校验，最后才允许激活；
            // 校验不通或型号未接入一律拒绝，不静默改写，也不回退 Sim。
            if (SelectedModel != null)
            {
                SelectedProfile.ApplyModel(SelectedModel);
                RaisePropertyChanged(nameof(SelectedProfile));
            }

            SelectedProfile.Axes = Axes.ToList();

            OnValidate();
            if (ValidationMessages.Count > 0)
            {
                Growl.Warning("当前配置存在不合规项，请先修正后再设为当前方案！");
                return;
            }

            // 未接入的总线/ZMC 型号不允许设为运行方案：宁可拒绝，也不能让启动时静默换成脉冲卡。
            var model = MotionCardCatalog.Find(SelectedProfile.CardModel);
            if (model != null && !model.IsImplemented)
            {
                Growl.Error(MotionCardCatalog.NotImplementedMessage(model.Driver, model.Model));
                return;
            }

            _store.Save(Profiles.ToList());
            _store.SetActive(SelectedProfile.ProfileName);
            Growl.Success($"已将方案 '{SelectedProfile.ProfileName}' 设为当前运行方案，保存成功，重启后生效！（运行中的运动控制器是启动时创建的单例，不会热切换）");
        }

        private void OnSaveAndApply()
        {
            if (SelectedProfile == null) return;

            // 先把级联选中的型号应用到档案，再重新校验，最后才落盘——避免旧字段残留绕过校验。
            if (SelectedModel != null)
            {
                SelectedProfile.ApplyModel(SelectedModel);
                RaisePropertyChanged(nameof(SelectedProfile));
            }

            SelectedProfile.Axes = Axes.ToList();

            OnValidate();
            if (ValidationMessages.Count > 0)
            {
                Growl.Warning("当前配置存在不合规项，请先修正后再保存！");
                return;
            }

            // 未接入的总线/ZMC 型号不允许保存为运行方案：宁可拒绝，也不能让启动时静默换成脉冲卡。
            var model = MotionCardCatalog.Find(SelectedProfile.CardModel);
            if (model != null && !model.IsImplemented)
            {
                Growl.Error(MotionCardCatalog.NotImplementedMessage(model.Driver, model.Model));
                return;
            }

            _store.Save(Profiles.ToList());
            _store.SetActive(SelectedProfile.ProfileName);

            Growl.Success($"控制卡与轴参数方案 '{SelectedProfile.ProfileName}' 保存成功，重启后生效！（运行中的运动控制器是启动时创建的单例，不会热切换）");
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
