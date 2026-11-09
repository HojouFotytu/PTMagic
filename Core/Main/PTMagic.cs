using System;
using System.Data;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Linq.Dynamic.Core;
using System.Linq.Expressions;
using Core.Helper;
using Core.Main.DataObjects.PTMagicData;
using Core.MarketAnalyzer;
using Core.MarketAnalyzer.MarketData;
using Core.ProfitTrailer;
using Newtonsoft.Json;

namespace Core.Main
{
  public class PTMagic
  {
    public PTMagic(LogHelper log, Func<ConfigurationPayload, ConfigurationDeliveryResult> configurationSender = null,
      Func<bool, ConfigurationDeliveryResult> globalSellOnlyModeSender = null)
    {
      this.Log = log;
      _configurationSender = configurationSender;
      _globalSellOnlyModeSender = globalSellOnlyModeSender;
    }
    
    #region Properties
    private LogHelper _log;
    private PTMagicConfiguration _systemConfiguration;
    private System.Timers.Timer _timer;
    private Summary _lastRuntimeSummary = null;
    private int _state = 0;
    private int _runCount = 0;
    private int _totalElapsedSeconds = 0;
    private bool _globalSettingWritten = false;
    private bool _singleMarketSettingChanged = false;
    private List<KeyValuePair<string, string>> _lastActiveSingleMarketSettings = null;
    private bool _enforceSettingsReapply = false;
    private DateTime _lastRuntime = Constants.confMinDate;
    private DateTime _lastSettingsChange = Constants.confMinDate;
    private DateTime _lastSettingFileCheck = Constants.confMinDate;
    private DateTime _lastVersionCheck = Constants.confMinDate;
    private DateTime _lastFiatCurrencyCheck = Constants.confMinDate;
    private string _activeSettingName = "";
    private GlobalSetting _activeSetting = null;
    private string _defaultSettingName = "";
    private string _pairsFileName = "PAIRS.PROPERTIES";
    private string _dcaFileName = "DCA.PROPERTIES";
    private string _indicatorsFileName = "INDICATORS.PROPERTIES";
    private Version _currentVersion = null;
    private string _latestVersion = "";
    private string _lastMainFiatCurrency = "USD";
    private double _lastMainFiatCurrencyExchangeRate = 1;
    private List<SingleMarketSettingSummary> _singleMarketSettingSummaries = new List<SingleMarketSettingSummary>();
    private List<string> _pairsLines = null;
    private List<string> _dcaLines = null;
    private List<string> _indicatorsLines = null;
    private List<string> _exchangeMarketList = null;
    private List<string> _marketList = new List<string>();
    private ConcurrentDictionary<string, MarketInfo> _marketInfos = new ConcurrentDictionary<string, MarketInfo>();
    private Dictionary<string, Market> _recentMarketSnapshots = new Dictionary<string, Market>(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, double> _averageMarketTrendChanges = new Dictionary<string, double>();
    private Dictionary<string, List<MarketTrendChange>> _singleMarketTrendChanges = new Dictionary<string, List<MarketTrendChange>>();
    private Dictionary<string, List<MarketTrendChange>> _globalMarketTrendChanges = new Dictionary<string, List<MarketTrendChange>>();
    private Dictionary<string, int> _singleMarketSettingsCount = new Dictionary<string, int>();
    Dictionary<string, List<SingleMarketSetting>> _triggeredSingleMarketSettings = new Dictionary<string, List<SingleMarketSetting>>();
    private readonly SemaphoreSlim _raidSemaphore = new SemaphoreSlim(1, 1);
    private readonly Core.MarketAnalyzer.MarketData.RaidMetricWarnings _raidMetricWarnings =
      new Core.MarketAnalyzer.MarketData.RaidMetricWarnings();
    private readonly Func<ConfigurationPayload, ConfigurationDeliveryResult> _configurationSender;
    private readonly Func<bool, ConfigurationDeliveryResult> _globalSellOnlyModeSender;
    private ConfigurationDeliveryCoordinator _configurationDelivery = new ConfigurationDeliveryCoordinator();
    private ConfigurationDeliveryResult _raidDelivery = ConfigurationDeliveryResult.Deferred("No completed raid.");
    private bool _firstSuccessfulRaidPending = true;
    private bool _startupGuardActive;
    private bool _startupGuardPending;
    private ConfigurationDeliveryResult _startupGuardDelivery = ConfigurationDeliveryResult.Deferred("No startup global SOM command attempted.");
    private string _startupGuardStatus = "Inactive";
    private bool _selectionIndeterminate;
    private bool _decisionFresh;
    private bool _raidAnalysisBuilt;
    private bool _normalDecisionCommittedThisRaid;
    private bool _historyWritten;
    private string _deliveryDestinationHash = "";
    private bool? _deliveryTestMode;
    private string _raidId = "";
    private string _desiredGlobalSettingName = "";
    private string _desiredPayloadHash = "";
    private string[] _desiredSms = Array.Empty<string>();
    private GlobalSetting _raidPriorSetting;
    private DateTime _raidPriorSettingsChange;
    private List<SingleMarketSettingSummary> _raidPriorSms;
    private readonly Dictionary<string, DecisionMetricInput> _decisionInputs =
      new Dictionary<string, DecisionMetricInput>(StringComparer.OrdinalIgnoreCase);
    public ConfigurationDeliveryCoordinator ConfigurationDelivery => _configurationDelivery;
    private Mutex mutex = new Mutex(false, "analyzerStateMutex");

    public LogHelper Log
    {
      get
      {
        return _log;
      }
      set
      {
        _log = value;
      }
    }

    public PTMagicConfiguration PTMagicConfiguration
    {
      get
      {
        return _systemConfiguration;
      }
      set
      {
        _systemConfiguration = value;
      }
    }
    public System.Timers.Timer Timer
    {
      get
      {
        return _timer;
      }
      set
      {
        _timer = value;
      }
    }

    public Summary LastRuntimeSummary
    {
      get
      {
        return _lastRuntimeSummary;
      }
      set
      {
        _lastRuntimeSummary = value;
      }
    }

    public int State
    {
      get
      {
        return _state;
      }
      set
      {
        _state = value;
      }
    }
   public void WriteStateToFile()
{
    try
    {
        mutex.WaitOne(); // Acquire the mutex

        string dirPath = "_data";
        string filePath = Path.Combine(dirPath, "AnalyzerState");

        // Ensure the directory exists
        Directory.CreateDirectory(dirPath);

        File.WriteAllText(filePath, this.State.ToString());

    }
    finally
    {
        mutex.ReleaseMutex(); // Release the mutex even if exceptions occur
    }
}


    public int RunCount
    {
      get
      {
        return _runCount;
      }
      set
      {
        _runCount = value;
      }
    }

    public int TotalElapsedSeconds
    {
      get
      {
        return _totalElapsedSeconds;
      }
      set
      {
        _totalElapsedSeconds = value;
      }
    }

    public bool GlobalSettingWritten
    {
      get
      {
        return _globalSettingWritten;
      }
      set
      {
        _globalSettingWritten = value;
      }
    }

    public bool SingleMarketSettingChanged
    {
      get
      {
        return _singleMarketSettingChanged;
      }
      set
      {
        _singleMarketSettingChanged = value;
      }
    }

    public bool EnforceSettingsReapply
    {
      get
      {
        return _enforceSettingsReapply;
      }
      set
      {
        _enforceSettingsReapply = value;
      }
    }

    public DateTime LastSettingsChange
    {
      get
      {
        return _lastSettingsChange;
      }
      set
      {
        _lastSettingsChange = value;
      }
    }

    public DateTime LastVersionCheck
    {
      get
      {
        return _lastVersionCheck;
      }
      set
      {
        _lastVersionCheck = value;
      }
    }

    public DateTime LastFiatCurrencyCheck
    {
      get
      {
        return _lastFiatCurrencyCheck;
      }
      set
      {
        _lastFiatCurrencyCheck = value;
      }
    }

    public DateTime LastSettingFileCheck
    {
      get
      {
        return _lastSettingFileCheck;
      }
      set
      {
        _lastSettingFileCheck = value;
      }
    }

    public DateTime LastRuntime
    {
      get
      {
        return _lastRuntime;
      }
      set
      {
        _lastRuntime = value;
      }
    }

    public string DefaultSettingName
    {
      get
      {
        return _defaultSettingName;
      }
      set
      {
        _defaultSettingName = value;
      }
    }

    public GlobalSetting ActiveSetting
    {
      get
      {
        return _activeSetting;
      }
      set
      {
        _activeSetting = value;
      }
    }

    public string ActiveSettingName
    {
      get
      {
        return _activeSettingName;
      }
      set
      {
        _activeSettingName = value;
      }
    }

    public string PairsFileName
    {
      get
      {
        return _pairsFileName;
      }
      set
      {
        _pairsFileName = value;
      }
    }

    public string DCAFileName
    {
      get
      {
        return _dcaFileName;
      }
      set
      {
        _dcaFileName = value;
      }
    }

    public string IndicatorsFileName
    {
      get
      {
        return _indicatorsFileName;
      }
      set
      {
        _indicatorsFileName = value;
      }
    }

    public Version CurrentVersion
    {
      get
      {
        return _currentVersion;
      }
      set
      {
        _currentVersion = value;
      }
    }

    public string LatestVersion
    {
      get
      {
        return _latestVersion;
      }
      set
      {
        _latestVersion = value;
      }
    }

    public string LastMainFiatCurrency
    {
      get
      {
        return _lastMainFiatCurrency;
      }
      set
      {
        _lastMainFiatCurrency = value;
      }
    }

    public double LastMainFiatCurrencyExchangeRate
    {
      get
      {
        return _lastMainFiatCurrencyExchangeRate;
      }
      set
      {
        _lastMainFiatCurrencyExchangeRate = value;
      }
    }

    public List<SingleMarketSettingSummary> SingleMarketSettingSummaries
    {
      get
      {
        return _singleMarketSettingSummaries;
      }
      set
      {
        _singleMarketSettingSummaries = value;
      }
    }

    public List<string> PairsLines
    {
      get
      {
        return _pairsLines;
      }
      set
      {
        _pairsLines = value;
      }
    }

    public List<string> DCALines
    {
      get
      {
        return _dcaLines;
      }
      set
      {
        _dcaLines = value;
      }
    }

    public List<string> IndicatorsLines
    {
      get
      {
        return _indicatorsLines;
      }
      set
      {
        _indicatorsLines = value;
      }
    }

    public List<string> ExchangeMarketList
    {
      get
      {
        return _exchangeMarketList;
      }
      set
      {
        _exchangeMarketList = value;
      }
    }

    public List<string> MarketList
    {
      get
      {
        return _marketList;
      }
      set
      {
        _marketList = value;
      }
    }

    public ConcurrentDictionary<string, MarketInfo> MarketInfos
    {
      get
      {
        return _marketInfos;
      }
      set
      {
        _marketInfos = value;
      }
    }

    public Dictionary<string, List<MarketTrendChange>> SingleMarketTrendChanges
    {
      get
      {
        return _singleMarketTrendChanges;
      }
      set
      {
        _singleMarketTrendChanges = value;
      }
    }

    public Dictionary<string, List<MarketTrendChange>> GlobalMarketTrendChanges
    {
      get
      {
        return _globalMarketTrendChanges;
      }
      set
      {
        _globalMarketTrendChanges = value;
      }
    }

    public Dictionary<string, double> AverageMarketTrendChanges
    {
      get
      {
        return _averageMarketTrendChanges;
      }
      set
      {
        _averageMarketTrendChanges = value;
      }
    }

    public Dictionary<string, int> SingleMarketSettingsCount
    {
      get
      {
        return _singleMarketSettingsCount;
      }
      set
      {
        _singleMarketSettingsCount = value;
      }
    }

    public Dictionary<string, List<SingleMarketSetting>> TriggeredSingleMarketSettings
    {
      get
      {
        return _triggeredSingleMarketSettings;
      }
      set
      {
        _triggeredSingleMarketSettings = value;
      }
    }
    #endregion

    #region PTMagic Startup  Methods

    private static int ExponentialDelay(int failedAttempts, int maxDelayInSeconds = 900)
    {
      //Attempt 1     0s     0s
      //Attempt 2     2s     2s
      //Attempt 3     4s     4s
      //Attempt 4     8s     8s
      //Attempt 5     16s    16s
      //Attempt 6     32s    32s

      //Attempt 7     64s     1m 4s
      //Attempt 8     128s    2m 8s
      //Attempt 9     256s    4m 16s
      //Attempt 10    512     8m 32s
      //Attempt 11    1024    17m 4s

      var delayInSeconds = ((1d / 2d) * (Math.Pow(2d, failedAttempts) - 1d));

      return maxDelayInSeconds < delayInSeconds
          ? maxDelayInSeconds
          : (int)delayInSeconds;
    }

    public bool StartProcess()
    {
      bool result = true;

      this.Log.DoLogInfo("");
      this.Log.DoLogInfo("  ██████╗ ████████╗    ███╗   ███╗ █████╗  ██████╗ ██╗ ██████╗");
      this.Log.DoLogInfo("  ██╔══██╗╚══██╔══╝    ████╗ ████║██╔══██╗██╔════╝ ██║██╔════╝");
      this.Log.DoLogInfo("  ██████╔╝   ██║       ██╔████╔██║███████║██║  ███╗██║██║     ");
      this.Log.DoLogInfo("  ██╔═══╝    ██║       ██║╚██╔╝██║██╔══██║██║   ██║██║██║     ");
      this.Log.DoLogInfo("  ██║        ██║       ██║ ╚═╝ ██║██║  ██║╚██████╔╝██║╚██████╗");
      this.Log.DoLogInfo("  ╚═╝        ╚═╝       ╚═╝     ╚═╝╚═╝  ╚═╝ ╚═════╝ ╚═╝ ╚═════╝");
      this.Log.DoLogInfo("                         Version " + this.CurrentVersion.Major + "." + this.CurrentVersion.Minor + "." + this.CurrentVersion.Build);
      this.Log.DoLogInfo("");
      this.Log.DoLogInfo("Starting PTMagic in " + Directory.GetCurrentDirectory());
      this.Log.DoLogInfo("with .NET Core: " + Path.GetDirectoryName(typeof(object).Assembly.Location));

      if (!this.RunStartupChecks())
      {
        return false;
      }
      if (!this.InitializeConfiguration())
      {
        return false;
      }

      bool configCheckResult = this.RunConfigurationChecks();

      if (!configCheckResult)
      {
        // Config check failed so retry using an exponential back off until it passes; max retry time 15 mins.
        int configRetryCount = 1;
        int delaySeconds;

        while (!configCheckResult)
        {
          delaySeconds = ExponentialDelay(configRetryCount);
          this.Log.DoLogError("Configuration check retry " + configRetryCount + " failed, starting next retry in " + delaySeconds + " seconds...");
          Thread.Sleep(delaySeconds * 1000);

          // Reinit config in case the user changed something
          this.InitializeConfiguration();
          configCheckResult = this.RunConfigurationChecks();
          configRetryCount++;
        }
      }

      this.LastSettingFileCheck = DateTime.UtcNow;

      SettingsFiles.CheckPresets(this.PTMagicConfiguration, this.Log, true);

      // Start the _preset folder file watcher.
      SettingsFiles.PresetFileWatcher.Changed += PresetFileWatcher_OnChanged;
      SettingsFiles.PresetFileWatcher.EnableRaisingEvents = true;

      // Force settings refresh first time
      EnforceSettingsReapply = true;

      // Set the Active config
      this.ActiveSetting = this.PTMagicConfiguration.AnalyzerSettings.GlobalSettings.Find(s => s.SettingName.Equals(this.DefaultSettingName, StringComparison.InvariantCultureIgnoreCase));
      this.ActiveSettingName = ActiveSetting.SettingName;
      this.LastSettingsChange = DateTime.UtcNow;
      this.InitializeDeliveryTracking();

      // Start polling
      this.StartPTMagicIntervalTimer();

      return result;
    }

    // File watcher event handlers
    private void PresetFileWatcher_OnChanged(object source, FileSystemEventArgs e)
    {
      // Disable the file watcher whilst we deal with the event
      SettingsFiles.PresetFileWatcher.EnableRaisingEvents = false;

      this.Log.DoLogInfo("Detected a '" + e.ChangeType.ToString() + "' change in the following preset file: " + e.FullPath);

      // Reprocess now
      this.EnforceSettingsReapply = true;
      PTMagicIntervalTimer_Elapsed(new object(), null);

      // Enable the file watcher again
      SettingsFiles.PresetFileWatcher.EnableRaisingEvents = true;
    }

    public bool RunStartupChecks()
    {
      bool result = true;

      // Startup checks
      if (!File.Exists(Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "settings.general.json"))
      {
        this.Log.DoLogError("File 'settings.general.json' not found! Please review the setup steps on the wiki and double check every step that involves copying files!");
        result = false;
      }

      if (!File.Exists(Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "settings.analyzer.json"))
      {
        this.Log.DoLogError("File 'settings.analyzer.json' not found! Please review the setup steps on the wiki and double check every step that involves copying files!");
        result = false;
      }

      return result;
    }

    public bool InitializeConfiguration()
    {
      bool result = true;

      try
      {
        this.PTMagicConfiguration = new PTMagicConfiguration();

        this.Log.DoLogInfo("Configuration loaded. Found " +
                            this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.MarketTrends != null ? this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.MarketTrends.Count.ToString() : "0" +
                            " Market Trends, " +
                            this.PTMagicConfiguration.AnalyzerSettings.GlobalSettings != null ? this.PTMagicConfiguration.AnalyzerSettings.GlobalSettings.Count.ToString() : "0" +
                            " Global Settings and " +
                            this.PTMagicConfiguration.AnalyzerSettings.SingleMarketSettings != null ? this.PTMagicConfiguration.AnalyzerSettings.SingleMarketSettings.Count.ToString() : "0" +
                            " Single Market Settings.");
      }
      catch (Exception ex)
      {
        result = false;
        this.Log.DoLogCritical("Error loading configuration!", ex);
        throw;
      }

      return result;
    }

    public bool RunConfigurationChecks()
    {
      bool result = true;

      foreach (string error in TriggerConfigurationValidator.Validate(this.PTMagicConfiguration.AnalyzerSettings))
      {
        this.Log.DoLogError("Invalid analyzer configuration: " + error);
        result = false;
      }
      foreach (string warning in TriggerConfigurationValidator.GetMetricWarnings(this.PTMagicConfiguration.AnalyzerSettings))
        this.Log.DoLogWarn("Analyzer metric configuration: " + warning);
      if (!result) return false;

      //Import Initial ProfitTrailer Information(Deactivated for now)
      //SettingsAPI.GetInitialProfitTrailerSettings(this.PTMagicConfiguration);

      // Check for valid default setting
      GlobalSetting defaultSetting = this.PTMagicConfiguration.AnalyzerSettings.GlobalSettings.Find(s => s.SettingName.Equals("default", StringComparison.InvariantCultureIgnoreCase));
      if (defaultSetting == null)
      {
        defaultSetting = this.PTMagicConfiguration.AnalyzerSettings.GlobalSettings.Find(s => s.SettingName.IndexOf("default", StringComparison.InvariantCultureIgnoreCase) > -1);
        if (defaultSetting != null)
        {
          this.Log.DoLogDebug("No setting named 'default' found, taking '" + defaultSetting.SettingName + "' as default.");
          this.DefaultSettingName = defaultSetting.SettingName;
        }
        else
        {
          this.Log.DoLogError("No 'default' setting found! Terminating process...");
          result = false;
        }
      }
      else
      {
        this.DefaultSettingName = defaultSetting.SettingName;
      }

      // Check if exchange is valid
      if (!BinanceFuturesAnalyticsRuntime.IsSupportedExchange(
        this.PTMagicConfiguration.GeneralSettings.Application.Exchange))
      {
        this.Log.DoLogError("Exchange '" + this.PTMagicConfiguration.GeneralSettings.Application.Exchange + "' specified in settings.general.json is invalid! Terminating process...");
        result = false;
      }

      // Check if the program is enabled
      if (this.PTMagicConfiguration.GeneralSettings.Application.IsEnabled)
      {
        result = RunProfitTrailerSettingsAPIChecks() && result;
        try
        {
          if (this.PTMagicConfiguration.GeneralSettings.Application.TestMode) 
          {
            this.Log.DoLogWarn("TESTMODE ENABLED - No PT settings will be changed!");
          }
        }
        catch (System.NullReferenceException)
        {
          this.Log.DoLogError("PTM failed to read the General Settings file. That means something in the file is either missing or incorrect. If this happend after an update please take a look at the release notes at: https://github.com/PTMagicians/PTMagic/releases");
          Console.WriteLine("Press enter to close the Application...");
          Console.ReadLine();
          Environment.Exit(0);
        }
      }
      else
      {
        this.Log.DoLogWarn("PTMagic is disabled.  The scheduled raid was skipped.");
        result = false;
      }

      return result;
    }

    private bool RunProfitTrailerSettingsAPIChecks()
    {
      bool result = true;

      this.Log.DoLogInfo("========== STARTING CHECKS FOR Profit Trailer ==========");

      // Check for PT license key
      if (!String.IsNullOrEmpty(this.PTMagicConfiguration.GeneralSettings.Application.ProfitTrailerLicense))
      {
        this.Log.DoLogInfo("Profit Trailer check: Profit Trailer license found");
      }
      else
      {
        this.Log.DoLogError("Profit Trailer check: No Profit Trailer license key specified! The license key is necessary to adjust your Profit Trailer settings");
        result = false;
      }

      //Check for ptServerAPIToken
      if (!String.IsNullOrEmpty(this.PTMagicConfiguration.GeneralSettings.Application.ProfitTrailerServerAPIToken))
      {
        this.Log.DoLogInfo("Profit Trailer check: Profit Trailer Server API Token Specified");
      }
      else
      {
        this.Log.DoLogError("Profit Trailer check: No Server API Token specified. Please configure ProfitTrailerServerAPIToken in settings.general.json , ensuring it has to be the same Token as in the Profit Trailer Config File!");
        result = false;
      }

      // Check for PT default setting key
      if (!String.IsNullOrEmpty(this.PTMagicConfiguration.GeneralSettings.Application.ProfitTrailerDefaultSettingName))
      {
        this.Log.DoLogInfo("Profit Trailer check: Profit Trailer default setting name specified");
      }
      else
      {
        this.Log.DoLogError("Profit Trailer check: No Profit Trailer default setting name specified! The default setting name is necessary to adjust your Profit Trailer settings since 2.0");
        result = false;
      }

      // Check for PT monitor
      if (!String.IsNullOrEmpty(this.PTMagicConfiguration.GeneralSettings.Application.ProfitTrailerMonitorURL))
      {
        this.Log.DoLogInfo("Profit Trailer check: Profit Trailer monitor URL found");
      }
      else
      {
        this.Log.DoLogError("Profit Trailer check: No Profit Trailer monitor URL specified! The monitor URL is necessary to adjust your Profit Trailer settings since 2.0");
        result = false;
      }

      // Check if PT monitor is reachable
      if (SystemHelper.UrlIsReachable(this.PTMagicConfiguration.GeneralSettings.Application.ProfitTrailerMonitorURL))
      {
        this.Log.DoLogInfo("Profit Trailer check: Profit Trailer monitor connection test succeeded");
      }
      else
      {
        this.Log.DoLogError("Profit Trailer check: Your Profit Trailer monitor (" + this.PTMagicConfiguration.GeneralSettings.Application.ProfitTrailerMonitorURL + ") is not available! Make sure your Profit Trailer bot is up and running and your monitor is accessible.");
        result = false;
      }

      if (result)
      {
        this.Log.DoLogInfo("========== CHECKS FOR Profit Trailer COMPLETED! ==========");
      }
      else
      {
        this.Log.DoLogInfo("========== CHECKS FOR Profit Trailer FAILED! ==========");
      }

      return result;
    }

    public void StartPTMagicIntervalTimer()
    {
      this.Timer = new System.Timers.Timer(this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.IntervalMinutes * 60 * 1000);
      this.Timer.Enabled = true;
      this.Timer.Elapsed += new System.Timers.ElapsedEventHandler(this.PTMagicIntervalTimer_Elapsed);

      this.Log.DoLogInfo("Checking market trends every " + this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.IntervalMinutes.ToString() + " minutes...");

      // Fire the first start immediately
      this.PTMagicIntervalTimer_Elapsed(null, null);
    }
    #endregion

    #region PTMagic Interval Methods



    public async void PTMagicIntervalTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
    {
      // Check if the bot is idle
      if (this.State == Constants.PTMagicBotState_Idle)
      {
        if (!await _raidSemaphore.WaitAsync(0).ConfigureAwait(false))
        {
          return;
        }

        try
        {
          try
          {
            // Change state to "Running"
            this.State = Constants.PTMagicBotState_Running;
            this.WriteStateToFile();
            this.RunCount++;
            this.LastRuntime = DateTime.UtcNow;
            _raidId = Guid.NewGuid().ToString("N");
            _historyWritten = false;
            _raidAnalysisBuilt = false;
            _normalDecisionCommittedThisRaid = false;
            _selectionIndeterminate = false;
            _decisionFresh = false;
            _decisionInputs.Clear();
            _desiredPayloadHash = "";
            _desiredSms = Array.Empty<string>();
            _desiredGlobalSettingName = this.ActiveSettingName;
            _raidPriorSetting = this.ActiveSetting;
            _raidPriorSettingsChange = this.LastSettingsChange;
            _raidPriorSms = null;
            _raidDelivery = ConfigurationDeliveryResult.Deferred("Raid has not completed.");

            this.EnforceSettingsReapply = this.HaveSettingsChanged() || this.EnforceSettingsReapply;

            if (PTMagicConfiguration.GeneralSettings.Application.IsEnabled)
            {
              // Validate settings
              this.ValidateSettings();
              this.EnsureDeliveryContext();
              _raidPriorSetting = this.ActiveSetting;
              _raidPriorSettingsChange = this.LastSettingsChange;

              // Start the process
              this.Log.DoLogInfo("");
              this.Log.DoLogInfo("##########################################################");
              this.Log.DoLogInfo("#********************************************************#");
              this.Log.DoLogInfo("Starting market trend check with Version " + this.CurrentVersion.Major + "." + this.CurrentVersion.Minor + "." + this.CurrentVersion.Build);

              // Initialise the last runtime summary              
              this.LastRuntimeSummary = new Summary();
              this._raidMetricWarnings.Reset();
              this.LastRuntimeSummary.LastRuntime = this.LastRuntime;
              this.LastRuntimeSummary.Version = this.CurrentVersion.Major.ToString() + "." + this.CurrentVersion.Minor.ToString() + "." + this.CurrentVersion.Build.ToString();
              this.LastRuntimeSummary.SourceRevision = BuildIdentity.SourceRevision;
              this.Log.DoLogInfo("Source revision: " + BuildIdentity.SourceRevision);

              // The native global SOM command changes no preset or property.
              // Protect new and DCA buys before compilation/network bootstrap.
              this.EnsureStartupSellOnlyMode();

              // Load current PT files
              this.LoadCurrentProfitTrailerProperties();

              // Loading SMS Summaries
              this.LoadSMSSummaries();
              _raidPriorSms = JsonConvert.DeserializeObject<List<SingleMarketSettingSummary>>(
                JsonConvert.SerializeObject(this.SingleMarketSettingSummaries));

              // Check for latest GitHub version after the startup guard is confirmed.
              await this.CheckLatestGitHubVersionAsync(this.LastRuntimeSummary.Version).ConfigureAwait(false);

              // Get saved market info
              this.MarketInfos = BaseAnalyzer.GetMarketInfosFromFile(this.PTMagicConfiguration, this.Log);

              // Build exchange market data
              await this.BuildMarketDataAsync().ConfigureAwait(false);

              // Get markets from PT properties
              this.BuildMarketList();
              this.ValidateMarketList();

              bool normalizedExchange = BinanceFuturesAnalyticsRuntime.IsSupportedExchange(
                this.PTMagicConfiguration.GeneralSettings.Application.Exchange);
              // Futures freezes its metadata-qualified universe in GetMarketPairs.
              // Spot and Binance US freeze their exchange-eligible population here.
              if (normalizedExchange && !this.PTMagicConfiguration.GeneralSettings.Application.Exchange
                .Equals("BinanceFutures", StringComparison.OrdinalIgnoreCase))
              {
                BinanceFuturesAnalyticsRuntime.FreezeAnalyzerUniverse(
                  this.PairsLines, this.PTMagicConfiguration, this.Log);
              }
              this.InitializeMarketTrendData();

              if (normalizedExchange)
              {
                await BinanceFuturesAnalyticsRuntime.RefreshOpenInterestAsync(
                  this.PTMagicConfiguration, this.Log, this._recentMarketSnapshots).ConfigureAwait(false);
                BinanceFuturesAnalyticsRuntime.EvaluateConfiguredMarketTrends(
                  this.GlobalMarketTrendChanges, this.SingleMarketTrendChanges,
                  this.PTMagicConfiguration, this.Log, this._recentMarketSnapshots);
                this.ApplyNormalizedPriceTrends();
                MarketTrendAnalyticsMonitorExporter.SaveCurrentRuntimeSnapshot(this.Log);
              }
              _raidAnalysisBuilt = true;
              _decisionFresh = this.IsRaidFresh(out string raidFreshnessReason);
              if (!_decisionFresh) this._raidMetricWarnings.ReportGlobalDecision(raidFreshnessReason);

              // Check for global settings triggers
              GlobalSetting triggeredSetting = this.PTMagicConfiguration.AnalyzerSettings.GlobalSettings.Find(s => s.SettingName.Equals(this.DefaultSettingName, StringComparison.InvariantCultureIgnoreCase));
              List<string> matchedTriggers = new List<string>();
              this.CheckGlobalSettingsTriggers(ref triggeredSetting, ref matchedTriggers);

              // Record the actual setting-name transition separately from forced
              // reapplication of the same preset.
              string previousActiveSettingName = this.ActiveSettingName;

              // Activate global setting
              this.ActivateSetting(ref triggeredSetting, ref matchedTriggers);

              // Check for single market trend triggers
              if (_decisionFresh) this.ApplySingleMarketSettings();

              // Ignore quarterly futures
              if (this.PTMagicConfiguration.GeneralSettings.Application.Exchange.Equals("BinanceFutures", StringComparison.InvariantCultureIgnoreCase))
              {
                // Find all quarterly futures pairs
                var results = this.MarketList.FindAll(m => m.Contains("_", StringComparison.InvariantCultureIgnoreCase));

                // Create the settings lines to disable trading
                if (results.Count > 0)
                {
                  this.PairsLines.AddRange(new string[] {
                    "",
                    "# BinanceFutures Quarterly Contracts - Ignore list:",
                    "###################################################"
                  });

                  foreach (var marketPair in results)
                  {
                    this.PairsLines.Add(String.Format("{0}_trading_enabled = false", marketPair));
                  }
                }
              }

              // Save new properties to Profit Trailer
              this.SaveProfitTrailerProperties();
              bool globalSettingSwitchedThisRaid = _normalDecisionCommittedThisRaid && !String.Equals(
                previousActiveSettingName, this.ActiveSettingName, StringComparison.OrdinalIgnoreCase);

              // Save Single Market Settings Summary
              if (_normalDecisionCommittedThisRaid) this.SaveSingleMarketSettingsSummary();

              // Claculate raid time
              DateTime endTime = DateTime.UtcNow;
              int elapsedSeconds = (int)Math.Round(endTime.Subtract(this.LastRuntime).TotalSeconds, 0);
              this.TotalElapsedSeconds += elapsedSeconds;

              // Save Runtime Summary
              this.SaveRuntimeSummary(elapsedSeconds);
              this.RecordDecisionHistory();

              // Summarise raid              
              this.Log.DoLogInfo("##########################################################");
              this.Log.DoLogInfo("#******************* RAID SUMMARY ********************#");
              this.Log.DoLogInfo("+ PT Magic Version: " + this.LastRuntimeSummary.Version);
              if (!SystemHelper.IsRecentVersion(this.LastRuntimeSummary.Version, this.LatestVersion))
              {
                this.Log.DoLogWarn("+ Your version is out of date! The most recent version is " + this.LatestVersion);
              }
              this.Log.DoLogInfo("+ Instance name: " + PTMagicConfiguration.GeneralSettings.Application.InstanceName);
              this.Log.DoLogInfo("+ Time spent: " + SystemHelper.GetProperDurationTime(elapsedSeconds));
              this.Log.DoLogInfo("+ Selected setting: " + _desiredGlobalSettingName);
              this.Log.DoLogInfo("+ Confirmed PT setting: " + (String.IsNullOrWhiteSpace(this.LastRuntimeSummary.AppliedGlobalSettingName) ? "unknown" : this.LastRuntimeSummary.AppliedGlobalSettingName));
              this.Log.DoLogInfo("+ Configuration delivery: " + _raidDelivery.Status);
              this.Log.DoLogInfo("+ Startup global SOM: " + _startupGuardStatus
                + (this.PTMagicConfiguration.GeneralSettings.Application.TestMode ? " (TestMode preview)" : ""));
              this.Log.DoLogInfo("+ Global setting switched: " + (globalSettingSwitchedThisRaid ? "Yes" : "No") + ((this.LastRuntimeSummary.FloodProtectedSetting != null) ? " (Flood protection!)" : ""));
              this.Log.DoLogInfo("+ Global setting reapplied: " + ((_normalDecisionCommittedThisRaid && this.GlobalSettingWritten && !globalSettingSwitchedThisRaid) ? "Yes" : "No"));
              this.Log.DoLogInfo("+ Single Market Settings changed: " + (this.SingleMarketSettingChanged ? "Yes" : "No"));
              this.Log.DoLogInfo("+ PT Config updated: " + ((_raidDelivery.Succeeded && _raidDelivery.Attempted && !_raidDelivery.Simulated) ? "Yes" : "No") + ((this.PTMagicConfiguration.GeneralSettings.Application.TestMode) ? " - TESTMODE active" : ""));
              this.Log.DoLogInfo("+ Markets with active single market settings: " + this.TriggeredSingleMarketSettings.Count.ToString());
              foreach (string activeSMS in this.SingleMarketSettingsCount.Keys)
              {
                this.Log.DoLogInfo("+   " + activeSMS + ": " + this.SingleMarketSettingsCount[activeSMS].ToString());
              }
              // Group metric problems here instead of repeating per-pair trigger warnings.
              if (BinanceFuturesAnalyticsRuntime.IsSupportedExchange(this.PTMagicConfiguration.GeneralSettings.Application.Exchange))
              {
                IReadOnlyDictionary<string, Core.MarketAnalyzer.MarketData.MarketTrendAnalysisSnapshot> analyses =
                  Core.MarketAnalyzer.MarketData.BinanceFuturesAnalyticsRuntime.CurrentTrendAnalyses;
                IReadOnlyList<string> warnings = this._raidMetricWarnings.BuildLines(analyses);
                if (warnings.Count > 0)
                {
                  foreach (string warning in warnings)
                  {
                    this.Log.DoLogWarn(warning);
                  }
                }
                else if (analyses.Count > 0)
                {
                  this.Log.DoLogInfo("+ Metric warnings: none");
                }
              }
              this.Log.DoLogInfo("+ " + this.TotalElapsedSeconds.ToString() + " Magicbots killed in " + this.RunCount.ToString() + " raids on Cryptodragon's Lair " + this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.IntervalMinutes.ToString() + ".");
              this.Log.DoLogInfo("");
              this.Log.DoLogInfo("DO NOT CLOSE THIS WINDOW! THIS IS THE BOT THAT ANALYZES TRENDS AND CHANGES SETTINGS!");
              this.Log.DoLogInfo("");
              this.Log.DoLogInfo("#********************************************************#");
              this.Log.DoLogInfo("##########################################################");
              this.Log.DoLogInfo("");
            }
            else
            {
              this.State = Constants.PTMagicBotState_Idle;
              Log.DoLogWarn("PTMagic is disabled.  The scheduled raid was skipped.");
            }
          }
          catch (Exception ex)
          {
            if (!_normalDecisionCommittedThisRaid)
            {
              this.RestoreRaidState();
              if (_raidDelivery.Succeeded) _raidDelivery = ConfigurationDeliveryResult.Deferred("Raid failed before a normal configuration could be committed.");
              this.UpdateDeliverySummary();
            }
            this.PersistFailedRaidSummary();
            this.RecordDecisionHistory(ex.GetType().Name);
            // Error
            this.Log.DoLogCritical("A error occurred during the raid, the raid did not complete, but will try again next interval!", ex);
          }
          finally
          {
            this.RecordDecisionHistory();
            // Cleanup to free memory in between intervals
            this.Cleanup();

            // Change state to Finished / Stopped
            this.State = Constants.PTMagicBotState_Idle;
            this.WriteStateToFile();
          }
        }
        finally
        {
          _raidSemaphore.Release();
        }
      }
      else
      {
        if (this.RunCount > 1)
        {
          Log.DoLogWarn("PTMagic has been raiding since " + this.LastRuntime.ToLocalTime().ToString() + ".  Checking things...");

          if (File.Exists(Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + Constants.PTMagicPathData + Path.DirectorySeparatorChar + "LastRuntimeSummary.json"))
          {
            FileInfo fiLastSummary = new FileInfo(Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + Constants.PTMagicPathData + Path.DirectorySeparatorChar + "LastRuntimeSummary.json");
            if (fiLastSummary.LastWriteTimeUtc < DateTime.UtcNow.AddMinutes(-(this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.IntervalMinutes * 2)))
            {
              Log.DoLogWarn("PTMagic raid " + this.RunCount.ToString() + " is taking longer than expected.  Consider increasing your IntervalMinues setting, reducing other processes on your PC, or raising PTMagic's priority.");
              this.State = Constants.PTMagicBotState_Idle;
              this.WriteStateToFile();
              Log.DoLogInfo("PTMagic status reset, waiting for the next raid to be good to go again.");
            }
          }
          else
          {
            Log.DoLogWarn("No LastRuntimeSummary.json found after raid " + this.RunCount.ToString() + ", trying to reset PT Magic status...");
            this.State = Constants.PTMagicBotState_Idle;
            this.WriteStateToFile();
            Log.DoLogInfo("PTMagic status reset, waiting for the next raid to be good to go again.");
          }
        }
      }
    }

    private bool HaveSettingsChanged()
    {
      bool result = false;

      FileInfo generalSettingsFile = new FileInfo(Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "settings.general.json");
      FileInfo analyzerSettingsFile = new FileInfo(Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "settings.analyzer.json");
      if (generalSettingsFile.LastWriteTimeUtc > this.LastSettingFileCheck || analyzerSettingsFile.LastWriteTimeUtc > this.LastSettingFileCheck)
      {
        Log.DoLogInfo("Detected configuration changes. Reloading settings...");

        try
        {
          PTMagicConfiguration = new PTMagicConfiguration();

          GlobalSetting defaultSetting = this.PTMagicConfiguration.AnalyzerSettings.GlobalSettings.Find(s => s.SettingName.Equals("default", StringComparison.InvariantCultureIgnoreCase));
          if (defaultSetting == null)
          {
            defaultSetting = this.PTMagicConfiguration.AnalyzerSettings.GlobalSettings.Find(s => s.SettingName.IndexOf("default", StringComparison.InvariantCultureIgnoreCase) > -1);
            if (defaultSetting != null)
            {
              Log.DoLogDebug("No setting named 'default' found, taking '" + defaultSetting.SettingName + "' as default.");
              this.DefaultSettingName = defaultSetting.SettingName;
            }
            else
            {
              Log.DoLogError("No 'default' setting found! Terminating process...");
              this.Timer.Stop();
              Exception ex = new Exception("No 'default' setting found!Terminating process...");
              throw ex;
            }
          }
          else
          {
            this.DefaultSettingName = defaultSetting.SettingName;
          }

          Log.DoLogInfo("New configuration reloaded.");
          this.LastSettingFileCheck = DateTime.UtcNow;
          result = true;

          if (this.Timer.Interval != this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.IntervalMinutes * 60 * 1000)
          {
            Log.DoLogInfo("Setting for 'IntervalMinutes' changed in MarketAnalyzer, setting new timer...");
            this.Timer.Stop();
            this.Timer.Interval = this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.IntervalMinutes * 60 * 1000;
            this.Timer.Start();
            Log.DoLogInfo("New timer set to " + this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.IntervalMinutes.ToString() + " minutes.");
          }

          SettingsFiles.CheckPresets(this.PTMagicConfiguration, this.Log, true);
        }
        catch (Exception ex)
        {
          Log.DoLogCritical("Error loading new configuration!", ex);
        }
      }
      else
      {
        result = SettingsFiles.CheckPresets(this.PTMagicConfiguration, this.Log, false);
      }

      return result;
    }

    private void ValidateSettings()
    {
      TriggerConfigurationValidator.ThrowIfInvalid(this.PTMagicConfiguration.AnalyzerSettings);
      if (this.EnforceSettingsReapply)
        foreach (string warning in TriggerConfigurationValidator.GetMetricWarnings(this.PTMagicConfiguration.AnalyzerSettings))
          this.Log.DoLogWarn("Analyzer metric configuration: " + warning);
      Application application = this.PTMagicConfiguration.GeneralSettings.Application;
      if (application.MaxAnalysisAgeSeconds < 1 || application.MaxAnalysisAgeSeconds > 86400)
        throw new InvalidOperationException("MaxAnalysisAgeSeconds must be between 1 and 86400.");
      if (application.DecisionHistoryRetentionDays < 1 || application.DecisionHistoryRetentionDays > 3650
        || application.DecisionHistoryMaxMegabytes < 16 || application.DecisionHistoryMaxMegabytes > 102400)
        throw new InvalidOperationException("Decision history requires retention 1–3650 days and a quota of 16–102400 MiB.");
      //Reimport Initial ProfitTrailer Information(Deactivated for now)
      //SettingsAPI.GetInitialProfitTrailerSettings(this.PTMagicConfiguration);

      // Check for a valid exchange
      if (this.PTMagicConfiguration.GeneralSettings.Application.Exchange == null)
      {
        Log.DoLogError("Your setting for Application.Exchange in settings.general.json is invalid (null)! Terminating process.");
        this.Timer.Stop();
        Exception ex = new Exception("Your setting for Application.Exchange in settings.general.json is invalid (null)! Terminating process.");
        throw ex;
      }
      else
      {
        if (String.IsNullOrEmpty(this.PTMagicConfiguration.GeneralSettings.Application.Exchange))
        {
          Log.DoLogError("Your setting for Application.Exchange in settings.general.json is invalid (empty)! Terminating process.");
          this.Timer.Stop();
          Exception ex = new Exception("Your setting for Application.Exchange in settings.general.json is invalid (empty)! Terminating process.");
          throw ex;
        }
        else
        {
          if (!BinanceFuturesAnalyticsRuntime.IsSupportedExchange(
             this.PTMagicConfiguration.GeneralSettings.Application.Exchange))
          {
            Log.DoLogError("Your setting for Application.Exchange in settings.general.json is invalid (" + this.PTMagicConfiguration.GeneralSettings.Application.Exchange + ")! Terminating process.");
            this.Timer.Stop();
            Exception ex = new Exception("Your setting for Application.Exchange in settings.general.json is invalid (" + this.PTMagicConfiguration.GeneralSettings.Application.Exchange + ")! Terminating process.");
            throw ex;
          }
        }
      }
    }

    private async Task CheckLatestGitHubVersionAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
      // Get latest version number
      if (this.LastVersionCheck < DateTime.UtcNow.AddMinutes(-30))
      {
        this.LatestVersion = await BaseAnalyzer.GetLatestGitHubReleaseAsync(this.Log, currentVersion, cancellationToken).ConfigureAwait(false);
        this.LastVersionCheck = DateTime.UtcNow;
        if (!SystemHelper.IsRecentVersion(currentVersion, this.LatestVersion))
        {
          this.Log.DoLogWarn("Your bot is out of date! The most recent version of PTMagic is " + this.LatestVersion);
        }
      }
    }

    // Get current PT properties
    private void LoadCurrentProfitTrailerProperties()
    {
      // Load current PT properties from API (Valid for PT 2.x and above)
      this.Log.DoLogInfo("Loading current Profit Trailer properties from preset files...");

      // Get current preset file PT properties
      // A failed send can leave ActiveSetting referencing the acknowledged
      // pre-reload object. Always prepare the current definition for retry.
      GlobalSetting configured = this.PTMagicConfiguration.AnalyzerSettings.GlobalSettings.Find(setting =>
        String.Equals(setting.SettingName, this.ActiveSettingName, StringComparison.OrdinalIgnoreCase));
      GlobalSetting settingToLoad = configured ?? this.ActiveSetting;
      SettingsHandler.CompileProperties(this, settingToLoad, this.LastSettingsChange.ToLocalTime());

      if (this.PairsLines != null && this.DCALines != null && this.IndicatorsLines != null)
      {
        this.Log.DoLogInfo("Properties loaded - P (" + this.PairsLines.Count.ToString() + " lines) - D (" + this.DCALines.Count.ToString() + " lines) - I (" + this.IndicatorsLines.Count.ToString() + " lines).");
      }
      else
      {
        this.Log.DoLogError("Unable to load all Profit Trailer properties! Waiting for the next interval to retry...");
        Exception ex = new Exception("Unable to load all Profit Trailer properties! Waiting for the next interval to retry...");
        this.State = 0;
        throw ex;
      }

      // Get market from PT properties
      try
      {
        this.LastRuntimeSummary.MainMarket = SettingsHandler.GetMainMarket(this.PTMagicConfiguration, this.PairsLines, this.Log);
      }
      catch (InvalidOperationException error)
      {
        throw new InvalidOperationException("Unable to determine the analysis main market for GlobalSetting '" + settingToLoad.SettingName + "'. " + error.Message, error);
      }
      this.Log.DoLogInfo("Analysis main market: " + this.LastRuntimeSummary.MainMarket + " (compiled PAIRS for GlobalSetting '" + settingToLoad.SettingName + "').");
    }

    private void LoadSMSSummaries()
    {
      this.Log.DoLogInfo("Loading Single Market Setting Summaries...");
      this.SingleMarketSettingSummaries = new List<SingleMarketSettingSummary>();
      if (File.Exists(SmsSummaryPath))
      {
        try
        {
          Dictionary<string, bool> smsVerificationResult = new Dictionary<string, bool>();
          // Cleanup SMS Summaries in case a SMS got removed
          foreach (SingleMarketSettingSummary smsSummary in JsonConvert.DeserializeObject<List<SingleMarketSettingSummary>>(System.IO.File.ReadAllText(SmsSummaryPath)))
          {
            string smsName = smsSummary.SingleMarketSetting.SettingName;
            bool smsIsValid = false;
            if (smsVerificationResult.ContainsKey(smsName))
            {
              smsIsValid = smsVerificationResult[smsName];
            }
            else
            {
              SingleMarketSetting sms = this.PTMagicConfiguration.AnalyzerSettings.SingleMarketSettings.Find(s => s.SettingName.Equals(smsName));
              if (sms != null)
              {
                smsIsValid = true;
                smsVerificationResult.Add(smsName, true);
              }
              else
              {
                smsVerificationResult.Add(smsName, false);
              }
            }

            if (smsIsValid)
            {
              this.SingleMarketSettingSummaries.Add(smsSummary);
            }
          }

          this.Log.DoLogInfo("Single Market Setting Summaries loaded.");
        }
        catch { }
      }
    }

    private async Task BuildMarketDataAsync(CancellationToken cancellationToken = default)
    {
      // Reject unrepresentable normalized timeframes before any exchange
      // contact or potentially expensive historical reconstruction.
      if (BinanceFuturesAnalyticsRuntime.IsSupportedExchange(
        this.PTMagicConfiguration.GeneralSettings.Application.Exchange))
      {
        foreach (MarketTrend trend in this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.MarketTrends
          ?? new List<MarketTrend>())
        {
          if (trend != null && String.Equals(trend.Platform, "Exchange", StringComparison.OrdinalIgnoreCase))
          {
            MarketTrendTimeframe.Validate(trend, TimeSpan.FromMinutes(5));
          }
        }
      }

      if (this.PTMagicConfiguration.GeneralSettings.Application.Exchange.Equals("Binance", StringComparison.InvariantCultureIgnoreCase))
      {
        this.ExchangeMarketList = await Binance.GetMarketDataAsync(this.LastRuntimeSummary.MainMarket, this.MarketInfos, this.PTMagicConfiguration, this.Log, cancellationToken).ConfigureAwait(false);
        if (this.ExchangeMarketList != null)
        {
          await BinanceFuturesAnalyticsRuntime.RefreshObservationDataAsync(
            this.ExchangeMarketList, this.LastRuntimeSummary.MainMarket,
            this.PTMagicConfiguration, this.Log, cancellationToken, "Binance").ConfigureAwait(false);
        }
      }
      else if (this.PTMagicConfiguration.GeneralSettings.Application.Exchange.Equals("BinanceUS", StringComparison.InvariantCultureIgnoreCase))
      {
        this.ExchangeMarketList = await BinanceUS.GetMarketDataAsync(this.LastRuntimeSummary.MainMarket, this.MarketInfos, this.PTMagicConfiguration, this.Log, cancellationToken).ConfigureAwait(false);
        if (this.ExchangeMarketList != null)
        {
          await BinanceFuturesAnalyticsRuntime.RefreshObservationDataAsync(
            this.ExchangeMarketList, this.LastRuntimeSummary.MainMarket,
            this.PTMagicConfiguration, this.Log, cancellationToken, "BinanceUS").ConfigureAwait(false);
        }
      }
      else if (this.PTMagicConfiguration.GeneralSettings.Application.Exchange.Equals("BinanceFutures", StringComparison.InvariantCultureIgnoreCase))
      {
        // Get most recent market data from BinanceFutures
        this.ExchangeMarketList = await BinanceFutures.GetMarketDataAsync(this.LastRuntimeSummary.MainMarket, this.MarketInfos, this.PTMagicConfiguration, this.Log, cancellationToken).ConfigureAwait(false);
      }
      // Check if problems occured during the Exchange contact
      if (this.ExchangeMarketList == null)
      {
        Exception ex = new Exception("Unable to contact " + this.PTMagicConfiguration.GeneralSettings.Application.Exchange + " for fresh market data. Trying again in " + this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.IntervalMinutes + " minute(s).");
        Log.DoLogError(ex.Message);
        this.State = Constants.PTMagicBotState_Idle;
        throw ex;
      }
    }

    private void BuildMarketList()
    {
      string marketPairs = SettingsHandler.GetMarketPairs(this.PTMagicConfiguration, this.PairsLines, this.Log);
      if (marketPairs.ToLower().Equals("all") || marketPairs.ToLower().Equals("false") || marketPairs.ToLower().Equals("true") || String.IsNullOrEmpty(marketPairs))
      {
        this.MarketList = this.ExchangeMarketList;
      }
      else
      {
        // Since PT 2.0 the main market is no longer included in the market list so we need to rebuild the list
        List<string> originalMarketList = SystemHelper.ConvertTokenStringToList(marketPairs, ",");
        foreach (string market in originalMarketList)
        {
          this.MarketList.Add(SystemHelper.GetFullMarketName(this.LastRuntimeSummary.MainMarket, market, this.PTMagicConfiguration.GeneralSettings.Application.Exchange));
        }
      }
    }

    private void ValidateMarketList()
    {
      // Check if markets are valid for the selected main market
      if (!this.MarketList.Any(m => m.IndexOf(this.LastRuntimeSummary.MainMarket, StringComparison.InvariantCultureIgnoreCase) > -1))
      {
        Exception ex = new Exception("No valid pairs found for main market '" + this.LastRuntimeSummary.MainMarket + "' in configured pars list (" + SystemHelper.ConvertListToTokenString(this.MarketList, ",", true) + ")! Terminating process...");
        Log.DoLogError(ex.Message);
        this.State = Constants.PTMagicBotState_Idle;
        this.Timer.Stop();
        throw ex;
      }
    }



    private void InitializeMarketTrendData()
    {
      // Exchange 24-hour snapshots are still used by SMS price/volume checks
      // and compatibility summaries. No legacy price returns are calculated.
      this._recentMarketSnapshots = BaseAnalyzer.GetMarketDataFromFile(
        this.PTMagicConfiguration, this.Log, "Exchange", DateTime.UtcNow, "Recent");
      this.SingleMarketTrendChanges = new Dictionary<string, List<MarketTrendChange>>();
      this.GlobalMarketTrendChanges = new Dictionary<string, List<MarketTrendChange>>();
      this.AverageMarketTrendChanges = new Dictionary<string, double>();
    }

    // Bridge the shared normalized PriceTrend to legacy Monitor and summary
    // fields without recomputing historical exchange price snapshots.
    private void ApplyNormalizedPriceTrends()
    {
      foreach (KeyValuePair<string, MarketTrendAnalysisSnapshot> entry in
        BinanceFuturesAnalyticsRuntime.CurrentTrendAnalyses)
      {
        MarketTrendAnalysisSnapshot analysis = entry.Value;
        List<MarketTrendChange> changes = new List<MarketTrendChange>();
        foreach (KeyValuePair<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> asset in analysis.AssetMetrics)
        {
          if (!asset.Value.TryGetValue(MarketTrendMetrics.PriceTrend, out FeatureValueSnapshot feature)
            || feature == null || !feature.IsValid || feature.IsStale
            || !feature.Value.HasValue || !double.IsFinite(feature.Value.Value))
          {
            continue;
          }
          this._recentMarketSnapshots.TryGetValue(asset.Key, out Market recent);
          changes.Add(new MarketTrendChange
          {
            MarketTrendName = entry.Key,
            Market = asset.Key,
            LastPrice = recent?.Price ?? 0,
            Volume24h = recent?.Volume24h ?? 0,
            TrendMinutes = analysis.TrendMinutes,
            TrendChange = feature.Value.Value,
            TrendDateTime = analysis.CalculatedAtUtc.UtcDateTime
          });
        }
        if (changes.Count > 0)
        {
          this.SingleMarketTrendChanges[entry.Key] = changes;
          HashSet<string> global = new HashSet<string>(
            analysis.GlobalSymbols, StringComparer.OrdinalIgnoreCase);
          this.GlobalMarketTrendChanges[entry.Key] =
            changes.Where(change => global.Contains(change.Market)).ToList();
        }
        if (analysis.MarketMetrics.TryGetValue(MarketTrendMetrics.PriceTrend, out FeatureValueSnapshot globalValue)
          && globalValue != null && globalValue.IsValid && !globalValue.IsStale
          && globalValue.Value.HasValue && double.IsFinite(globalValue.Value.Value))
        {
          this.AverageMarketTrendChanges[entry.Key] = globalValue.Value.Value;
        }
      }
    }

    private void CheckGlobalSettingsTriggers(ref GlobalSetting triggeredSetting, ref List<string> matchedTriggers)
    {
        _selectionIndeterminate = false;
        this.Log.DoLogInfo("Checking global settings triggers...");
        bool unavailableNormalizedTriggerFound = false;
        bool foundMatchingSetting = false;

        foreach (GlobalSetting globalSetting in this.PTMagicConfiguration.AnalyzerSettings.GlobalSettings)
        {
            // Reset triggers for each setting
            matchedTriggers = new List<string>();

            if (globalSetting.Triggers.Count > 0)
            {
                this.Log.DoLogInfo("Checking triggers for '" + globalSetting.SettingName + "'...");
                Dictionary<string, Core.MarketAnalyzer.MarketData.TriggerTruthValue> triggerResults =
                  new Dictionary<string, Core.MarketAnalyzer.MarketData.TriggerTruthValue>(StringComparer.OrdinalIgnoreCase);
                List<Core.MarketAnalyzer.MarketData.TriggerTruthValue> orderedTriggerResults =
                  new List<Core.MarketAnalyzer.MarketData.TriggerTruthValue>();
                foreach (Trigger trigger in globalSetting.Triggers)
                {
                    MarketTrend marketTrend = this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.MarketTrends.Find(mt => String.Equals(mt.Name, trigger.MarketTrendName, StringComparison.OrdinalIgnoreCase));
                    if (marketTrend != null)
                    {
                        // Omitted Metric uses normalized PriceTrend; named metrics use the same engine.
                        bool useNormalizedMetric = Core.MarketAnalyzer.MarketData.MarketTrendMetricTriggerResolver.UsesNormalizedMetric(marketTrend, trigger.Metric, this.PTMagicConfiguration.GeneralSettings.Application.Exchange);
                        double resolvedMetricValue = 0;
                        string metricError = "";
                        bool hasMetricValue = useNormalizedMetric &&
                          Core.MarketAnalyzer.MarketData.MarketTrendMetricTriggerResolver.TryResolveGlobal(
                            this.PTMagicConfiguration, marketTrend, trigger.Metric, out resolvedMetricValue, out metricError);
                        this.CaptureDecisionMetric(marketTrend, trigger.Metric, null, trigger.MarketTrendRelation,
                          hasMetricValue ? resolvedMetricValue : (double?)null, metricError,
                          "GlobalSetting", globalSetting.SettingName, "#" + (orderedTriggerResults.Count + 1));
                        if (hasMetricValue)
                        {
                            double averageMarketTrendChange = resolvedMetricValue;
                            bool isTriggered = averageMarketTrendChange >= trigger.MinChange && averageMarketTrendChange < trigger.MaxChange;
                            Core.MarketAnalyzer.MarketData.TriggerTruthValue value = isTriggered
                              ? Core.MarketAnalyzer.MarketData.TriggerTruthValue.True
                              : Core.MarketAnalyzer.MarketData.TriggerTruthValue.False;
                            orderedTriggerResults.Add(value);
                            if (!String.IsNullOrWhiteSpace(trigger.Tag))
                            {
                              triggerResults[trigger.Tag] = value;
                            }

                            if (isTriggered)
                            {
                                // Trigger met!
                                this.Log.DoLogInfo("Trigger '" + trigger.MarketTrendName + (useNormalizedMetric ? "' [" + Core.MarketAnalyzer.MarketData.MarketTrendMetrics.Normalize(trigger.Metric) + "]" : "'") + " triggered! Value = " + averageMarketTrendChange.ToString("#,#0.00", new System.Globalization.CultureInfo("en-US")) + (useNormalizedMetric ? Core.MarketAnalyzer.MarketData.MarketTrendMetricTriggerResolver.GetUnit(trigger.Metric) : "%"));

                                string triggerContent = trigger.MarketTrendName +
                                  (useNormalizedMetric ? " [" + Core.MarketAnalyzer.MarketData.MarketTrendMetrics.Normalize(trigger.Metric) + "]" : "") + " - ";
                                string triggerUnit = useNormalizedMetric
                                  ? Core.MarketAnalyzer.MarketData.MarketTrendMetricTriggerResolver.GetUnit(trigger.Metric)
                                  : "%";
                                if (trigger.MinChange != Constants.MinTrendChange)
                                {
                                    triggerContent += " - Min: " + trigger.MinChange.ToString("#,#0.00", new System.Globalization.CultureInfo("en-US")) + triggerUnit;
                                }

                                if (trigger.MaxChange != Constants.MaxTrendChange)
                                {
                                    triggerContent += " - Max: " + trigger.MaxChange.ToString("#,#0.00", new System.Globalization.CultureInfo("en-US")) + triggerUnit;
                                }

                                matchedTriggers.Add(triggerContent);
                            }
                            else
                            {
                                this.Log.DoLogDebug("Trigger '" + trigger.MarketTrendName + "' not triggered. Value = " + averageMarketTrendChange.ToString("#,#0.00", new System.Globalization.CultureInfo("en-US")) + (useNormalizedMetric ? Core.MarketAnalyzer.MarketData.MarketTrendMetricTriggerResolver.GetUnit(trigger.Metric) : "%"));
                            }
                        }
                        else
                        {
                            Core.MarketAnalyzer.MarketData.TriggerTruthValue unavailableValue = Core.MarketAnalyzer.MarketData.TriggerTruthValue.Unavailable;
                            orderedTriggerResults.Add(unavailableValue);
                            if (!String.IsNullOrWhiteSpace(trigger.Tag))
                            {
                              triggerResults[trigger.Tag] = unavailableValue;
                            }
                            this._raidMetricWarnings.ReportUnavailable(
                              trigger.MarketTrendName, trigger.Metric, metricError,
                              "GlobalSetting", globalSetting.SettingName, null, trigger.MarketTrendRelation,
                              String.IsNullOrWhiteSpace(trigger.Tag) ? "(untagged)" : "tag=" + trigger.Tag);
                        }
                    }
                    else
                    {
                        this.CaptureDecisionMetric(new MarketTrend { Name = trigger.MarketTrendName }, trigger.Metric, null,
                          trigger.MarketTrendRelation, null, "Referenced MarketTrend does not exist.",
                          "GlobalSetting", globalSetting.SettingName, "#" + (orderedTriggerResults.Count + 1));
                        orderedTriggerResults.Add(Core.MarketAnalyzer.MarketData.TriggerTruthValue.Unavailable);
                        if (!String.IsNullOrWhiteSpace(trigger.Tag))
                        {
                          triggerResults[trigger.Tag] = Core.MarketAnalyzer.MarketData.TriggerTruthValue.Unavailable;
                        }
                        this._raidMetricWarnings.ReportUnavailable(trigger.MarketTrendName, trigger.Metric,
                          "Referenced MarketTrend does not exist.", "GlobalSetting", globalSetting.SettingName,
                          null, trigger.MarketTrendRelation, trigger.Tag);
                    }
                }

                // Evaluate the complete Boolean expression using three-valued
                // logic. An unavailable metric is unknown, but a conclusive
                // true OR unknown remains true (and false AND unknown is false).
                // A negated unknown never becomes true.
                Core.MarketAnalyzer.MarketData.TriggerTruthValue settingResult;
                try
                {
                    settingResult = Core.MarketAnalyzer.MarketData.ThreeValuedTriggerLogic.Evaluate(
                        globalSetting.TriggerConnection,
                        orderedTriggerResults,
                        triggerResults);
                }
                catch (Exception ex)
                {
                    this.Log.DoLogError("ERROR: Trigger Connection for global setting " +
                      globalSetting.SettingName + " is invalid or missing. Program halted. " + ex.Message);
                    Environment.Exit(1);
                    return;
                }

                if (settingResult == Core.MarketAnalyzer.MarketData.TriggerTruthValue.True)
                {
                    triggeredSetting = globalSetting;
                    foundMatchingSetting = true;
                    break;
                }
                if (settingResult == Core.MarketAnalyzer.MarketData.TriggerTruthValue.Unavailable)
                {
                    unavailableNormalizedTriggerFound = true;
                    // Each unavailable metric is already collected for the raid summary.
                }
            }
        }

        // A missing normalized measurement is not evidence that the active
        // regime ended. Hold the existing setting when no fully evaluated
        // setting matched, instead of falling back to default by accident.
        if (!foundMatchingSetting && unavailableNormalizedTriggerFound)
        {
            _selectionIndeterminate = true;
            GlobalSetting active = this.PTMagicConfiguration.AnalyzerSettings.GlobalSettings
              .Find(setting => setting.SettingName.Equals(this.ActiveSettingName, StringComparison.OrdinalIgnoreCase))
              ?? this.ActiveSetting;
            if (active != null)
            {
                triggeredSetting = active;
                matchedTriggers = new List<string>();
                this._raidMetricWarnings.ReportGlobalDecision("selection indeterminate; retained '" + active.SettingName + "'.");
            }
            else
            {
                this._raidMetricWarnings.ReportGlobalDecision("selection indeterminate; no prior setting is known, so delivery is deferred.");
            }
        }
    }

    private void ActivateSetting(ref GlobalSetting triggeredSetting, ref List<string> matchedTriggers)
    {
      // Prepare the desired preset. Applied state is committed only after delivery.
      bool changed = !String.Equals(this.ActiveSettingName, triggeredSetting.SettingName, StringComparison.OrdinalIgnoreCase);
      if (changed && !this.EnforceSettingsReapply && !_firstSuccessfulRaidPending
        && this.LastSettingsChange > DateTime.UtcNow.AddMinutes(-PTMagicConfiguration.GeneralSettings.Application.FloodProtectionMinutes))
      {
        this.LastRuntimeSummary.FloodProtectedSetting = triggeredSetting;
        triggeredSetting = this.ActiveSetting;
        matchedTriggers = new List<string>();
        changed = false;
        this.Log.DoLogInfo("Flood protection retained the confirmed global setting '" + this.ActiveSettingName + "'.");
      }
      this.GlobalSettingWritten = this.EnforceSettingsReapply || changed;
      if (this.GlobalSettingWritten)
        SettingsHandler.CompileProperties(this, triggeredSetting, changed ? DateTime.Now : this.LastSettingsChange.ToLocalTime());
      this.LastRuntimeSummary.CurrentGlobalSetting = triggeredSetting;
      this.ActiveSetting = triggeredSetting;
      this.ActiveSettingName = triggeredSetting.SettingName;
      _desiredGlobalSettingName = triggeredSetting.SettingName;
    }

    private void ApplySingleMarketSettings()
    {
      if (this.PTMagicConfiguration.AnalyzerSettings.SingleMarketSettings.Count > 0)
      {
        this.Log.DoLogInfo("Checking single market settings triggers for " + this.MarketList.Count.ToString() + " markets...");

        int marketPairProcess = 1;
        Dictionary<string, List<string>> matchedMarketTriggers = new Dictionary<string, List<string>>();
        // Metric warnings are aggregated in the raid summary.
        string mainMarket = this.LastRuntimeSummary.MainMarket;

        List<(SingleMarketSetting Setting, HashSet<string> IgnoredMarkets, HashSet<string> AllowedMarkets, HashSet<string> IgnoredGlobalSettings, HashSet<string> AllowedGlobalSettings)> marketSettingFilters =
          new List<(SingleMarketSetting Setting, HashSet<string> IgnoredMarkets, HashSet<string> AllowedMarkets, HashSet<string> IgnoredGlobalSettings, HashSet<string> AllowedGlobalSettings)>();

        foreach (SingleMarketSetting marketSetting in this.PTMagicConfiguration.AnalyzerSettings.SingleMarketSettings)
        {
          string ignored = marketSetting.IgnoredMarkets.ToUpper();
          ignored = ignored.Replace(mainMarket, "");


          string allowed = marketSetting.AllowedMarkets.ToUpper();
          allowed = allowed.Replace(mainMarket, "");


          marketSettingFilters.Add((
            marketSetting,
            new HashSet<string>(SystemHelper.ConvertTokenStringToList(ignored, ","), StringComparer.Ordinal),
            new HashSet<string>(SystemHelper.ConvertTokenStringToList(allowed, ","), StringComparer.Ordinal),
            new HashSet<string>(SystemHelper.ConvertTokenStringToList(marketSetting.IgnoredGlobalSettings, ","), StringComparer.Ordinal),
            new HashSet<string>(SystemHelper.ConvertTokenStringToList(marketSetting.AllowedGlobalSettings, ","), StringComparer.Ordinal)));
        }

        // Loop through markets
        foreach (string marketPair in this.MarketList)
        {
          this.Log.DoLogDebug("'" + marketPair + "' - Checking triggers (" + marketPairProcess.ToString() + "/" + this.MarketList.Count.ToString() + ")...");
          string market = marketPair.Replace(mainMarket, "");

          bool stopTriggers = false;

          // Loop through single market settings
          foreach (var marketSettingFilter in marketSettingFilters)
          {
            SingleMarketSetting marketSetting = marketSettingFilter.Setting;
            List<string> matchedSingleMarketTriggers = new List<string>();

            // Check ignore markets
            if (marketSettingFilter.IgnoredMarkets.Contains(market))
            {
              this.Log.DoLogDebug("'" + marketPair + "' - Is ignored in '" + marketSetting.SettingName + "'.");
              continue;
            }

            // Check allowed markets
            if (marketSettingFilter.AllowedMarkets.Count > 0 && !marketSettingFilter.AllowedMarkets.Contains(market))
            {
              this.Log.DoLogDebug("'" + marketPair + "' - Is not allowed in '" + marketSetting.SettingName + "'.");
              continue;
            }

            // Check ignore global settings
            if (marketSettingFilter.IgnoredGlobalSettings.Contains(this.ActiveSettingName))
            {
              this.Log.DoLogDebug("'" + marketPair + "' - '" + this.ActiveSettingName + "' - Is ignored in '" + marketSetting.SettingName + "'.");
              continue;
            }

            // Check allowed global settings
            if (marketSettingFilter.AllowedGlobalSettings.Count > 0 && !marketSettingFilter.AllowedGlobalSettings.Contains(this.ActiveSettingName))
            {
              this.Log.DoLogDebug("'" + marketPair + "' - '" + this.ActiveSettingName + "' - Is not allowed in '" + marketSetting.SettingName + "'.");
              continue;
            }

            // Trigger checking
            SingleMarketSettingSummary smss = this.SingleMarketSettingSummaries.Find(s => s.Market.Equals(marketPair, StringComparison.InvariantCultureIgnoreCase) && s.SingleMarketSetting.SettingName.Equals(marketSetting.SettingName, StringComparison.InvariantCultureIgnoreCase));
            if (smss != null)
            {
              if (marketSetting.OffTriggers != null)
              {
                if (marketSetting.OffTriggers.Count > 0)
                {

                  this.Log.DoLogDebug("'" + marketPair + "' - Checking off triggers '" + marketSetting.SettingName + "'...");

                  List<bool?> offTriggerResults = new List<bool?>();
                  int offTriggerIndex = 0;
                  foreach (OffTrigger offTrigger in marketSetting.OffTriggers)
                  {
                    offTriggerIndex++;
                    if (offTrigger.HoursSinceTriggered > 0)
                    {
                      // Check for Activation time period trigger
                      int smsActiveHours = (int)Math.Floor(DateTime.UtcNow.Subtract(smss.ActivationDateTimeUTC).TotalHours);
                      this.CaptureDecisionObservation("HoursSinceTriggered", marketPair, smsActiveHours, "",
                        "SMS OffTrigger", marketSetting.SettingName, "#" + offTriggerIndex);
                      if (smsActiveHours >= offTrigger.HoursSinceTriggered)
                      {

                        // Trigger met!
                        this.Log.DoLogDebug("'" + marketPair + "' - SMS already active for  " + smsActiveHours.ToString() + " hours. Trigger matched!");

                        offTriggerResults.Add(true);
                      }
                      else
                      {
                        // Trigger not met!
                        this.Log.DoLogDebug("'" + marketPair + "' - SMS only active for  " + smsActiveHours.ToString() + " hours. Trigger not matched!");

                        offTriggerResults.Add(false);
                      }
                    }
                    else if (offTrigger.Min24hVolume > 0 || offTrigger.Max24hVolume < Constants.Max24hVolume)
                    {
                      // Check for 24h volume trigger
                      if (this._recentMarketSnapshots.TryGetValue(marketPair, out Market recentMarket)
                        && double.IsFinite(recentMarket.Volume24h) && recentMarket.Volume24h >= 0)
                      {
                        double volume24h = recentMarket.Volume24h;
                        this.CaptureDecisionObservation("Volume24h", marketPair, volume24h, "",
                          "SMS OffTrigger", marketSetting.SettingName, "#" + offTriggerIndex);
                        {
                          if (volume24h >= offTrigger.Min24hVolume && volume24h <= offTrigger.Max24hVolume)
                          {
                            // Trigger met!
                            this.Log.DoLogDebug("'" + marketPair + "' - 24h volume off trigger matched! 24h volume = " + volume24h.ToString(new System.Globalization.CultureInfo("en-US")) + " " + this.LastRuntimeSummary.MainMarket);

                            offTriggerResults.Add(true);
                          }
                          else
                          {
                            // Trigger not met!
                            this.Log.DoLogDebug("'" + marketPair + "' - 24h volume off trigger not matched! 24h volume = " + volume24h.ToString(new System.Globalization.CultureInfo("en-US")) + " " + this.LastRuntimeSummary.MainMarket);

                            offTriggerResults.Add(false);
                          }
                        }
                      }
                      else
                      {
                        offTriggerResults.Add(null);
                        this.CaptureDecisionObservation("Volume24h", marketPair, null, "Recent market volume is unavailable.",
                          "SMS OffTrigger", marketSetting.SettingName, "#" + offTriggerIndex);
                      }
                    }
                    else
                    {
                      // Missing Metric retains PriceTrend and legacy relation semantics via the normalized resolver.
                      MarketTrend metricTrend = this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.MarketTrends
                        .Find(mt => mt.Name.Equals(offTrigger.MarketTrendName, StringComparison.OrdinalIgnoreCase));
                      if (Core.MarketAnalyzer.MarketData.MarketTrendMetricTriggerResolver.UsesNormalizedMetric(metricTrend, offTrigger.Metric, this.PTMagicConfiguration.GeneralSettings.Application.Exchange))
                      {
                        double metricValue;
                        string metricError;
                        this._recentMarketSnapshots.TryGetValue(marketPair, out Market currentMarket);
                        bool validMetric = Core.MarketAnalyzer.MarketData.MarketTrendMetricTriggerResolver.TryResolveSingleWithRelation(
                          this.PTMagicConfiguration, metricTrend, offTrigger.Metric, marketPair,
                          offTrigger.MarketTrendRelation, currentMarket?.Price ?? 0,
                          smss?.TriggerSnapshot?.LastPrice ?? 0,
                          out metricValue, out metricError);
                        this.CaptureDecisionMetric(metricTrend, offTrigger.Metric, marketPair,
                          offTrigger.MarketTrendRelation, validMetric ? metricValue : (double?)null, metricError,
                          "SMS OffTrigger", marketSetting.SettingName, "#" + offTriggerIndex);
                        if (!validMetric)
                        {
                          this._raidMetricWarnings.ReportUnavailable(
                            offTrigger.MarketTrendName, offTrigger.Metric, metricError,
                            "SMS OffTrigger", marketSetting.SettingName, marketPair,
                            offTrigger.MarketTrendRelation, "#" + offTriggerIndex);
                        }
                        offTriggerResults.Add(validMetric
                          ? (bool?)(metricValue >= offTrigger.MinChange && metricValue < offTrigger.MaxChange)
                          : null);
                      }
                      else
                      {
                        offTriggerResults.Add(null);
                        this.CaptureDecisionMetric(metricTrend ?? new MarketTrend { Name = offTrigger.MarketTrendName },
                          offTrigger.Metric, marketPair, offTrigger.MarketTrendRelation, null,
                          "Referenced MarketTrend is missing or cannot supply normalized metrics.",
                          "SMS OffTrigger", marketSetting.SettingName, "#" + offTriggerIndex);
                        this._raidMetricWarnings.ReportUnavailable(offTrigger.MarketTrendName, offTrigger.Metric,
                          "Referenced MarketTrend is missing or cannot supply normalized metrics.",
                          "SMS OffTrigger", marketSetting.SettingName, marketPair,
                          offTrigger.MarketTrendRelation, "#" + offTriggerIndex);
                      }
                    }
                  }

                  // A conclusive true OffTrigger expression may deactivate SMS.
                  // Unknown cannot deactivate; true OR unknown is still true.
                  // OffTriggerConnection has historically supported only
                  // AND/OR. Preserve that contract: unsupported values cannot
                  // deactivate an active SMS.
                  bool hasSupportedOffConnection =
                    String.Equals(marketSetting.OffTriggerConnection?.Trim(), "AND", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(marketSetting.OffTriggerConnection?.Trim(), "OR", StringComparison.OrdinalIgnoreCase);
                  TriggerTruthValue offResult = hasSupportedOffConnection
                    ? Core.MarketAnalyzer.MarketData.ThreeValuedTriggerLogic.Evaluate(
                      marketSetting.OffTriggerConnection,
                      offTriggerResults.Select(value => !value.HasValue
                        ? Core.MarketAnalyzer.MarketData.TriggerTruthValue.Unavailable
                        : value.Value
                          ? Core.MarketAnalyzer.MarketData.TriggerTruthValue.True
                          : Core.MarketAnalyzer.MarketData.TriggerTruthValue.False).ToList())
                    : TriggerTruthValue.Unavailable;
                  bool settingOffTriggered = offResult == TriggerTruthValue.True;
                  if (offResult == TriggerTruthValue.Unavailable) _selectionIndeterminate = true;
                  // Setting got off triggered, remove it from the summary
                  if (settingOffTriggered)
                  {
                    this.Log.DoLogDebug("'" + marketPair + "' - '" + marketSetting.SettingName + "' off triggered!");
                    this.SingleMarketSettingSummaries.Remove(smss);
                    smss = null;
                  }
                  else
                  {
                    this.Log.DoLogDebug("'" + marketPair + "' - '" + marketSetting.SettingName + "' not off triggered!");
                  }
                }
                else
                {
                  this.Log.DoLogDebug("'" + marketPair + "' - '" + marketSetting.SettingName + "' has no off triggers; reevaluating activation.");
                }
              }
            }

            // Do we have triggers 
            if (marketSetting.Triggers.Count > 0 && !stopTriggers)
            {
              #region Checking Triggers
              this.Log.DoLogDebug("'" + marketPair + "' - Checking triggers for '" + marketSetting.SettingName + "'...");

              List<bool?> triggerResults = new List<bool?>();
              Dictionary<int, double> relevantTriggers = new Dictionary<int, double>();
              int triggerIndex = 0;

              // Create a dictionary to store the tag and its corresponding result
              Dictionary<string, bool?> triggerTagsResults = new Dictionary<string, bool?>();
              // Initialize all tags with a value of false
              foreach (Trigger trigger in marketSetting.Triggers)
              {
                  triggerTagsResults[trigger.Tag] = false;
              }

              // Loop through SMS triggers
              foreach (Trigger trigger in marketSetting.Triggers)
              {

                if (trigger.Min24hVolume > 0 || trigger.Max24hVolume < Constants.Max24hVolume)
                {
                  #region Check for 24h volume trigger
                  if (this._recentMarketSnapshots.TryGetValue(marketPair, out Market recentMarket)
                    && double.IsFinite(recentMarket.Volume24h) && recentMarket.Volume24h >= 0)
                  {
                    double volume24h = recentMarket.Volume24h;
                    this.CaptureDecisionObservation("Volume24h", marketPair, volume24h, "",
                      "SMS activation", marketSetting.SettingName, "#" + (triggerIndex + 1));
                    {

                      if (volume24h >= trigger.Min24hVolume && volume24h <= trigger.Max24hVolume)
                      {
                        // Trigger met!
                        this.Log.DoLogDebug("'" + marketPair + "' - 24h volume trigger matched! 24h volume = " + volume24h.ToString(new System.Globalization.CultureInfo("en-US")) + " " + this.LastRuntimeSummary.MainMarket);

                        relevantTriggers.Add(triggerIndex, volume24h);

                        string triggerContent = "24h Volume";
                        if (trigger.Min24hVolume > 0)
                        {
                          triggerContent += " - Min: " + trigger.Min24hVolume.ToString(new System.Globalization.CultureInfo("en-US")) + " " + this.LastRuntimeSummary.MainMarket;
                        }

                        if (trigger.Max24hVolume < Constants.Max24hVolume)
                        {
                          triggerContent += " - Max: " + trigger.Max24hVolume.ToString(new System.Globalization.CultureInfo("en-US")) + " " + this.LastRuntimeSummary.MainMarket;
                        }

                        matchedSingleMarketTriggers.Add(marketSetting.SettingName + ": " + triggerContent + " - 24h volume = " + volume24h.ToString(new System.Globalization.CultureInfo("en-US")) + " " + this.LastRuntimeSummary.MainMarket);

                        triggerResults.Add(true);
                        triggerTagsResults[trigger.Tag] = true;
                      }
                      else
                      {
                        this.Log.DoLogDebug("'" + marketPair + "' - 24h volume trigger not matched. 24h volume = " + volume24h.ToString(new System.Globalization.CultureInfo("en-US")) + " " + this.LastRuntimeSummary.MainMarket);
                        triggerResults.Add(false);
                      }
                    }
                  }
                  else
                  {
                    triggerResults.Add(null);
                    triggerTagsResults[trigger.Tag] = null;
                    this.CaptureDecisionObservation("Volume24h", marketPair, null, "Recent market volume is unavailable.",
                      "SMS activation", marketSetting.SettingName, "#" + (triggerIndex + 1));
                  }

                  #endregion
                }
                else if (trigger.AgeDaysLowerThan > 0)
                {
                  #region Check for age trigger
                  MarketInfo marketInfo = null;
                  if (this.MarketInfos.ContainsKey(marketPair))
                  {
                    marketInfo = this.MarketInfos[marketPair];
                  }

                  if (marketInfo != null && marketInfo.FirstSeen > Constants.confMinDate
                    && marketInfo.FirstSeen <= DateTime.UtcNow)
                  {
                    int marketAge = (int)Math.Floor(DateTime.UtcNow.Subtract(marketInfo.FirstSeen).TotalDays);
                    this.CaptureDecisionObservation("MarketAgeDays", marketPair, marketAge, "",
                      "SMS activation", marketSetting.SettingName, "#" + (triggerIndex + 1));
                    if (marketAge < trigger.AgeDaysLowerThan)
                    {
                      matchedSingleMarketTriggers.Add(marketSetting.SettingName + ": '" + marketPair + "' is only " + marketAge.ToString() + " days old on this exchange. Trigger matched!");
                      this.Log.DoLogDebug("'" + marketPair + "' - Is only " + marketAge.ToString() + " days old on this exchange. Trigger matched!");

                      relevantTriggers.Add(triggerIndex, marketAge);
                      triggerResults.Add(true);
                      triggerTagsResults[trigger.Tag] = true;
                    }
                    else
                    {
                      this.Log.DoLogDebug("'" + marketPair + "' - Age Trigger not triggered. Is already " + marketAge.ToString() + " days old on this exchange.");
                      triggerResults.Add(false);
                    }
                  }
                  else
                  {
                    this.Log.DoLogDebug("'" + marketPair + "' - Age unavailable; trigger remains indeterminate.");
                    triggerResults.Add(null);
                    triggerTagsResults[trigger.Tag] = null;
                    this.CaptureDecisionObservation("MarketAgeDays", marketPair, null, "Market age is unavailable.",
                      "SMS activation", marketSetting.SettingName, "#" + (triggerIndex + 1));
                  }

                  #endregion
                }
                else
                {
                  #region Check for market trend triggers
                  MarketTrend metricTrend = this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.MarketTrends
                    .Find(mt => mt.Name.Equals(trigger.MarketTrendName, StringComparison.OrdinalIgnoreCase));
                  if (Core.MarketAnalyzer.MarketData.MarketTrendMetricTriggerResolver.UsesNormalizedMetric(metricTrend, trigger.Metric, this.PTMagicConfiguration.GeneralSettings.Application.Exchange))
                  {
                    double metricValue;
                    string metricError;
                    this._recentMarketSnapshots.TryGetValue(marketPair, out Market currentMarket);
                    bool validMetric = Core.MarketAnalyzer.MarketData.MarketTrendMetricTriggerResolver.TryResolveSingleWithRelation(
                      this.PTMagicConfiguration, metricTrend, trigger.Metric, marketPair,
                      trigger.MarketTrendRelation, currentMarket?.Price ?? 0, 0,
                      out metricValue, out metricError);
                    this.CaptureDecisionMetric(metricTrend, trigger.Metric, marketPair,
                      trigger.MarketTrendRelation, validMetric ? metricValue : (double?)null, metricError,
                      "SMS activation", marketSetting.SettingName, "#" + (triggerIndex + 1));
                    bool metricTriggered = validMetric && metricValue >= trigger.MinChange && metricValue < trigger.MaxChange;
                    triggerResults.Add(validMetric ? (bool?)metricTriggered : null);
                    triggerTagsResults[trigger.Tag] = validMetric ? (bool?)metricTriggered : null;
                    if (metricTriggered)
                    {
                      relevantTriggers.Add(triggerIndex, metricValue);
                      matchedSingleMarketTriggers.Add(marketSetting.SettingName + ": " + trigger.MarketTrendName +
                        " [" + Core.MarketAnalyzer.MarketData.MarketTrendMetrics.Normalize(trigger.Metric) + "] - Value: " +
                        metricValue.ToString("#,#0.00", new System.Globalization.CultureInfo("en-US")) +
                        Core.MarketAnalyzer.MarketData.MarketTrendMetricTriggerResolver.GetUnit(trigger.Metric));
                    }
                    else if (!validMetric)
                    {
                      this._raidMetricWarnings.ReportUnavailable(
                        trigger.MarketTrendName, trigger.Metric, metricError,
                        "SMS activation", marketSetting.SettingName, marketPair,
                        trigger.MarketTrendRelation,
                        String.IsNullOrWhiteSpace(trigger.Tag) ? "#" + (triggerIndex + 1) : "tag=" + trigger.Tag);
                    }
                  }
                  
                  else
                  {
                    triggerResults.Add(null);
                    triggerTagsResults[trigger.Tag] = null;
                    this.CaptureDecisionMetric(metricTrend ?? new MarketTrend { Name = trigger.MarketTrendName },
                      trigger.Metric, marketPair, trigger.MarketTrendRelation, null,
                      "Referenced MarketTrend is missing or cannot supply normalized metrics.",
                      "SMS activation", marketSetting.SettingName, "#" + (triggerIndex + 1));
                    this._raidMetricWarnings.ReportUnavailable(trigger.MarketTrendName, trigger.Metric,
                      "Referenced MarketTrend is missing or cannot supply normalized metrics.",
                      "SMS activation", marketSetting.SettingName, marketPair,
                      trigger.MarketTrendRelation, "#" + (triggerIndex + 1));
                  }
                  #endregion
                }
                triggerIndex++;
              } // End loop SMS triggers


              // The complete condition, not any single trigger, determines
              // eligibility. Unknown branches can be masked by true OR /
              // false AND, but a final unknown cannot freshly activate SMS.
              bool settingTriggered;
              bool activationIndeterminate;
              try
              {
                Core.MarketAnalyzer.MarketData.TriggerTruthValue smsResult =
                  Core.MarketAnalyzer.MarketData.ThreeValuedTriggerLogic.Evaluate(
                    marketSetting.TriggerConnection,
                    triggerResults.Select(value => !value.HasValue
                      ? Core.MarketAnalyzer.MarketData.TriggerTruthValue.Unavailable
                      : value.Value
                        ? Core.MarketAnalyzer.MarketData.TriggerTruthValue.True
                        : Core.MarketAnalyzer.MarketData.TriggerTruthValue.False).ToList(),
                    triggerTagsResults.ToDictionary(pair => pair.Key,
                      pair => !pair.Value.HasValue
                        ? Core.MarketAnalyzer.MarketData.TriggerTruthValue.Unavailable
                        : pair.Value.Value
                          ? Core.MarketAnalyzer.MarketData.TriggerTruthValue.True
                          : Core.MarketAnalyzer.MarketData.TriggerTruthValue.False));
                settingTriggered = smsResult == Core.MarketAnalyzer.MarketData.TriggerTruthValue.True;
                activationIndeterminate = smsResult == TriggerTruthValue.Unavailable;
                if (activationIndeterminate) _selectionIndeterminate = true;
              }
              catch (Exception ex)
              {
                this.Log.DoLogError("ERROR: Trigger Connection for single market setting " +
                  marketSetting.SettingName + " is invalid or missing. Program halted. " + ex.Message);
                Environment.Exit(1);
                return;
              }
              #endregion

              bool isFreshTrigger = true;

              // Missing observations cannot prove that a previously applied SMS
              // ended. Keep its activation snapshot until the result is known.
              if (!settingTriggered && activationIndeterminate && smss != null)
              {
                settingTriggered = true;
                isFreshTrigger = false;
                matchedSingleMarketTriggers = MatchedMarketTriggerContent.CopyForSnapshot(
                  smss.TriggerSnapshot?.MatchedTriggersContent ?? new List<string>());
              }

              // Setting not triggered -> Check if it is already active as a long term SMS using Off Triggers
              if (!settingTriggered)
              {
                this.Log.DoLogDebug("'" + marketPair + "' - SMS '" + marketSetting.SettingName + "' not triggered, checking for long term activation.");
                if (smss != null)
                {
                  if (marketSetting.OffTriggers != null)
                  {
                    if (marketSetting.OffTriggers.Count > 0)
                    {
                      this.Log.DoLogDebug("'" + marketPair + "' - SMS '" + marketSetting.SettingName + "' has off triggers, starting special trigger...");
                      // Setting already active and using off triggers -> set as triggered
                      settingTriggered = true;
                      isFreshTrigger = false;

                      matchedSingleMarketTriggers = new List<string>();
                      foreach (string matchedTriggerContent in smss.TriggerSnapshot.MatchedTriggersContent)
                      {
                        if (matchedTriggerContent.StartsWith(marketSetting.SettingName + ":"))
                        {
                          matchedSingleMarketTriggers.Add(matchedTriggerContent);
                        }
                      }

                      int removalLength = matchedSingleMarketTriggers.Count - marketSetting.Triggers.Count;
                      if (removalLength > 0)
                      {
                        matchedSingleMarketTriggers.RemoveRange(0, removalLength);
                      }

                      this.Log.DoLogDebug("'" + marketPair + "' - Activating SMS '" + marketSetting.SettingName + "' as off triggers are not met.");
                    }
                  }
                }
              }

              // Setting got triggered -> Activate it!
              if (settingTriggered)
              {
                this.Log.DoLogDebug("'" + marketPair + "' - '" + marketSetting.SettingName + "' triggered!");

                // Aggregate comment-line triggers without sharing a mutable
                // list with this setting's persisted trigger snapshot.
                MatchedMarketTriggerContent.Append(matchedMarketTriggers, marketPair, matchedSingleMarketTriggers);

                if (!this.TriggeredSingleMarketSettings.ContainsKey(marketPair))
                {
                  List<SingleMarketSetting> smsList = new List<SingleMarketSetting>();
                  smsList.Add(marketSetting);
                  this.TriggeredSingleMarketSettings.Add(marketPair, smsList);
                }
                else
                {
                  this.TriggeredSingleMarketSettings[marketPair].Add(marketSetting);
                }

                // Counting triggered setting
                if (!this.SingleMarketSettingsCount.ContainsKey(marketSetting.SettingName))
                {
                  this.SingleMarketSettingsCount.Add(marketSetting.SettingName, 1);
                }
                else
                {
                  this.SingleMarketSettingsCount[marketSetting.SettingName]++;
                }

                if (isFreshTrigger)
                {
                  this.Log.DoLogDebug("'" + marketPair + "' - SMS '" + marketSetting.SettingName + "' saving summary data...");

                  // Check if this setting is already active for this market
                  if (smss == null || marketSetting.RefreshOffTriggers)
                  {
                    if (smss == null)
                    {
                      smss = new SingleMarketSettingSummary();
                    }
                    else
                    {
                      this.SingleMarketSettingSummaries.Remove(smss);
                    }

                    smss.ActivationDateTimeUTC = DateTime.UtcNow;
                    smss.Market = marketPair;
                    smss.SingleMarketSetting = marketSetting;
                    smss.TriggerSnapshot = new TriggerSnapshot();
                    smss.TriggerSnapshot.Last24hVolume = 0;
                    smss.TriggerSnapshot.LastPrice = 0;
                    smss.TriggerSnapshot.RelevantTriggers = relevantTriggers;
                    smss.TriggerSnapshot.MatchedTriggersContent = MatchedMarketTriggerContent.CopyForSnapshot(matchedSingleMarketTriggers);

                    if (this._recentMarketSnapshots.TryGetValue(marketPair, out Market recentMarket))
                    {
                      smss.TriggerSnapshot.Last24hVolume = recentMarket.Volume24h;
                      smss.TriggerSnapshot.LastPrice = recentMarket.Price;
                    }

                    this.SingleMarketSettingSummaries.Add(smss);

                    this.Log.DoLogDebug("'" + marketPair + "' - SMS '" + marketSetting.SettingName + "' summary data saved.");
                  }
                  else
                  {
                    this.Log.DoLogDebug("'" + marketPair + "' - SMS '" + marketSetting.SettingName + "' already active for this market and no refresh allowed.");
                  }
                }

                // Stop processing other settings if configured
                if (marketSetting.StopProcessWhenTriggered)
                {
                  stopTriggers = true;
                }
              }
              else
              {
                if (smss != null) this.SingleMarketSettingSummaries.Remove(smss);
                this.Log.DoLogDebug("'" + marketPair + "' - '" + marketSetting.SettingName + "' not triggered!");
              }
            }
          } // End loop single market settings

          if ((marketPairProcess % 10) == 0)
          {
            this.Log.DoLogInfo("What are you looking at? " + marketPairProcess + "/" + this.MarketList.Count + " markets done...");
          }

          marketPairProcess++;
        } // End loop through markets

        // Persist only SMS that are actually part of this raid's desired
        // configuration; filtered/removed settings must not remain latched.
        this.SingleMarketSettingSummaries.RemoveAll(summary =>
          !this.TriggeredSingleMarketSettings.TryGetValue(summary.Market, out List<SingleMarketSetting> active)
          || !active.Any(setting => String.Equals(setting.SettingName, summary.SingleMarketSetting.SettingName,
            StringComparison.OrdinalIgnoreCase)));

        // Did we trigger any SMS?
        if (this.TriggeredSingleMarketSettings.Count > 0)
        {
          this.Log.DoLogInfo("Building single market settings for '" + this.TriggeredSingleMarketSettings.Count.ToString() + "' markets...");

          // Write single market settings
          var newSingleMarketSettings = SettingsHandler.CompileSingleMarketProperties(this, matchedMarketTriggers);

          // Compare against last run to see if they have changed or not
          if (_lastActiveSingleMarketSettings == null || haveSingleMarketSettingsHaveChanged(_lastActiveSingleMarketSettings, newSingleMarketSettings))
          {
            // Single market settings differ from the last raid, so update
            this.SingleMarketSettingChanged = true;
          }

          // Buffer for next raid
          _lastActiveSingleMarketSettings = newSingleMarketSettings;

          this.Log.DoLogInfo("Building single market settings completed.");
        }
        else
        {
          this.Log.DoLogInfo("No settings triggered for single markets.");

          // Remove single market settings if no triggers are met - if necessary
          bool hadAppliedSms = _lastActiveSingleMarketSettings != null && _lastActiveSingleMarketSettings.Count > 0;
          this.SingleMarketSettingChanged = SettingsHandler.RemoveSingleMarketSettings(this) || hadAppliedSms;
          _lastActiveSingleMarketSettings = new List<KeyValuePair<string, string>>();
        }
      }
      else
      {
        this.Log.DoLogInfo("No single market settings found.");
        this.SingleMarketSettingSummaries.Clear();
        this.SingleMarketSettingChanged = SettingsHandler.RemoveSingleMarketSettings(this)
          || (_lastActiveSingleMarketSettings != null && _lastActiveSingleMarketSettings.Count > 0);
        _lastActiveSingleMarketSettings = new List<KeyValuePair<string, string>>();
      }
    }

    private bool haveSingleMarketSettingsHaveChanged(List<KeyValuePair<string, string>> oldMarketTriggers, List<KeyValuePair<string, string>> newMarketTriggers)
    {
      // Check if the SMS settings have changed between raids
      string oldSms = "", newSms = "";

      foreach (var entry in oldMarketTriggers)
      {
        oldSms += string.Format("{0}: {1}|", entry.Key, entry.Value);
      }

      foreach (var entry in newMarketTriggers)
      {
        newSms += string.Format("{0}: {1}|", entry.Key, entry.Value);
      }

      return !string.Equals(oldSms, newSms, StringComparison.OrdinalIgnoreCase);
    }

    private string DeliveryCheckpointPath => Path.Combine(Directory.GetCurrentDirectory(), Constants.PTMagicPathData, "ConfigurationDeliveryState.json");
    private string StartupGuardCheckpointPath => Path.Combine(Directory.GetCurrentDirectory(), Constants.PTMagicPathData, "StartupSellOnlyModeState.json");

    private string StartupGuardDestinationFingerprint()
    {
      Application app = this.PTMagicConfiguration.GeneralSettings.Application;
      // Ownership follows the destinations, not a particular token or preset.
      return ConfigurationPayload.Hash(JsonConvert.SerializeObject(new[]
      {
        app.ProfitTrailerMonitorURL?.Trim().TrimEnd('/'), app.ProfitTrailerMonitorURLXtra?.Trim()
      }));
    }

    private string DestinationFingerprint()
    {
      Application app = this.PTMagicConfiguration.GeneralSettings.Application;
      return ConfigurationPayload.Hash(JsonConvert.SerializeObject(new[]
      {
        app.ProfitTrailerMonitorURL, app.ProfitTrailerMonitorURLXtra,
        app.ProfitTrailerLicense, app.ProfitTrailerLicenseXtra, app.ProfitTrailerDefaultSettingName
      }));
    }

    private void InitializeDeliveryTracking()
    {
      _deliveryDestinationHash = DestinationFingerprint();
      _deliveryTestMode = this.PTMagicConfiguration.GeneralSettings.Application.TestMode;
      ConfigurationDeliveryState state = null;
      try
      {
        state = ConfigurationDeliveryCoordinator.Load(DeliveryCheckpointPath);
        if (state != null && state.DestinationHash != DestinationFingerprint()) state = null;
      }
      catch (Exception exception)
      {
        this.Log.DoLogWarn("Configuration acknowledgement checkpoint could not be restored: " + exception.GetType().Name + ".");
      }
      _configurationDelivery = new ConfigurationDeliveryCoordinator(state);
      _firstSuccessfulRaidPending = true;
      StartupSellOnlyModeState guardState = null;
      try
      {
        guardState = StartupSellOnlyModeState.Load(StartupGuardCheckpointPath);
        if (guardState != null && guardState.DestinationHash != StartupGuardDestinationFingerprint()) guardState = null;
      }
      catch (Exception exception)
      {
        this.Log.DoLogWarn("Startup global SOM checkpoint could not be restored: " + exception.GetType().Name + ".");
      }
      _startupGuardPending = guardState?.PendingRelease == true;
      _startupGuardActive = _startupGuardPending && guardState?.EnabledAcknowledged == true;
      _startupGuardStatus = _startupGuardPending ? "Pending restart reconciliation" : "Inactive";
      _startupGuardDelivery = ConfigurationDeliveryResult.Deferred("Startup global SOM has not been checked in this process.");
      _lastEffectivePayload = null;
      GlobalSetting confirmed = this.PTMagicConfiguration.AnalyzerSettings.GlobalSettings
        .Find(setting => String.Equals(setting.SettingName, state?.AppliedSettingName, StringComparison.OrdinalIgnoreCase));
      if (confirmed != null)
      {
        this.ActiveSetting = confirmed;
        this.ActiveSettingName = confirmed.SettingName;
        this.LastSettingsChange = state.AppliedAtUtc?.UtcDateTime ?? Constants.confMinDate;
      }
    }

    private void EnsureDeliveryContext()
    {
      bool testMode = this.PTMagicConfiguration.GeneralSettings.Application.TestMode;
      if (_deliveryDestinationHash != DestinationFingerprint() || (_deliveryTestMode.HasValue && _deliveryTestMode.Value != testMode))
        this.InitializeDeliveryTracking();
      _deliveryTestMode = testMode;
    }

    private ConfigurationDeliveryResult SendConfiguration(ConfigurationPayload payload) =>
      _configurationSender != null ? _configurationSender(payload) :
      SettingsAPI.SendPropertyLinesToAPI(payload.Pairs.ToList(), payload.Dca.ToList(), payload.Indicators.ToList(),
        this.PTMagicConfiguration, this.Log,
        canSend: () => (_decisionFresh = this.IsRaidFresh(out _)));

    private ConfigurationDeliveryResult SendGlobalSellOnlyMode(bool enabled)
    {
      if (this.PTMagicConfiguration.GeneralSettings.Application.TestMode)
        return new ConfigurationDeliveryResult { Succeeded = true, Simulated = true,
          Status = enabled ? "TestMode global SOM enabled" : "TestMode global SOM released" };
      return _globalSellOnlyModeSender != null ? _globalSellOnlyModeSender(enabled)
        : GlobalSellOnlyModeAPI.Set(this.PTMagicConfiguration, enabled, this.Log,
          canSend: () => enabled || (_decisionFresh = this.IsRaidFresh(out _)));
    }

    private void SaveDeliveryCheckpoint()
    {
      if (this.PTMagicConfiguration.GeneralSettings.Application.TestMode) return;
      try { _configurationDelivery.SaveConfirmed(DeliveryCheckpointPath); }
      catch (Exception exception)
      {
        this.Log.DoLogError("PT acknowledged configuration, but its checkpoint could not be saved: " + exception.GetType().Name + ".");
      }
    }

    private ConfigurationPayload _lastEffectivePayload;

    private void SaveStartupGuardCheckpoint()
    {
      if (this.PTMagicConfiguration.GeneralSettings.Application.TestMode) return;
      new StartupSellOnlyModeState
      {
        DestinationHash = StartupGuardDestinationFingerprint(), PendingRelease = _startupGuardPending,
        EnabledAcknowledged = _startupGuardActive
      }.Save(StartupGuardCheckpointPath);
    }

    private void SaveAcknowledgedStartupGuardCheckpoint()
    {
      try { this.SaveStartupGuardCheckpoint(); }
      catch (Exception exception)
      {
        // An HTTP acknowledgement remains real even if its metadata cannot be
        // saved. A prior pending marker causes conservative replay on restart.
        this.Log.DoLogError("Global SOM command acknowledged, but its checkpoint could not be saved: " + exception.GetType().Name + ".");
      }
    }

    private void EnsureStartupSellOnlyMode()
    {
      if (!_firstSuccessfulRaidPending && !_startupGuardPending) return;
      if (!this.PTMagicConfiguration.GeneralSettings.Application.StartupSellOnlyMode && !_startupGuardPending) return;
      _startupGuardPending = true;
      _startupGuardStatus = "Enable pending";
      // Persist the intent before the API call, covering a crash/timeout after
      // PT accepted true but before acknowledgement was recorded locally.
      this.SaveStartupGuardCheckpoint();
      try { _startupGuardDelivery = this.SendGlobalSellOnlyMode(true) ?? ConfigurationDeliveryResult.Failed("Global SOM transport returned no acknowledgement."); }
      catch (Exception exception) { _startupGuardDelivery = ConfigurationDeliveryResult.Failed("Global SOM transport exception: " + exception.GetType().Name); }
      if (!_startupGuardDelivery.Succeeded)
      {
        _startupGuardStatus = "Enable failed; remote state unconfirmed";
        this.Log.DoLogError(_startupGuardDelivery.Error);
        this.UpdateDeliverySummary();
        throw new InvalidOperationException("Initial global SOM was not acknowledged; market-data bootstrap is deferred until the next interval.");
      }
      _startupGuardActive = true;
      _startupGuardStatus = _startupGuardDelivery.Simulated ? "TestMode enabled" : "Enabled";
      this.SaveAcknowledgedStartupGuardCheckpoint();
      this.Log.DoLogInfo(_startupGuardDelivery.Simulated
        ? "TESTMODE: startup global SOM preview; no PT API command was sent."
        : "Startup global SOM enable acknowledged: new buys and DCA buys blocked; sells remain allowed. Loading market data.");
      this.UpdateDeliverySummary();
    }

    private void ReleaseStartupSellOnlyMode()
    {
      if (!_startupGuardPending) return;
      _decisionFresh = this.IsRaidFresh(out string reason);
      if (!_decisionFresh || _selectionIndeterminate)
      {
        _startupGuardStatus = "Release deferred; waiting for a fresh conclusive raid";
        _startupGuardDelivery = ConfigurationDeliveryResult.Deferred(reason);
        return;
      }
      try { _startupGuardDelivery = this.SendGlobalSellOnlyMode(false) ?? ConfigurationDeliveryResult.Failed("Global SOM transport returned no acknowledgement."); }
      catch (Exception exception) { _startupGuardDelivery = ConfigurationDeliveryResult.Failed("Global SOM transport exception: " + exception.GetType().Name); }
      if (!_startupGuardDelivery.Succeeded)
      {
        _startupGuardStatus = "Release failed; remote state unconfirmed";
        this.Log.DoLogWarn(_startupGuardDelivery.Error);
        this.Log.DoLogWarn("The normal configuration is acknowledged, but startup global SOM release is pending. It will be reasserted before the next load and retried after a fresh conclusive raid.");
        return;
      }
      _startupGuardActive = false;
      _startupGuardPending = false;
      _startupGuardStatus = _startupGuardDelivery.Simulated ? "TestMode released" : "Released";
      this.SaveAcknowledgedStartupGuardCheckpoint();
      this.Log.DoLogInfo(_startupGuardDelivery.Simulated
        ? "TESTMODE: startup global SOM release preview; no PT API command was sent."
        : "Startup global SOM disable acknowledged after fresh analysis and confirmed configuration delivery.");
    }

    private bool IsRaidFresh(out string reason)
    {
      // Even a volume/age-only configuration needs a completed, current raid.
      // Never reuse the preceding raid's timestamp before this one has built.
      if (!_raidAnalysisBuilt)
      {
        reason = "The current raid has not completed its market-data analysis.";
        return false;
      }
      return BinanceFuturesAnalyticsRuntime.IsDecisionFresh(this.PTMagicConfiguration, out reason);
    }

    private void RestoreRaidState()
    {
      if (_raidPriorSetting != null)
      {
        this.ActiveSetting = _raidPriorSetting;
        this.ActiveSettingName = _raidPriorSetting.SettingName;
        this.LastSettingsChange = _raidPriorSettingsChange;
        if (this.LastRuntimeSummary != null) this.LastRuntimeSummary.CurrentGlobalSetting = _raidPriorSetting;
      }
      if (_raidPriorSms != null) this.SingleMarketSettingSummaries = _raidPriorSms;
      this.TriggeredSingleMarketSettings = _raidPriorSms == null
        ? new Dictionary<string, List<SingleMarketSetting>>()
        : _raidPriorSms.Where(summary => summary?.SingleMarketSetting != null)
          .GroupBy(summary => summary.Market, StringComparer.OrdinalIgnoreCase)
          .ToDictionary(group => group.Key, group => group.Select(summary => summary.SingleMarketSetting).ToList(), StringComparer.OrdinalIgnoreCase);
      this.SingleMarketSettingsCount = this.TriggeredSingleMarketSettings.Values.SelectMany(settings => settings)
        .GroupBy(setting => setting.SettingName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
      if (_lastEffectivePayload != null)
      {
        this.PairsLines = _lastEffectivePayload.Pairs.ToList();
        this.DCALines = _lastEffectivePayload.Dca.ToList();
        this.IndicatorsLines = _lastEffectivePayload.Indicators.ToList();
      }
      else if (this.ActiveSetting != null)
      {
        try { SettingsHandler.CompileProperties(this, this.ActiveSetting, this.LastSettingsChange.ToLocalTime()); }
        catch (Exception exception) { this.Log.DoLogWarn("Unable to reconstruct the previous preset for display: " + exception.GetType().Name + ". Confirmed delivery was not changed."); }
      }
    }

    private void SaveProfitTrailerProperties()
    {
      _desiredGlobalSettingName = this.ActiveSettingName;
      _desiredSms = this.TriggeredSingleMarketSettings
        .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
        .SelectMany(pair => pair.Value.Select(setting => pair.Key + "/" + setting.SettingName)).ToArray();
      ConfigurationPayload desired = new ConfigurationPayload(this.ActiveSettingName, this.PairsLines,
        this.DCALines, this.IndicatorsLines, DestinationFingerprint());
      _desiredPayloadHash = desired.PayloadHash;
      _decisionFresh = this.IsRaidFresh(out string freshnessReason);
      if (!_decisionFresh || ((_firstSuccessfulRaidPending || _startupGuardPending) && _selectionIndeterminate))
      {
        _raidDelivery = ConfigurationDeliveryResult.Deferred(!_decisionFresh ? freshnessReason :
          "Initial selection is indeterminate; the confirmed startup guard or prior configuration remains in place.");
        this.RestoreRaidState();
        this.UpdateDeliverySummary();
        this.Log.DoLogWarn("PT configuration deferred: " + _raidDelivery.Error);
        return;
      }

      // Compare the full desired payload against the last acknowledgement. This
      // covers removal of the last SMS and retries even when selection is unchanged.
      _raidDelivery = _configurationDelivery.Deliver(desired, SendConfiguration,
        this.PTMagicConfiguration.GeneralSettings.Application.TestMode,
        this.EnforceSettingsReapply || _firstSuccessfulRaidPending);
      if (_raidDelivery.Succeeded)
      {
        _normalDecisionCommittedThisRaid = true;
        _firstSuccessfulRaidPending = false;
        _lastEffectivePayload = desired;
        bool switched = !String.Equals(_raidPriorSetting?.SettingName, this.ActiveSettingName, StringComparison.OrdinalIgnoreCase);
        if (switched || this.GlobalSettingWritten) this.LastSettingsChange = DateTime.UtcNow;
        if (switched) this.LastRuntimeSummary.LastGlobalSettingSwitch = this.LastRuntime;
        this.SaveDeliveryCheckpoint();
        this.ReleaseStartupSellOnlyMode();
        this.Log.DoLogInfo(_raidDelivery.Simulated
          ? "TESTMODE: configuration evaluated; no updates sent to PT."
          : _raidDelivery.Attempted ? "Complete configuration acknowledged by all PT targets."
          : "Confirmed PT configuration unchanged; no update required.");
        if (switched && !_raidDelivery.Simulated && this.PTMagicConfiguration.GeneralSettings.Telegram.IsEnabled)
        {
          try
          {
            TelegramHelper.SendMessage(this.PTMagicConfiguration.GeneralSettings.Telegram.BotToken,
              this.PTMagicConfiguration.GeneralSettings.Telegram.ChatId,
              this.PTMagicConfiguration.GeneralSettings.Application.InstanceName + ": Setting applied: " + this.ActiveSettingName + ".",
              this.PTMagicConfiguration.GeneralSettings.Telegram.SilentMode, this.Log);
          }
          catch (Exception exception) { this.Log.DoLogCritical("Failed to send setting confirmation", exception); }
        }
      }
      else
      {
        this.RestoreRaidState();
        this.Log.DoLogError("PT configuration delivery failed; the desired update remains pending and will be retried after a fresh raid.");
      }
      this.UpdateDeliverySummary();
    }

    private void UpdateDeliverySummary()
    {
      if (this.LastRuntimeSummary == null) return;
      this.LastRuntimeSummary.DesiredGlobalSettingName = _desiredGlobalSettingName;
      this.LastRuntimeSummary.AppliedGlobalSettingName = _configurationDelivery.Confirmed.AppliedSettingName;
      this.LastRuntimeSummary.ConfigurationDeliveryStatus = _raidDelivery.Status;
      this.LastRuntimeSummary.StartupSellOnlyModeActive = _startupGuardActive;
      this.LastRuntimeSummary.StartupSellOnlyModePending = _startupGuardPending;
      this.LastRuntimeSummary.StartupSellOnlyModeStatus = _startupGuardStatus;
      this.LastRuntimeSummary.AnalysisAsOfUtc = _raidAnalysisBuilt ? BinanceFuturesAnalyticsRuntime.CurrentRaidAsOfUtc : null;
      this.LastRuntimeSummary.SourceRevision = BuildIdentity.SourceRevision;
      this.LastRuntimeSummary.CurrentGlobalSetting ??= this.ActiveSetting;
    }

    private void PersistFailedRaidSummary()
    {
      // Failures before the full market summary must still be visible in the
      // Monitor. Preserve the last market data while updating delivery state.
      try
      {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), Constants.PTMagicPathData);
        string path = Path.Combine(directory, "LastRuntimeSummary.json");
        Summary summary = File.Exists(path)
          ? JsonConvert.DeserializeObject<Summary>(File.ReadAllText(path)) ?? new Summary() : new Summary();
        summary.LastRuntime = this.LastRuntime;
        summary.LastRuntimeSeconds = (int)Math.Max(0, Math.Ceiling((DateTime.UtcNow - this.LastRuntime).TotalSeconds));
        summary.Version = this.CurrentVersion?.ToString() ?? "";
        summary.SourceRevision = BuildIdentity.SourceRevision;
        summary.CurrentGlobalSetting = this.ActiveSetting;
        summary.DesiredGlobalSettingName = _desiredGlobalSettingName;
        summary.AppliedGlobalSettingName = _configurationDelivery.Confirmed.AppliedSettingName;
        summary.ConfigurationDeliveryStatus = _raidDelivery.Status;
        summary.StartupSellOnlyModeActive = _startupGuardActive;
        summary.StartupSellOnlyModePending = _startupGuardPending;
        summary.StartupSellOnlyModeStatus = _startupGuardStatus;
        summary.AnalysisAsOfUtc = _raidAnalysisBuilt ? BinanceFuturesAnalyticsRuntime.CurrentRaidAsOfUtc : null;
        Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonConvert.SerializeObject(summary));
        File.Move(temporary, path, true);
      }
      catch (Exception exception)
      {
        this.Log.DoLogWarn("Failed raid status could not be saved: " + exception.GetType().Name + ".");
      }
    }

    private void CaptureDecisionMetric(MarketTrend trend, string metric, string symbol, string relation,
      double? value, string reason, string scope, string settingName, string triggerId)
    {
      if (!this.PTMagicConfiguration.GeneralSettings.Application.DecisionHistoryEnabled) return;
      string name = MarketTrendMetrics.Normalize(metric);
      BinanceFuturesAnalyticsRuntime.TryGetMetric(trend?.Name, name, symbol, out FeatureValueSnapshot feature);
      string key = scope + "|" + settingName + "|" + triggerId + "|" + (trend?.Name ?? "")
        + "|" + name + "|" + (symbol ?? "") + "|" + (relation ?? "");
      _decisionInputs[key] = new DecisionMetricInput
      {
        Scope = scope, SettingName = settingName, TriggerId = triggerId,
        MarketTrend = trend?.Name, Metric = name, Market = symbol, Relation = relation,
        Value = value.HasValue && double.IsFinite(value.Value) ? value : null,
        Available = value.HasValue && double.IsFinite(value.Value), Reason = reason ?? "",
        SourceDataTimestampUtc = feature?.SourceDataTimestampUtc, CoveragePercent = feature?.CoveragePercent
      };
    }

    private void CaptureDecisionObservation(string metric, string symbol, double? value, string reason,
      string scope, string settingName, string triggerId)
    {
      if (!this.PTMagicConfiguration.GeneralSettings.Application.DecisionHistoryEnabled) return;
      string key = scope + "|" + settingName + "|" + triggerId + "|" + metric + "|" + symbol;
      _decisionInputs[key] = new DecisionMetricInput
      {
        Scope = scope, SettingName = settingName, TriggerId = triggerId,
        Metric = metric, Market = symbol, Value = value.HasValue && double.IsFinite(value.Value) ? value : null,
        Available = value.HasValue && double.IsFinite(value.Value), Reason = reason
      };
    }

    private void RecordDecisionHistory(string errorCategory = "")
    {
      if (_historyWritten || this.PTMagicConfiguration?.GeneralSettings?.Application == null) return;
      Application app = this.PTMagicConfiguration.GeneralSettings.Application;
      if (!app.DecisionHistoryEnabled) return;
      try
      {
        DecisionHistoryRecord record = new DecisionHistoryRecord
        {
          RaidId = _raidId, StartedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(this.LastRuntime, DateTimeKind.Utc)),
          RecordedAtUtc = DateTimeOffset.UtcNow, ApplicationVersion = this.CurrentVersion?.ToString() ?? "",
          SourceRevision = BuildIdentity.SourceRevision, Exchange = app.Exchange,
          ConfigurationHash = ConfigurationPayload.Hash(JsonConvert.SerializeObject(new
          {
            Analyzer = this.PTMagicConfiguration.AnalyzerSettings,
            app.Exchange, app.MaxAnalysisAgeSeconds, app.StartupSellOnlyMode
          })),
          DesiredPayloadHash = _desiredPayloadHash, ConfirmedPayloadHash = _configurationDelivery.Confirmed.AppliedPayloadHash,
          AnalysisAsOfUtc = _raidAnalysisBuilt ? BinanceFuturesAnalyticsRuntime.CurrentRaidAsOfUtc : null,
          DecisionFresh = _decisionFresh, SelectionIndeterminate = _selectionIndeterminate,
          StartupSellOnlyModeActive = _startupGuardActive, StartupSellOnlyModePending = _startupGuardPending,
          StartupSellOnlyModeStatus = _startupGuardStatus, TestMode = app.TestMode,
          DesiredGlobalSetting = _desiredGlobalSettingName, ConfirmedGlobalSetting = _configurationDelivery.Confirmed.AppliedSettingName,
          DesiredSingleMarketSettings = _desiredSms, DeliveryStatus = _raidDelivery.Status,
          ErrorCategory = String.IsNullOrWhiteSpace(errorCategory) ? (_raidDelivery.Succeeded ? "" : _raidDelivery.Status) : errorCategory,
          MetricInputs = _decisionInputs.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Value).ToArray(),
          Universes = !_raidAnalysisBuilt ? Array.Empty<DecisionTrendUniverse>() :
            BinanceFuturesAnalyticsRuntime.CurrentTrendAnalyses.Values.Select(analysis => new DecisionTrendUniverse
            {
              Name = analysis.MarketTrendName, TrendMinutes = analysis.TrendMinutes, AsOfUtc = analysis.CalculatedAtUtc,
              ExpectedClosedCandleTimeUtc = analysis.ExpectedClosedCandleTimeUtc,
              GlobalSymbols = analysis.GlobalSymbols.ToArray(), AssetSymbols = analysis.AssetSymbols.ToArray()
            }).ToArray()
        };
        new DecisionHistoryWriter(Path.Combine(Directory.GetCurrentDirectory(), Constants.PTMagicPathData, "DecisionHistory"))
          .Append(record, app.DecisionHistoryRetentionDays, app.DecisionHistoryMaxMegabytes);
        _historyWritten = true;
      }
      catch (Exception exception)
      {
        this.Log.DoLogWarn("Decision history could not be appended: " + exception.GetType().Name + ". Configuration delivery status is unchanged.");
      }
    }

    private string SmsSummaryPath => Path.Combine(Directory.GetCurrentDirectory(), Constants.PTMagicPathData,
      this.PTMagicConfiguration.GeneralSettings.Application.TestMode
        ? "SingleMarketSettingSummary.TestMode.json" : "SingleMarketSettingSummary.json");

    private void SaveSingleMarketSettingsSummary()
    {
      JsonSerializerSettings smsSummaryJsonSettings = new JsonSerializerSettings();
      smsSummaryJsonSettings.NullValueHandling = NullValueHandling.Ignore;
      smsSummaryJsonSettings.DefaultValueHandling = DefaultValueHandling.Ignore;

      FileHelper.WriteTextToFile(Path.GetDirectoryName(SmsSummaryPath) + Path.DirectorySeparatorChar,
        Path.GetFileName(SmsSummaryPath), JsonConvert.SerializeObject(this.SingleMarketSettingSummaries, Formatting.None, smsSummaryJsonSettings));

      this.Log.DoLogInfo("Single Market Settings Summary saved.");
    }

    private void SaveRuntimeSummary(int elapsedSeconds)
    {
      this.Log.DoLogInfo("Building LastRuntimeSummary.json for your monitor...");

      this.LastRuntimeSummary.LastRuntimeSeconds = elapsedSeconds;

      // Load existing runtime summary and read ongoing data
      if (File.Exists(Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + Constants.PTMagicPathData + Path.DirectorySeparatorChar + "LastRuntimeSummary.json"))
      {
        try
        {
          Summary summary = JsonConvert.DeserializeObject<Summary>(System.IO.File.ReadAllText(Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + Constants.PTMagicPathData + Path.DirectorySeparatorChar + "LastRuntimeSummary.json"));
          if (summary != null)
          {

            // Last setting switch in case the app got restarted and has no history
            if (this.LastRuntimeSummary.LastGlobalSettingSwitch == Constants.confMinDate)
            {
              this.LastRuntimeSummary.LastGlobalSettingSwitch = summary.LastGlobalSettingSwitch;
            }

            // Market trend changes history for graph data
            foreach (string key in summary.MarketTrendChanges.Keys)
            {
              // Do not rehydrate obsolete legacy graph values for Metrics-only
              // trends whose legacy averages were deliberately not calculated.
              if (!this.AverageMarketTrendChanges.ContainsKey(key))
              {
                continue;
              }

              this.LastRuntimeSummary.MarketTrendChanges.Add(key, summary.MarketTrendChanges[key].FindAll(mtc => mtc.TrendDateTime >= DateTime.UtcNow.AddHours(-PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.StoreDataMaxHours)));
            }

            // Global setting summary to be kept
            this.LastRuntimeSummary.GlobalSettingSummary.AddRange(summary.GlobalSettingSummary.FindAll(gss => gss.SwitchDateTime >= DateTime.UtcNow.AddHours(-96)));

            this.Log.DoLogInfo("Summary: Loaded old LastRuntimeSummary.json to keep data.");
          }
        }
        catch (Exception ex)
        {
          this.Log.DoLogCritical("Summary: Error loading old summary (" + ex.Message + "). Creating new one.", ex);
        }
      }

      this.Log.DoLogInfo("Summary: Building global settings summary...");

      // Change setting summary
      GlobalSettingSummary lastSettingSummary = null;
      if (this.LastRuntimeSummary.LastGlobalSettingSwitch == this.LastRuntimeSummary.LastRuntime || this.LastRuntimeSummary.GlobalSettingSummary.Count == 0)
      {

        // Setting got switched this run, add a new setting summary
        GlobalSettingSummary gss = new GlobalSettingSummary();
        gss.SettingName = this.LastRuntimeSummary.CurrentGlobalSetting.SettingName;
        gss.SwitchDateTime = this.LastRuntimeSummary.LastRuntime.ToUniversalTime();

        if (this.LastRuntimeSummary.GlobalSettingSummary.Count > 0)
        {
          lastSettingSummary = this.LastRuntimeSummary.GlobalSettingSummary.OrderByDescending(lss => lss.SwitchDateTime).First();
          lastSettingSummary.ActiveSeconds = (int)Math.Ceiling(DateTime.UtcNow.Subtract(lastSettingSummary.SwitchDateTime).TotalSeconds);
        }

        this.LastRuntimeSummary.GlobalSettingSummary.Add(gss);

        lastSettingSummary = this.LastRuntimeSummary.GlobalSettingSummary.OrderByDescending(lss => lss.SwitchDateTime).First();
      }
      else
      {

        // Setting did not get switched, update data
        if (this.LastRuntimeSummary.GlobalSettingSummary.Count > 0)
        {
          lastSettingSummary = this.LastRuntimeSummary.GlobalSettingSummary.OrderByDescending(lss => lss.SwitchDateTime).First();
          lastSettingSummary.ActiveSeconds = (int)Math.Ceiling(DateTime.UtcNow.Subtract(lastSettingSummary.SwitchDateTime).TotalSeconds);
        }
      }

      this.Log.DoLogInfo("Summary: Built global settings summary.");

      this.Log.DoLogInfo("Summary: Save market trend changes for summary.");
      // Save market trend changes for the summary
      foreach (string key in this.AverageMarketTrendChanges.Keys)
      {
        List<MarketTrendChange> mtChanges = new List<MarketTrendChange>();
        if (this.LastRuntimeSummary.MarketTrendChanges.ContainsKey(key))
        {
          mtChanges = this.LastRuntimeSummary.MarketTrendChanges[key];
        }

        MarketTrendChange newChange = new MarketTrendChange();
        newChange.MarketTrendName = key;
        newChange.TrendChange = this.AverageMarketTrendChanges[key];
        newChange.TrendDateTime = this.LastRuntimeSummary.LastRuntime;

        mtChanges.Add(newChange);
        if (lastSettingSummary != null)
        {
          if (!lastSettingSummary.MarketTrendChanges.ContainsKey(key))
          {
            GlobalSetting gs = this.PTMagicConfiguration.AnalyzerSettings.GlobalSettings.Find(g => g.SettingName.Equals(lastSettingSummary.SettingName));
            if (gs != null)
            {
              if (gs.SettingName.Equals("Default", StringComparison.InvariantCultureIgnoreCase) || gs.Triggers.Find(t => t.MarketTrendName.Equals(key)) != null)
              {
                lastSettingSummary.MarketTrendChanges.Add(key, newChange);
              }
            }
          }
        }

        this.LastRuntimeSummary.MarketTrendChanges[key] = mtChanges;
      }
      this.Log.DoLogInfo("Summary: Market trends saved.");

      this.Log.DoLogInfo("Summary: Getting current global properties...");

      // Get current global settings from PAIRS.PROPERTIES
      Dictionary<string, string> pairsProperties = SettingsHandler.GetPropertiesAsDictionary(this.PairsLines);
      Dictionary<string, string> dcaProperties = SettingsHandler.GetPropertiesAsDictionary(this.DCALines);

      string defaultBuyValueString = SettingsHandler.GetCurrentPropertyValue(pairsProperties, "ALL_buy_value", "DEFAULT_A_buy_value");
      double defaultBuyValue = SystemHelper.TextToDouble(defaultBuyValueString, 0, "en-US");

      string defaultTrailingBuyString = SettingsHandler.GetCurrentPropertyValue(pairsProperties, "ALL_trailing_buy", "DEFAULT_trailing_buy");
      double defaultTrailingBuy = SystemHelper.TextToDouble(defaultTrailingBuyString, 0, "en-US");

      string defaultSellValueString = SettingsHandler.GetCurrentPropertyValue(pairsProperties, "ALL_sell_value", "DEFAULT_A_sell_value");
      double defaultSellValue = SystemHelper.TextToDouble(defaultSellValueString, 0, "en-US");

      string defaultTrailingProfitString = SettingsHandler.GetCurrentPropertyValue(pairsProperties, "ALL_trailing_profit", "DEFAULT_trailing_profit");
      double defaultTrailingProfit = SystemHelper.TextToDouble(defaultTrailingProfitString, 0, "en-US");

      string defaultMaxTradingPairsString = SettingsHandler.GetCurrentPropertyValue(pairsProperties, "ALL_max_trading_pairs", "max_trading_pairs");
      double defaultMaxTradingPairs = SystemHelper.TextToDouble(defaultMaxTradingPairsString, 0, "en-US");

      string defaultMaxCostString = SettingsHandler.GetCurrentPropertyValue(pairsProperties, "ALL_max_cost", "DEFAULT_initial_cost");
      double defaultMaxCost = SystemHelper.TextToDouble(defaultMaxCostString, 0, "en-US");

      string defaultMaxCostPercentageString = SettingsHandler.GetCurrentPropertyValue(pairsProperties, "ALL_max_cost_percentage", "DEFAULT_initial_cost_percentage");
      double defaultMaxCostPercentage = SystemHelper.TextToDouble(defaultMaxCostPercentageString, 0, "en-US");

      string defaultMinBuyVolumeString = SettingsHandler.GetCurrentPropertyValue(pairsProperties, "ALL_min_buy_volume", "DEFAULT_min_buy_volume");
      double defaultMinBuyVolume = SystemHelper.TextToDouble(defaultMinBuyVolumeString, 0, "en-US");

      string defaultDCALevelString = SettingsHandler.GetCurrentPropertyValue(dcaProperties, "max_buy_times", "DEFAULT_DCA_max_buy_times");
      double defaultDCALevel = SystemHelper.TextToDouble(defaultDCALevelString, 0, "en-US");

      string defaultBuyStrategy = SettingsHandler.GetCurrentPropertyValue(pairsProperties, "ALL_buy_strategy", "DEFAULT_A_buy_strategy");

      string defaultSellStrategy = SettingsHandler.GetCurrentPropertyValue(pairsProperties, "ALL_sell_strategy", "DEFAULT_A_sell_strategy");

      string defaultDCAEnabledString = SettingsHandler.GetCurrentPropertyValue(pairsProperties, "ALL_DCA_enabled", "DEFAULT_DCA_enabled");
      bool defaultDCAEnabled = (defaultDCAEnabledString.Equals("false", StringComparison.InvariantCultureIgnoreCase)) ? false : true;

      string defaultSOMActiveString = SettingsHandler.GetCurrentPropertyValue(pairsProperties, "ALL_sell_only_mode", "DEFAULT_sell_only_mode_enabled");
      bool defaultSOMActive = (defaultSOMActiveString.Equals("false", StringComparison.InvariantCultureIgnoreCase)) ? false : true;
      this.LastRuntimeSummary.IsSOMActive = defaultSOMActive;

      string dcaBuyStrategyString = SettingsHandler.GetCurrentPropertyValue(dcaProperties, "buy_strategy", "DEFAULT_DCA_A_buy_strategy");
      this.LastRuntimeSummary.DCABuyStrategy = dcaBuyStrategyString;

      this.LastRuntimeSummary.BuyValue = defaultBuyValue;
      this.LastRuntimeSummary.TrailingBuy = defaultTrailingBuy;
      this.LastRuntimeSummary.SellValue = defaultSellValue;
      this.LastRuntimeSummary.TrailingProfit = defaultTrailingProfit;
      this.LastRuntimeSummary.MaxTradingPairs = defaultMaxTradingPairs;
      this.LastRuntimeSummary.MaxCost = defaultMaxCost;
      this.LastRuntimeSummary.MaxCostPercentage = defaultMaxCostPercentage;
      this.LastRuntimeSummary.MinBuyVolume = defaultMinBuyVolume;
      this.LastRuntimeSummary.BuyStrategy = defaultBuyStrategy;
      this.LastRuntimeSummary.SellStrategy = defaultSellStrategy;
      this.LastRuntimeSummary.DCALevels = defaultDCALevel;

      double maxDCALevel = this.LastRuntimeSummary.DCALevels;
      if (maxDCALevel == 0) maxDCALevel = 1000;

      string dcaDefaultTriggerString = SettingsHandler.GetCurrentPropertyValue(dcaProperties, "buy_trigger", "DEFAULT_DCA_buy_trigger");
      double dcaDefaultTrigger = SystemHelper.TextToDouble(dcaDefaultTriggerString, 0, "en-US");

      this.LastRuntimeSummary.DCATrigger = dcaDefaultTrigger;


      // Get PROFITPERCENTAGE strategy label
      string ProfitPercentageLabel = "";
      for (char c = 'A'; c <= 'Z'; c++)
      {

        string buyStrategyName = SettingsHandler.GetCurrentPropertyValue(dcaProperties, "DEFAULT_DCA_" + c + "_buy_strategy", "");
        if (buyStrategyName.Contains("PROFITPERCENTAGE"))
        {

          ProfitPercentageLabel = "" + c;
        }
      }

      // Get configured DCA triggers
      for (int dca = 1; dca <= maxDCALevel; dca++)
      {
        string dcaTriggerString = SettingsHandler.GetCurrentPropertyValue(dcaProperties, ProfitPercentageLabel + "buy_value_" + dca.ToString(), "DEFAULT_DCA_" + ProfitPercentageLabel + "_buy_value_" + dca.ToString());
        if (!String.IsNullOrEmpty(dcaTriggerString))
        {
          double dcaTrigger = SystemHelper.TextToDouble(dcaTriggerString, 0, "en-US");

          this.LastRuntimeSummary.DCATriggers.Add(dca, dcaTrigger);
        }
        else
        {
          if (this.LastRuntimeSummary.DCALevels == 0) this.LastRuntimeSummary.DCALevels = dca - 1;
          break;
        }
      }

      // Get configured DCA percentages
      string dcaDefaultPercentageString = SettingsHandler.GetCurrentPropertyValue(dcaProperties, "DEFAULT_DCA_buy_percentage", "");
      double dcaDefaultPercentage = SystemHelper.TextToDouble(dcaDefaultPercentageString, 0, "en-US");

      this.LastRuntimeSummary.DCAPercentage = dcaDefaultPercentage;

      for (int dca = 1; dca <= maxDCALevel; dca++)
      {
        string dcaPercentageString = SettingsHandler.GetCurrentPropertyValue(dcaProperties, "DEFAULT_DCA_buy_percentage_" + dca.ToString(), "");
        if (!String.IsNullOrEmpty(dcaPercentageString))
        {
          double dcaPercentage = SystemHelper.TextToDouble(dcaPercentageString, 0, "en-US");

          this.LastRuntimeSummary.DCAPercentages.Add(dca, dcaPercentage);
        }
        else
        {
          if (this.LastRuntimeSummary.DCALevels == 0) this.LastRuntimeSummary.DCALevels = dca - 1;
          break;
        }
      }

      // Get configured Buy Strategies
      for (char c = 'A'; c <= 'Z'; c++)
      {
        string buyStrategyName = SettingsHandler.GetCurrentPropertyValue(pairsProperties, "DEFAULT_" + c + "_buy_strategy", "");
        if (!String.IsNullOrEmpty(buyStrategyName))
        {
          StrategySummary buyStrategy = new StrategySummary();
          buyStrategy.Name = buyStrategyName;
          buyStrategy.Value = SystemHelper.TextToDouble(SettingsHandler.GetCurrentPropertyValue(pairsProperties, "DEFAULT_" + c + "_buy_value", ""), 0, "en-US");

          this.LastRuntimeSummary.BuyStrategies.Add(buyStrategy);
        }
        else
        {
          break;
        }
      }

      // Get configured Sell Strategies
      for (char c = 'A'; c <= 'Z'; c++)
      {
        string sellStrategyName = SettingsHandler.GetCurrentPropertyValue(pairsProperties, "DEFAULT_" + c + "_sell_strategy", "");
        if (!String.IsNullOrEmpty(sellStrategyName))
        {
          StrategySummary sellStrategy = new StrategySummary();
          sellStrategy.Name = sellStrategyName;
          sellStrategy.Value = SystemHelper.TextToDouble(SettingsHandler.GetCurrentPropertyValue(pairsProperties, "DEFAULT_" + c + "_sell_value", ""), 0, "en-US");

          this.LastRuntimeSummary.SellStrategies.Add(sellStrategy);
        }
        else
        {
          break;
        }
      }

      // Get configured DCA Buy Strategies
      for (char c = 'A'; c <= 'Z'; c++)
      {
        string buyStrategyName = SettingsHandler.GetCurrentPropertyValue(dcaProperties, "DEFAULT_DCA_" + c + "_buy_strategy", "");
        if (!String.IsNullOrEmpty(buyStrategyName))
        {
          StrategySummary buyStrategy = new StrategySummary();
          buyStrategy.Name = buyStrategyName;
          buyStrategy.Value = SystemHelper.TextToDouble(SettingsHandler.GetCurrentPropertyValue(dcaProperties, "DEFAULT_DCA_" + c + "_buy_value", ""), 0, "en-US");

          this.LastRuntimeSummary.DCABuyStrategies.Add(buyStrategy);
        }
        else
        {
          break;
        }
      }

      // Get configured DCA Sell Strategies
      for (char c = 'A'; c <= 'Z'; c++)
      {
        string sellStrategyName = SettingsHandler.GetCurrentPropertyValue(dcaProperties, "DEFAULT_DCA_" + c + "_sell_strategy", "");
        if (!String.IsNullOrEmpty(sellStrategyName))
        {
          StrategySummary sellStrategy = new StrategySummary();
          sellStrategy.Name = sellStrategyName;
          sellStrategy.Value = SystemHelper.TextToDouble(SettingsHandler.GetCurrentPropertyValue(dcaProperties, "DEFAULT_DCA_" + c + "_sell_value", ""), 0, "en-US");

          this.LastRuntimeSummary.DCASellStrategies.Add(sellStrategy);
        }
        else
        {
          break;
        }
      }

      // Get current main currency price
      Dictionary<string, Market> recentMarkets = BaseAnalyzer.GetMarketDataFromFile(this.PTMagicConfiguration, this.Log, "Exchange", DateTime.UtcNow, "Recent");
      if (recentMarkets.Keys.Count > 0)
      {
        this.LastRuntimeSummary.MainMarketPrice = recentMarkets.First().Value.MainCurrencyPriceUSD;

        if (!this.LastRuntimeSummary.MainFiatCurrency.Equals("USD", StringComparison.InvariantCultureIgnoreCase))
        {
          this.LastRuntimeSummary.MainMarketPrice = this.LastRuntimeSummary.MainMarketPrice * this.LastRuntimeSummary.MainFiatCurrencyExchangeRate;
        }
      }

      this.Log.DoLogInfo("Summary: Current global properties saved.");

      // Get current single market settings from PAIRS.PROPERTIES for each configured market
      this.Log.DoLogInfo("Summary: Getting current single market properties...");

      foreach (string marketPair in this.MarketList)
      {
        MarketPairSummary mpSummary = new MarketPairSummary();
        mpSummary.CurrentBuyValue = defaultBuyValue;
        mpSummary.CurrentTrailingBuy = defaultTrailingBuy;
        mpSummary.CurrentSellValue = defaultSellValue;
        mpSummary.CurrentTrailingProfit = defaultTrailingProfit;
        mpSummary.IsDCAEnabled = defaultDCAEnabled;
        mpSummary.IsSOMActive = defaultSOMActive;
        mpSummary.ActiveSingleSettings = new List<SingleMarketSetting>();

        if (this.MarketList.Contains(marketPair))
        {

          // Pair is allowed for trading, check for individual values
          mpSummary.IsTradingEnabled = true;

          if (this.TriggeredSingleMarketSettings.Count > 0)
          {
            if (this.TriggeredSingleMarketSettings.ContainsKey(marketPair))
            {
              mpSummary.ActiveSingleSettings = this.TriggeredSingleMarketSettings[marketPair];
            }
          }

          string marketPairSimple = marketPair.Replace(this.LastRuntimeSummary.MainMarket, "").Replace("_", "").Replace("-", "");

          // Get configured Buy Strategies
          for (char c = 'A'; c <= 'Z'; c++)
          {
            string buyStrategyName = SettingsHandler.GetCurrentPropertyValue(pairsProperties, marketPairSimple + "_" + c + "_buy_strategy", "");
            if (!String.IsNullOrEmpty(buyStrategyName))
            {
              StrategySummary buyStrategy = new StrategySummary();
              buyStrategy.Name = buyStrategyName;
              buyStrategy.Value = SystemHelper.TextToDouble(SettingsHandler.GetCurrentPropertyValue(pairsProperties, marketPair + "_" + c + "_buy_value", ""), 0, "en-US");

              mpSummary.BuyStrategies.Add(buyStrategy);
            }
            else
            {
              break;
            }
          }
          if (mpSummary.BuyStrategies.Count == 0) mpSummary.BuyStrategies = this.LastRuntimeSummary.BuyStrategies;

          // Get configured Sell Strategies
          for (char c = 'A'; c <= 'Z'; c++)
          {
            string sellStrategyName = SettingsHandler.GetCurrentPropertyValue(pairsProperties, marketPairSimple + "_" + c + "_sell_strategy", "");
            if (!String.IsNullOrEmpty(sellStrategyName))
            {
              StrategySummary sellStrategy = new StrategySummary();
              sellStrategy.Name = sellStrategyName;
              sellStrategy.Value = SystemHelper.TextToDouble(SettingsHandler.GetCurrentPropertyValue(pairsProperties, marketPairSimple + "_" + c + "_sell_value", ""), 0, "en-US");

              mpSummary.SellStrategies.Add(sellStrategy);
            }
            else
            {
              break;
            }
          }
          if (mpSummary.SellStrategies.Count == 0) mpSummary.SellStrategies = this.LastRuntimeSummary.SellStrategies;

          string pairBuyValueString = SettingsHandler.GetCurrentPropertyValue(pairsProperties, marketPairSimple + "_buy_value", marketPairSimple + "_A_buy_value");
          double pairBuyValue = SystemHelper.TextToDouble(pairBuyValueString, 100, "en-US");
          if (pairBuyValue < 100) mpSummary.CurrentBuyValue = pairBuyValue;

          string pairTrailingBuyString = SettingsHandler.GetCurrentPropertyValue(pairsProperties, marketPair + "_trailing_buy", marketPairSimple + "_trailing_buy");
          double pairTrailingBuy = SystemHelper.TextToDouble(pairTrailingBuyString, -1, "en-US");
          if (pairTrailingBuy > -1) mpSummary.CurrentTrailingBuy = pairTrailingBuy;

          string pairSellValueString = SettingsHandler.GetCurrentPropertyValue(pairsProperties, marketPair + "_sell_value", marketPairSimple + "_A_sell_value");
          double pairSellValue = SystemHelper.TextToDouble(pairSellValueString, 0, "en-US");
          if (pairSellValue > -1) mpSummary.CurrentSellValue = pairSellValue;

          string pairTrailingProfitString = SettingsHandler.GetCurrentPropertyValue(pairsProperties, marketPair + "_trailing_profit", marketPairSimple + "_trailing_profit");
          double pairTrailingProfit = SystemHelper.TextToDouble(pairTrailingProfitString, 0, "en-US");
          if (pairTrailingProfit > -1) mpSummary.CurrentTrailingProfit = pairTrailingProfit;

          string pairTradingEnabledString = SettingsHandler.GetCurrentPropertyValue(pairsProperties, marketPair + "_trading_enabled", marketPairSimple + "_trading_enabled");
          mpSummary.IsTradingEnabled = (pairTradingEnabledString.Equals("false", StringComparison.InvariantCultureIgnoreCase)) ? false : true;

          string pairDCAEnabledString = SettingsHandler.GetCurrentPropertyValue(pairsProperties, marketPair + "_DCA_enabled", marketPairSimple + "_DCA_enabled");
          mpSummary.IsDCAEnabled = (pairDCAEnabledString.Equals("false", StringComparison.InvariantCultureIgnoreCase)) ? false : defaultDCAEnabled;

          string pairSOMActiveString = SettingsHandler.GetCurrentPropertyValue(pairsProperties, marketPair + "_sell_only_mode", marketPairSimple + "_sell_only_mode_enabled");
          mpSummary.IsSOMActive = (pairSOMActiveString.Equals("true", StringComparison.InvariantCultureIgnoreCase)) ? true : false;
        }

        // Get market trend values for each market pair
        foreach (string marketTrendName in this.SingleMarketTrendChanges.Keys)
        {
          if (this.SingleMarketTrendChanges.ContainsKey(marketTrendName))
          {
            MarketTrendChange mtc = this.SingleMarketTrendChanges[marketTrendName].Find(m => m.Market == marketPair);
            if (mtc != null)
            {
              double marketTrendChange = mtc.TrendChange;
              mpSummary.MarketTrendChanges.Add(marketTrendName, marketTrendChange);

              mpSummary.LatestPrice = mtc.LastPrice;
              mpSummary.Latest24hVolume = mtc.Volume24h;
            }
          }
        }

        if (this._recentMarketSnapshots.TryGetValue(marketPair, out Market recentMarket))
        {
          mpSummary.LatestPrice = recentMarket.Price;
          mpSummary.Latest24hVolume = recentMarket.Volume24h;
        }
        this.LastRuntimeSummary.MarketSummary.TryAdd(marketPair, mpSummary);
      }

      this.Log.DoLogInfo("Summary: Current single market properties saved.");

      string serialziedJson = JsonConvert.SerializeObject(this.LastRuntimeSummary);

      // Save the summary JSON file
      try
      {
        FileHelper.WriteTextToFile(Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + Constants.PTMagicPathData + Path.DirectorySeparatorChar, "LastRuntimeSummary.json", serialziedJson);

        this.Log.DoLogInfo("Summary: LastRuntimeSummary.json saved.");
      }
      catch (Exception ex)
      {
        this.Log.DoLogCritical("Exception while writing LastRuntimeSummary.json", ex);

        try
        {
          this.Log.DoLogInfo("Summary: Retrying one more time to save LastRuntimeSummary.json.");
          FileHelper.WriteTextToFile(Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + Constants.PTMagicPathData + Path.DirectorySeparatorChar, "LastRuntimeSummary.json", serialziedJson);

          this.Log.DoLogInfo("Summary: LastRuntimeSummary.json saved.");
        }
        catch (Exception ex2)
        {
          this.Log.DoLogCritical("Nope, another Exception while writing LastRuntimeSummary.json", ex2);
        }
      }
    }

    private void Cleanup()
    {
      // Clear down memory
      this.GlobalSettingWritten = false;
      this.SingleMarketSettingChanged = false;
      this.EnforceSettingsReapply = false;

      this.PairsLines = null;
      this.DCALines = null;
      this.IndicatorsLines = null;

      this.SingleMarketTrendChanges = new Dictionary<string, List<MarketTrendChange>>();
      this.GlobalMarketTrendChanges = new Dictionary<string, List<MarketTrendChange>>();
      this.AverageMarketTrendChanges = new Dictionary<string, double>();
      this._recentMarketSnapshots = new Dictionary<string, Market>(StringComparer.OrdinalIgnoreCase);
      this.SingleMarketSettingsCount = new Dictionary<string, int>();
      this.TriggeredSingleMarketSettings = new Dictionary<string, List<SingleMarketSetting>>();
      this.ExchangeMarketList = null;
      this.MarketList = new List<string>();
      this.LastRuntimeSummary = null;

      // Cleanup log files
      string logsPath = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + Constants.PTMagicPathLogs + Path.DirectorySeparatorChar;
      if (Directory.Exists(logsPath))
      {
        FileHelper.CleanupFiles(logsPath, 24 * 3);
        this.Log.DoLogInfo("Cleaned up logfiles.");
      }
    }
    #endregion
  }
}
