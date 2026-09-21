#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
    public class MotionCardConfigViewModel : BindableBase, INavigationAware
    {
        private readonly MotionCardProfileStore _store;
        private readonly AxisManager? _axisManager;

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

        public DriverKind[] DriverKinds { get; } =
        {
            DriverKind.GoogolGts,
            DriverKind.LeadShineDmc
        };
        public Array AccelKinds => Enum.GetValues(typeof(AccelParamKind));
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

        public ObservableCollection<string> ValidationMessages { get; } = new();

        public DelegateCommand AddProfileCommand { get; }
        public DelegateCommand DeleteProfileCommand { get; }
        public DelegateCommand AddAxisCommand { get; }
        public DelegateCommand DeleteAxisCommand { get; }
        public DelegateCommand ValidateCommand { get; }
        public DelegateCommand SaveAndApplyCommand { get; }
        public DelegateCommand SetAsActiveCommand { get; }
        public DelegateCommand DriverChangedCommand { get; }

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
            DriverChangedCommand = new DelegateCommand(OnDriverChanged);

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
            OnValidate();
        }

        private void OnDriverChanged()
        {
            if (SelectedProfile != null)
            {
                SelectedProfile.Platform = MotionCardProfile.DefaultPlatformFor(SelectedProfile.Driver);
                RaisePropertyChanged(nameof(SelectedProfile));
                OnValidate();
            }
        }

        private void OnAddProfile()
        {
            int idx = Profiles.Count + 1;
            var newProfile = new MotionCardProfile
            {
                ProfileName = $"控制卡方案_{idx}",
                Driver = DriverKind.GoogolGts,
                CardModel = "GTS-400",
                CardNo = 0,
                Platform = MotionCardProfile.DefaultPlatformFor(DriverKind.GoogolGts),
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

            // 同步 Axes 回当前 Profile
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
