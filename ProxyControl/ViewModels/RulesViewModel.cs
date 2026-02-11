using ProxyControl.Helpers;
using ProxyControl.Models;
using ProxyControl.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace ProxyControl.ViewModels
{
    public class RulesViewModel : BaseViewModel
    {
        private readonly SettingsService _settingsService;
        private AppConfig _config;

        public ObservableCollection<TrafficRule> RulesList { get; private set; }
        public ObservableCollection<ProxyItem> Proxies { get; private set; }

        public event Action? RulesChanged;
        public event Action<string, string, int>? RequestShowNotification;

        public RulesViewModel(SettingsService settingsService, ObservableCollection<TrafficRule> rules, ObservableCollection<ProxyItem> proxies, AppConfig config)
        {
            _settingsService = settingsService;
            RulesList = rules;
            Proxies = proxies;
            _config = config;

            ConfigureCommands();
        }

        [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(AddRuleCommand), nameof(RemoveRuleCommand), nameof(EditRuleCommand), nameof(OpenRuleModalCommand), nameof(SaveRuleFromModalCommand), nameof(CancelRuleModalCommand), nameof(EditGroupCommand), nameof(RemoveGroupCommand), nameof(EditAppCommand), nameof(RemoveAppCommand), nameof(SelectGroupCommand), nameof(SelectAppCommand), nameof(SelectRuleCommand), nameof(BrowseAppCommand))]
        private void ConfigureCommands()
        {
            AddRuleCommand = new RelayCommand(AddRule);
            RemoveRuleCommand = new RelayCommand(RemoveRule);
            EditRuleCommand = new RelayCommand(OpenRuleModal);

            OpenRuleModalCommand = new RelayCommand(OpenRuleModal);
            SaveRuleFromModalCommand = new RelayCommand(SaveRuleFromModal);
            CancelRuleModalCommand = new RelayCommand(obj => IsModalVisible = false);

            BackToGroupsCommand = new RelayCommand(obj => SelectedGroupName = null);
            BackToAppsCommand = new RelayCommand(obj => SelectedAppName = null);

            EditGroupCommand = new RelayCommand(EditGroup);
            RemoveGroupCommand = new RelayCommand(RemoveGroup);
            EditAppCommand = new RelayCommand(EditApp);
            RemoveAppCommand = new RelayCommand(RemoveApp);

            SelectGroupCommand = new RelayCommand(obj => SelectedGroupName = obj as string);
            SelectAppCommand = new RelayCommand(obj => SelectedAppName = obj as string);
            SelectRuleCommand = new RelayCommand(obj => SelectedRule = obj as TrafficRule);

            BrowseAppCommand = new RelayCommand(obj => BrowseAppFile());
        }

        private void BrowseAppFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Applications|*.exe;*.lnk|Executable Files (*.exe)|*.exe|Shortcut Files (*.lnk)|*.lnk",
                Title = "Select Application or Shortcut"
            };
            if (dialog.ShowDialog() == true)
            {
                if (dialog.FileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var shellType = Type.GetTypeFromProgID("WScript.Shell");
                        if (shellType != null)
                        {
                            dynamic shell = Activator.CreateInstance(shellType)!;
                            var shortcut = shell.CreateShortcut(dialog.FileName);
                            string targetPath = shortcut.TargetPath;
                            if (!string.IsNullOrEmpty(targetPath) && targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                ModalProcessName = System.IO.Path.GetFileNameWithoutExtension(targetPath);
                            }
                        }
                    }
                    catch { }
                }
                else
                {
                    ModalProcessName = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
                }
                _modalIcon = IconHelper.GetIconByProcessName(ModalProcessName);
                OnPropertyChanged(nameof(ModalProcessName)); // Ensure UI updates
            }
        }

        // --- Properties ---

        private TrafficRule? _selectedRule;
        public TrafficRule? SelectedRule
        {
            get => _selectedRule;
            set => SetProperty(ref _selectedRule, value);
        }

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                {
                    RefreshRuleGroups();
                }
            }
        }

        public bool IsBlackListMode => _config.CurrentMode == RuleMode.BlackList;

        // --- Modal Properties ---
        private bool _isModalVisible;
        public bool IsModalVisible
        {
            get => _isModalVisible;
            set => SetProperty(ref _isModalVisible, value);
        }

        private string _modalTitle = "Rule";
        public string ModalTitle
        {
            get => _modalTitle;
            set => SetProperty(ref _modalTitle, value);
        }

        private string _modalSubtitle = "";
        public string ModalSubtitle
        {
            get => _modalSubtitle;
            set => SetProperty(ref _modalSubtitle, value);
        }

        private string _modalProcessName = "";
        public string ModalProcessName
        {
            get => _modalProcessName;
            set => SetProperty(ref _modalProcessName, value);
        }

        private string _modalHost = "";
        public string ModalHost
        {
            get => _modalHost;
            set => SetProperty(ref _modalHost, value);
        }

        private RuleAction _modalAction = RuleAction.Proxy;
        public RuleAction ModalAction
        {
            get => _modalAction;
            set
            {
                if (SetProperty(ref _modalAction, value))
                {
                    OnPropertyChanged(nameof(IsModalProxyRequired));
                }
            }
        }

        public bool IsModalProxyRequired => ModalAction == RuleAction.Proxy;

        private BlockDirection _modalBlockDirection = BlockDirection.Both;
        public BlockDirection ModalBlockDirection
        {
            get => _modalBlockDirection;
            set => SetProperty(ref _modalBlockDirection, value);
        }

        private string _modalGroupName = "General";
        public string ModalGroupName
        {
            get => _modalGroupName;
            set => SetProperty(ref _modalGroupName, value);
        }

        private ProxyItem? _modalSelectedProxy;
        public ProxyItem? ModalSelectedProxy
        {
            get => _modalSelectedProxy;
            set => SetProperty(ref _modalSelectedProxy, value);
        }

        private RuleMode _modalTargetMode;
        public RuleMode ModalTargetMode
        {
            get => _modalTargetMode;
            set => SetProperty(ref _modalTargetMode, value);
        }

        private bool _modalIsScheduleEnabled;
        public bool ModalIsScheduleEnabled
        {
            get => _modalIsScheduleEnabled;
            set => SetProperty(ref _modalIsScheduleEnabled, value);
        }

        private string _modalTimeStart = "";
        public string ModalTimeStart
        {
            get => _modalTimeStart;
            set => SetProperty(ref _modalTimeStart, value);
        }

        private string _modalTimeEnd = "";
        public string ModalTimeEnd
        {
            get => _modalTimeEnd;
            set => SetProperty(ref _modalTimeEnd, value);
        }

        public IEnumerable<string> ExistingGroups => RulesList.Select(r => r.GroupName ?? "General").Distinct().OrderBy(g => g);
        public IEnumerable<RuleAction> ActionTypes => Enum.GetValues(typeof(RuleAction)).Cast<RuleAction>();
        public IEnumerable<BlockDirection> BlockDirectionTypes => Enum.GetValues(typeof(BlockDirection)).Cast<BlockDirection>();
        public IEnumerable<RuleMode> RuleModes => Enum.GetValues(typeof(RuleMode)).Cast<RuleMode>();

        public bool IsEditMode => _editingRule != null;
        private TrafficRule? _editingRule;
        private System.Windows.Media.ImageSource? _modalIcon;

        // --- Commands Properties ---
        public ICommand AddRuleCommand { get; private set; }
        public ICommand RemoveRuleCommand { get; private set; }
        public ICommand EditRuleCommand { get; private set; } // Opens modal
        public ICommand OpenRuleModalCommand { get; private set; }
        public ICommand SaveRuleFromModalCommand { get; private set; }
        public ICommand CancelRuleModalCommand { get; private set; }
        public ICommand BackToGroupsCommand { get; private set; }
        public ICommand BackToAppsCommand { get; private set; }
        public ICommand BrowseAppCommand { get; private set; }

        public ICommand EditGroupCommand { get; private set; }
        public ICommand RemoveGroupCommand { get; private set; }
        public ICommand EditAppCommand { get; private set; }
        public ICommand RemoveAppCommand { get; private set; }
        public ICommand SelectGroupCommand { get; private set; }
        public ICommand SelectAppCommand { get; private set; }
        public ICommand SelectRuleCommand { get; private set; }

        // --- Group/App Selection Logic ---
        private string? _selectedGroupName;
        public string? SelectedGroupName
        {
            get => _selectedGroupName;
            set
            {
                if (SetProperty(ref _selectedGroupName, value))
                {
                    OnPropertyChanged(nameof(IsGroupSelected));
                    OnPropertyChanged(nameof(SelectedGroupApps));
                    OnPropertyChanged(nameof(SelectedGroupRules));
                }
            }
        }
        public bool IsGroupSelected => !string.IsNullOrEmpty(_selectedGroupName);

        private string? _selectedAppName;
        public string? SelectedAppName
        {
            get => _selectedAppName;
            set
            {
                if (SetProperty(ref _selectedAppName, value))
                {
                    OnPropertyChanged(nameof(IsAppSelected));
                    OnPropertyChanged(nameof(SelectedGroupRules));
                }
            }
        }
        public bool IsAppSelected => !string.IsNullOrEmpty(_selectedAppName);

        // --- Logic ---

        // Batch Edit Support
        private bool _isBatchEditMode;
        private string _batchEditTarget = "";
        private string _batchEditValue = "";
        private bool _isRenameGroupMode;
        public bool IsRenameGroupMode
        {
            get => _isRenameGroupMode;
            set => SetProperty(ref _isRenameGroupMode, value);
        }

        public void UpdateConfig(AppConfig config)
        {
            _config = config;
            ReloadRulesForCurrentMode();
        }

        public void ReloadRulesForCurrentMode()
        {
            try
            {
                RulesList.Clear();
                var source = IsBlackListMode ? _config.BlackListRules : _config.WhiteListRules;

                if (source != null)
                {
                    foreach (var r in source)
                    {
                        if (!string.IsNullOrEmpty(r.IconBase64))
                            r.AppIcon = IconHelper.Base64ToImageSource(r.IconBase64);
                        else if (r.TargetApps != null && r.TargetApps.Any())
                        {
                            var i = IconHelper.GetIconByProcessName(r.TargetApps.First());
                            if (i != null) { r.AppIcon = i; r.IconBase64 = IconHelper.ImageSourceToBase64(i); }
                        }
                        SubscribeToItem(r);
                        RulesList.Add(r);
                    }
                }
                RefreshRuleGroups();
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Error("Rules", $"Reload error: {ex.Message}");
            }
        }

        private void SubscribeToItem(INotifyPropertyChanged item)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
            item.PropertyChanged += OnItemPropertyChanged;
        }

        private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // If IsEnabled changes, we might need to update TunService or just save settings
            RulesChanged?.Invoke();
        }

        private void AddRule(object? obj)
        {
            OpenRuleModal(null);
        }

        private void RemoveRule(object? obj)
        {
            var rule = obj as TrafficRule ?? SelectedRule;
            if (rule != null)
            {
                if (IsBlackListMode) _config.BlackListRules.Remove(rule);
                else _config.WhiteListRules.Remove(rule);
                RulesList.Remove(rule);
                RefreshRuleGroups();
                RulesChanged?.Invoke();
            }
        }

        private void OpenRuleModal(object? obj)
        {
            _isBatchEditMode = false;
            _editingRule = null;
            _modalTitle = "Add Rule";
            _modalSubtitle = "";

            TrafficRule? rule = obj as TrafficRule;

            string? prefillProcess = null;
            string? prefillHost = null;

            if (obj is ConnectionHistoryItem historyItem)
            {
                prefillProcess = historyItem.ProcessName;
                prefillHost = historyItem.Host;
            }
            else if (obj is ConnectionLog logItem)
            {
                prefillProcess = logItem.ProcessName;
                prefillHost = logItem.Host;
            }

            if (rule != null)
            {
                _editingRule = rule;
                ModalProcessName = rule.TargetApps?.FirstOrDefault() ?? "";
                ModalHost = rule.TargetHosts?.FirstOrDefault() ?? "";
                ModalAction = rule.Action;
                ModalBlockDirection = rule.BlockDirection;
                ModalGroupName = rule.GroupName ?? "General";
                ModalSelectedProxy = Proxies.FirstOrDefault(p => p.Id == rule.ProxyId);
                _modalIcon = rule.AppIcon;
                ModalTargetMode = _config.BlackListRules.Contains(rule) ? RuleMode.BlackList : RuleMode.WhiteList;

                ModalIsScheduleEnabled = rule.IsScheduleEnabled;
                ModalTimeStart = rule.TimeStart ?? "";
                ModalTimeEnd = rule.TimeEnd ?? "";

                ModalTitle = "Edit Rule";
            }
            else
            {
                // New Rule
                ModalProcessName = !string.IsNullOrEmpty(prefillProcess) ? prefillProcess : (!string.IsNullOrEmpty(_selectedAppName) ? _selectedAppName : "");
                ModalHost = !string.IsNullOrEmpty(prefillHost) ? prefillHost : "";
                ModalAction = RuleAction.Proxy;
                ModalBlockDirection = BlockDirection.Both;
                ModalGroupName = !string.IsNullOrEmpty(_selectedGroupName) ? _selectedGroupName : "QuickRules";
                ModalSelectedProxy = Proxies.FirstOrDefault();
                ModalTargetMode = _config.CurrentMode;
                _modalIcon = !string.IsNullOrEmpty(ModalProcessName) ? IconHelper.GetIconByProcessName(ModalProcessName) : null;

                ModalIsScheduleEnabled = false;
                ModalTimeStart = "";
                ModalTimeEnd = "";
            }

            IsRenameGroupMode = false;
            OnPropertyChanged(nameof(ExistingGroups));
            IsModalVisible = true;
        }

        private void SaveRuleFromModal(object? obj)
        {
            if (!IsRenameGroupMode && ModalIsScheduleEnabled)
            {
                if (ParseTime(ModalTimeStart) == null || ParseTime(ModalTimeEnd) == null)
                {
                    RequestShowNotification?.Invoke("Error", "Invalid schedule time format. Please use HH:mm.", 3000);
                    return;
                }
            }

            if (IsRenameGroupMode)
            {
                if (!string.IsNullOrEmpty(_batchEditValue) && !string.IsNullOrEmpty(ModalGroupName))
                {
                    var rulesToUpdate = RulesList.Where(r => r.GroupName == _batchEditValue).ToList();
                    foreach (var rule in rulesToUpdate)
                    {
                        rule.GroupName = ModalGroupName;
                    }

                    var blackListUpdates = _config.BlackListRules.Where(r => r.GroupName == _batchEditValue).ToList();
                    blackListUpdates.ForEach(r => r.GroupName = ModalGroupName);

                    var whiteListUpdates = _config.WhiteListRules.Where(r => r.GroupName == _batchEditValue).ToList();
                    whiteListUpdates.ForEach(r => r.GroupName = ModalGroupName);
                }

                _isBatchEditMode = false;
                IsRenameGroupMode = false;
                ReloadRulesForCurrentMode();
            }
            else if (_isBatchEditMode)
            {
                IEnumerable<TrafficRule> targets = Enumerable.Empty<TrafficRule>();

                if (_batchEditTarget == "Group")
                {
                    targets = RulesList.Where(r => r.GroupName == _batchEditValue).ToList();
                }
                else if (_batchEditTarget == "App")
                {
                    targets = RulesList.Where(r => r.TargetApps.Contains(_batchEditValue)).ToList();
                }

                foreach (var rule in targets)
                {
                    rule.Action = ModalAction;
                    rule.BlockDirection = ModalBlockDirection;
                    rule.ProxyId = (ModalAction == RuleAction.Proxy && ModalSelectedProxy != null) ? ModalSelectedProxy.Id : null;

                    rule.IsScheduleEnabled = ModalIsScheduleEnabled;
                    rule.TimeStart = ModalTimeStart;
                    rule.TimeEnd = ModalTimeEnd;
                    rule.ScheduleStart = ParseTime(ModalTimeStart);
                    rule.ScheduleEnd = ParseTime(ModalTimeEnd);

                    bool isInBlackList = _config.BlackListRules.Contains(rule);
                    bool isInWhiteList = _config.WhiteListRules.Contains(rule);

                    if (ModalTargetMode == RuleMode.BlackList && !isInBlackList)
                    {
                        _config.WhiteListRules.Remove(rule);
                        _config.BlackListRules.Add(rule);
                    }
                    else if (ModalTargetMode == RuleMode.WhiteList && !isInWhiteList)
                    {
                        _config.BlackListRules.Remove(rule);
                        _config.WhiteListRules.Add(rule);
                    }
                }

                _isBatchEditMode = false;
                ReloadRulesForCurrentMode();
            }
            else if (IsEditMode && _editingRule != null)
            {
                _editingRule.TargetApps = new List<string> { ModalProcessName?.Trim() ?? "" };
                _editingRule.TargetHosts = new List<string> { ModalHost?.Trim() ?? "" };
                _editingRule.Action = ModalAction;
                _editingRule.BlockDirection = ModalBlockDirection;
                _editingRule.GroupName = ModalGroupName?.Trim() ?? "General";
                _editingRule.ProxyId = (ModalAction == RuleAction.Proxy && ModalSelectedProxy != null) ? ModalSelectedProxy.Id : null;
                _editingRule.AppIcon = _modalIcon;
                _editingRule.IconBase64 = _modalIcon != null ? IconHelper.ImageSourceToBase64(_modalIcon) : null;

                _editingRule.IsScheduleEnabled = ModalIsScheduleEnabled;
                _editingRule.TimeStart = ModalTimeStart;
                _editingRule.TimeEnd = ModalTimeEnd;
                _editingRule.ScheduleStart = ParseTime(ModalTimeStart);
                _editingRule.ScheduleEnd = ParseTime(ModalTimeEnd);

                bool isInBlackList = _config.BlackListRules.Contains(_editingRule);
                if (ModalTargetMode == RuleMode.BlackList && !isInBlackList)
                {
                    _config.WhiteListRules.Remove(_editingRule);
                    _config.BlackListRules.Add(_editingRule);
                    if (!IsBlackListMode) RulesList.Remove(_editingRule);
                    else if (IsBlackListMode && !RulesList.Contains(_editingRule)) RulesList.Add(_editingRule);
                }
                else if (ModalTargetMode == RuleMode.WhiteList && isInBlackList)
                {
                    _config.BlackListRules.Remove(_editingRule);
                    _config.WhiteListRules.Add(_editingRule);
                    if (IsBlackListMode) RulesList.Remove(_editingRule);
                    else if (!IsBlackListMode && !RulesList.Contains(_editingRule)) RulesList.Add(_editingRule);
                }
            }
            else
            {
                var apps = ModalProcessName.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                            .Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                var hosts = ModalHost.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                           .Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();

                if (!apps.Any()) apps.Add("*");
                if (!hosts.Any()) hosts.Add("*");

                foreach (var app in apps)
                {
                    var icon = IconHelper.GetIconByProcessName(app);
                    var icon64 = icon != null ? IconHelper.ImageSourceToBase64(icon) : null;

                    foreach (var host in hosts)
                    {
                        var rule = new TrafficRule
                        {
                            TargetApps = new List<string> { app },
                            TargetHosts = new List<string> { host },
                            IsEnabled = true,
                            Action = ModalAction,
                            BlockDirection = ModalBlockDirection,
                            GroupName = ModalGroupName,
                            ProxyId = (ModalAction == RuleAction.Proxy && ModalSelectedProxy != null) ? ModalSelectedProxy.Id : null,
                            AppIcon = icon,
                            IconBase64 = icon64,
                            IsScheduleEnabled = ModalIsScheduleEnabled,
                            TimeStart = ModalTimeStart?.Trim() ?? "",
                            TimeEnd = ModalTimeEnd?.Trim() ?? "",
                            ScheduleStart = ParseTime(ModalTimeStart),
                            ScheduleEnd = ParseTime(ModalTimeEnd),
                            ScheduleDays = new DayOfWeek[0]
                        };

                        if (ModalTargetMode == RuleMode.BlackList) _config.BlackListRules.Add(rule);
                        else _config.WhiteListRules.Add(rule);

                        bool isCurrentModeView = (IsBlackListMode && ModalTargetMode == RuleMode.BlackList) || (!IsBlackListMode && ModalTargetMode == RuleMode.WhiteList);
                        if (isCurrentModeView) RulesList.Add(rule);
                    }
                }
            }

            RulesChanged?.Invoke();
            IsModalVisible = false;
        }

        private TimeSpan? ParseTime(string? t)
        {
            if (string.IsNullOrWhiteSpace(t)) return null;
            if (TimeSpan.TryParse(t.Trim(), out var ts)) return ts;
            if (DateTime.TryParse(t.Trim(), out var dt)) return dt.TimeOfDay;
            return null;
        }

        private void RefreshRuleGroups()
        {
            OnPropertyChanged(nameof(RuleGroups));
            OnPropertyChanged(nameof(SelectedGroupApps));
            OnPropertyChanged(nameof(SelectedGroupRules));
        }

        // Group Logic (Simplified for now)
        private void EditGroup(object? obj)
        {
            string? groupName = obj as string;
            if (groupName == null) return;

            _isBatchEditMode = true;
            IsRenameGroupMode = true;
            _batchEditTarget = "Group";
            _batchEditValue = groupName;

            ModalGroupName = groupName;
            ModalTitle = $"Rename Group '{groupName}'";
            IsModalVisible = true;
        }
        private void RemoveGroup(object? obj)
        {
            string? groupName = obj as string;
            if (groupName == null) return;

            // Confirm delete? For now just delete rules in group
            var groupRules = RulesList.Where(r => r.GroupName == groupName).ToList();
            foreach (var rule in groupRules)
            {
                if (IsBlackListMode) _config.BlackListRules.Remove(rule);
                else _config.WhiteListRules.Remove(rule);
                RulesList.Remove(rule);
            }
            RefreshRuleGroups();
            RulesChanged?.Invoke();
        }
        private void EditApp(object? obj) { /* TODO: Implement */ }
        private void RemoveApp(object? obj) { /* TODO: Implement */ }

        public IEnumerable<RuleGroupInfo> RuleGroups
        {
            get
            {
                if (RulesList == null || RulesList.Count == 0)
                    return Enumerable.Empty<RuleGroupInfo>();

                var source = RulesList.AsEnumerable();
                if (!string.IsNullOrWhiteSpace(_searchText))
                {
                    var s = _searchText.ToLower();
                    source = source.Where(r =>
                        (r.GroupName?.ToLower().Contains(s) == true) ||
                        (r.TargetApps?.Any(a => a.ToLower().Contains(s)) == true) ||
                        (r.TargetHosts?.Any(h => h.ToLower().Contains(s)) == true)
                    );
                }

                return source.GroupBy(r => r.GroupName ?? "General")
                    .Select(g => new RuleGroupInfo
                    {
                        GroupName = g.Key,
                        RuleCount = g.Count(),
                        AppCount = g.SelectMany(r => r.TargetApps ?? new List<string>()).Distinct().Count(),
                        Rules = g.ToList()
                    })
                    .OrderBy(g => g.GroupName);
            }
        }

        public IEnumerable<AppRuleInfo> SelectedGroupApps
        {
            get
            {
                if (string.IsNullOrEmpty(_selectedGroupName) || RulesList == null)
                    return Enumerable.Empty<AppRuleInfo>();

                var query = RulesList.Where(r => (r.GroupName ?? "General") == _selectedGroupName);
                if (!string.IsNullOrWhiteSpace(_searchText))
                {
                    var s = _searchText.ToLower();
                    query = query.Where(r =>
                        (r.TargetApps?.Any(a => a.ToLower().Contains(s)) == true) ||
                        (r.TargetHosts?.Any(h => h.ToLower().Contains(s)) == true)
                    );
                }

                return query
                    .SelectMany(r => r.TargetApps ?? new List<string>())
                    .Distinct()
                    .Select(app => new AppRuleInfo
                    {
                        AppName = app,
                        RuleCount = RulesList.Count(r => (r.GroupName ?? "General") == _selectedGroupName &&
                            (r.TargetApps?.Contains(app) ?? false))
                    })
                    .ToList();
            }
        }

        public IEnumerable<TrafficRule> SelectedGroupRules
        {
            get
            {
                if (string.IsNullOrEmpty(_selectedGroupName) || RulesList == null)
                    return Enumerable.Empty<TrafficRule>();

                var groupRules = RulesList.Where(r => (r.GroupName ?? "General") == _selectedGroupName);

                if (!string.IsNullOrWhiteSpace(_searchText))
                {
                    var s = _searchText.ToLower();
                    groupRules = groupRules.Where(r =>
                        (r.TargetApps?.Any(a => a.ToLower().Contains(s)) == true) ||
                        (r.TargetHosts?.Any(h => h.ToLower().Contains(s)) == true)
                    );
                }

                if (!string.IsNullOrEmpty(_selectedAppName))
                {
                    groupRules = groupRules.Where(r => r.TargetApps?.Contains(_selectedAppName) ?? false);
                }

                return groupRules.ToList();
            }
        }
    }
}
