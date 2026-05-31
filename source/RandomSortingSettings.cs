using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace RandomSorting
{
    public enum LabelType
    {
        Category,
        Tag
    }

    public enum AutomaticRunBehavior
    {
        SkipIfRunning,
        CancelCurrentAndRestart,
        QueueOneRerunAfterCurrent
    }

    public class FeatureFilterOption
    {
        public Guid? Id { get; set; }
        public string Name { get; set; }
    }

    public class AutomaticRunBehaviorOption
    {
        public AutomaticRunBehavior Value { get; set; }
        public string Name { get; set; }
    }

    public class RandomSortingSettings : ObservableObject
    {
        private bool updateOnStartup = false;
        private bool updateOnGameStart = false;
        private LabelType selectedLabelType = LabelType.Tag;
        private string randomPrefix = "z[Random] ";
        private bool includeUninstalledGames = false;
        private bool includeHiddenGames = false;
        private Guid? requiredFeatureId = null;
        private AutomaticRunBehavior automaticRunBehavior = AutomaticRunBehavior.CancelCurrentAndRestart;
        private bool cleanupOrphanedRandomLabels = true;
        private bool cleanupOnAutomaticRuns = false;
        private bool useCollisionFreeLabels = true;
        private bool detailedLogging = false;
        private int databaseUpdateBatchSize = 500;
        private int automaticDatabaseUpdateBatchSize = 500;
        private int uiBatchDelayMs = 10;
        private int maxAutomaticCleanupLabels = 1000;

        public bool UpdateOnStartup
        {
            get => updateOnStartup;
            set => SetValue(ref updateOnStartup, value);
        }

        public bool UpdateOnGameStart
        {
            get => updateOnGameStart;
            set => SetValue(ref updateOnGameStart, value);
        }

        public LabelType SelectedLabelType
        {
            get => selectedLabelType;
            set => SetValue(ref selectedLabelType, value);
        }

        public string RandomPrefix
        {
            get => randomPrefix;
            set => SetValue(ref randomPrefix, value);
        }

        public bool IncludeUninstalledGames
        {
            get => includeUninstalledGames;
            set => SetValue(ref includeUninstalledGames, value);
        }

        public bool IncludeHiddenGames
        {
            get => includeHiddenGames;
            set => SetValue(ref includeHiddenGames, value);
        }

        public Guid? RequiredFeatureId
        {
            get => requiredFeatureId;
            set => SetValue(ref requiredFeatureId, value);
        }

        public AutomaticRunBehavior AutomaticRunBehavior
        {
            get => automaticRunBehavior;
            set => SetValue(ref automaticRunBehavior, value);
        }

        public bool CleanupOrphanedRandomLabels
        {
            get => cleanupOrphanedRandomLabels;
            set => SetValue(ref cleanupOrphanedRandomLabels, value);
        }

        public bool CleanupOnAutomaticRuns
        {
            get => cleanupOnAutomaticRuns;
            set => SetValue(ref cleanupOnAutomaticRuns, value);
        }

        public bool UseCollisionFreeLabels
        {
            get => useCollisionFreeLabels;
            set => SetValue(ref useCollisionFreeLabels, value);
        }

        public bool DetailedLogging
        {
            get => detailedLogging;
            set => SetValue(ref detailedLogging, value);
        }

        public int DatabaseUpdateBatchSize
        {
            get => databaseUpdateBatchSize;
            set => SetValue(ref databaseUpdateBatchSize, value);
        }

        public int AutomaticDatabaseUpdateBatchSize
        {
            get => automaticDatabaseUpdateBatchSize;
            set => SetValue(ref automaticDatabaseUpdateBatchSize, value);
        }

        public int UiBatchDelayMs
        {
            get => uiBatchDelayMs;
            set => SetValue(ref uiBatchDelayMs, value);
        }

        public int MaxAutomaticCleanupLabels
        {
            get => maxAutomaticCleanupLabels;
            set => SetValue(ref maxAutomaticCleanupLabels, value);
        }
    }

    public class RandomSortingSettingsViewModel : ObservableObject, ISettings
    {
        private readonly RandomSorting plugin;
        private RandomSortingSettings settings;

        public RandomSortingSettings Settings
        {
            get => settings;
            set => SetValue(ref settings, value);
        }

        public string RandomPrefix
        {
            get => settings.RandomPrefix;
            set => settings.RandomPrefix = value;
        }

        public Array LabelTypeOptions => Enum.GetValues(typeof(LabelType));

        public List<AutomaticRunBehaviorOption> AutomaticRunBehaviorOptions => new List<AutomaticRunBehaviorOption>
        {
            new AutomaticRunBehaviorOption
            {
                Value = AutomaticRunBehavior.CancelCurrentAndRestart,
                Name = "Cancel current run and start new run"
            },
            new AutomaticRunBehaviorOption
            {
                Value = AutomaticRunBehavior.SkipIfRunning,
                Name = "Skip new trigger while running"
            },
            new AutomaticRunBehaviorOption
            {
                Value = AutomaticRunBehavior.QueueOneRerunAfterCurrent,
                Name = "Queue one rerun after current run"
            }
        };

        public List<FeatureFilterOption> FeatureFilterOptions => plugin.GetFeatureFilterOptions();

        public ICommand AssignRandomIdentifiersCommand { get; }
        public ICommand CleanupRandomLabelsCommand { get; }

        public RandomSortingSettingsViewModel(RandomSorting plugin)
        {
            this.plugin = plugin;
            settings = plugin.LoadPluginSettings<RandomSortingSettings>() ?? new RandomSortingSettings();

            AssignRandomIdentifiersCommand = new RelayCommand(AssignRandomIdentifiers);
            CleanupRandomLabelsCommand = new RelayCommand(CleanupRandomLabels);
        }

        private void AssignRandomIdentifiers()
        {
            plugin.AssignRandomIdentifiersWithProgress(Settings.SelectedLabelType);
        }

        private void CleanupRandomLabels()
        {
            plugin.CleanupRandomLabelsWithProgress(Settings.SelectedLabelType);
        }

        public void BeginEdit() { }

        public void CancelEdit() { }

        public void EndEdit()
        {
            plugin.SavePluginSettings(settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = null;
            return true;
        }
    }
}
