#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Linq;
using HandyControl.Controls;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Navigation.Regions;
using Sophon.Contracts;
using Sophon.Infrastructure.Config;
using Sophon.UI.Vision;

namespace Sophon.UI.ViewModels.Vision
{
    /// <summary>
    /// 相机连接与采集配置页视图模型。
    /// 管理相机配置列表（新增/删除/保存），并将选中配置应用到运行时视觉提供者。
    /// </summary>
    public class CameraConfigViewModel : BindableBase, INavigationAware
    {
        private readonly CameraConfigStore _store;
        private readonly SwitchableVisionProvider _switchable;

        public ObservableCollection<CameraConfig> Configs { get; } = new();

        private CameraConfig? _selectedConfig;
        public CameraConfig? SelectedConfig
        {
            get => _selectedConfig;
            set => SetProperty(ref _selectedConfig, value);
        }

        public CameraVendor[] VendorKinds { get; } =
        {
            CameraVendor.HikvisionMvs,
            CameraVendor.GenICam
        };
        public Array TriggerModes => Enum.GetValues(typeof(CameraTriggerMode));

        public DelegateCommand AddConfigCommand { get; }
        public DelegateCommand DeleteConfigCommand { get; }
        public DelegateCommand SaveCommand { get; }
        public DelegateCommand ApplyCommand { get; }

        public CameraConfigViewModel(CameraConfigStore store, SwitchableVisionProvider switchable)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _switchable = switchable ?? throw new ArgumentNullException(nameof(switchable));

            AddConfigCommand = new DelegateCommand(OnAddConfig);
            DeleteConfigCommand = new DelegateCommand(OnDeleteConfig, () => SelectedConfig != null).ObservesProperty(() => SelectedConfig);
            SaveCommand = new DelegateCommand(OnSave);
            ApplyCommand = new DelegateCommand(OnApply, () => SelectedConfig != null).ObservesProperty(() => SelectedConfig);
        }

        private void LoadConfigs()
        {
            Configs.Clear();
            var list = _store.Load();
            if (list.Count == 0)
            {
                list = CameraConfigStore.SeedDefaults();
                _store.Save(list);
            }

            foreach (var c in list)
            {
                Configs.Add(c);
            }

            SelectedConfig = Configs.FirstOrDefault();
        }

        private void OnAddConfig()
        {
            var cfg = CameraConfigStore.SeedDefaults()[0];
            cfg.Name = $"相机{Configs.Count + 1}";
            Configs.Add(cfg);
            SelectedConfig = cfg;
            Growl.Success($"已新增相机配置 '{cfg.Name}'");
        }

        private void OnDeleteConfig()
        {
            if (SelectedConfig == null) return;

            if (Configs.Count <= 1)
            {
                Growl.Warning("至少需保留一个相机配置！");
                return;
            }

            string name = SelectedConfig.Name;
            Configs.Remove(SelectedConfig);
            SelectedConfig = Configs.FirstOrDefault();
            Growl.Info($"已删除相机配置 '{name}'");
        }

        private void OnSave()
        {
            if (SelectedConfig == null)
            {
                Growl.Warning("请先选择要保存的相机配置");
                return;
            }

            _store.Save(Configs.ToList());
            Growl.Success("相机配置已保存");
        }

        private void OnApply()
        {
            if (SelectedConfig == null)
            {
                Growl.Warning("请先选择要应用的相机配置");
                return;
            }

            try
            {
                // 先保存，再应用到运行时提供者
                _store.Save(Configs.ToList());
                _switchable.Apply(SelectedConfig);
                Growl.Success($"已保存并应用相机配置 '{SelectedConfig.Name}'");
            }
            catch (Exception ex)
            {
                Growl.Error($"应用相机配置失败: {ex.Message}");
            }
        }

        public void OnNavigatedTo(NavigationContext navigationContext)
        {
            LoadConfigs();
        }

        public bool IsNavigationTarget(NavigationContext navigationContext) => true;

        public void OnNavigatedFrom(NavigationContext navigationContext)
        {
        }
    }
}
