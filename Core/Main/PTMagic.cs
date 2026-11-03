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
    public PTMagic(LogHelper log)
    {
      this.Log = log;
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
    private Dictionary<string, double> _averageMarketTrendChanges = new Dictionary<string, double>();
    private Dictionary<string, List<MarketTrendChange>> _singleMarketTrendChanges = new Dictionary<string, List<MarketTrendChange>>();
    private Dictionary<string, List<MarketTrendChange>> _globalMarketTrendChanges = new Dictionary<string, List<MarketTrendChange>>();
    private Dictionary<string, int> _singleMarketSettingsCount = new Dictionary<string, int>();
    Dictionary<string, List<SingleMarketSetting>> _triggeredSingleMarketSettings = new Dictionary<string, List<SingleMarketSetting>>();
    private readonly SemaphoreSlim _raidSemaphore = new SemaphoreSlim(1, 1);
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
      var delayInSeconds = ((1d / 2d) * (Math.Pow(2d, failedAttempts) - 1d));
      return maxDelayInSeconds < delayInSeconds ? maxDelayInSeconds : (int)delayInSeconds;
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

      if (!this.RunStartupChecks()) return false;
      if (!this.InitializeConfiguration()) return false;

      bool configCheckResult = this.RunConfigurationChecks();
      if (!configCheckResult)
      {
        int configRetryCount = 1;
        while (!configCheckResult)
        {
          int delaySeconds = ExponentialDelay(configRetryCount);
          this.Log.DoLogError("Configuration check retry " + configRetryCount + " failed, starting next retry in " + delaySeconds + " seconds...");
          Thread.Sleep(delaySeconds * 1000);
          this.InitializeConfiguration();
          configCheckResult = this.RunConfigurationChecks();
          configRetryCount++;
        }
      }

      this.LastSettingFileCheck = DateTime.UtcNow;
      SettingsFiles.CheckPresets(this.PTMagicConfiguration, this.Log, true);
      SettingsFiles.PresetFileWatcher.Changed += PresetFileWatcher_OnChanged;
      SettingsFiles.PresetFileWatcher.EnableRaisingEvents = true;
      EnforceSettingsReapply = true;
      this.ActiveSetting = this.PTMagicConfiguration.AnalyzerSettings.GlobalSettings.Find(s => s.SettingName.Equals(this.DefaultSettingName, StringComparison.InvariantCultureIgnoreCase));
      this.ActiveSettingName = ActiveSetting.SettingName;
      this.LastSettingsChange = DateTime.UtcNow;
      this.StartPTMagicIntervalTimer();
      return result;
    }

    private void PresetFileWatcher_OnChanged(object source, FileSystemEventArgs e)
    {
      SettingsFiles.PresetFileWatcher.EnableRaisingEvents = false;
      this.Log.DoLogInfo("Detected a '" + e.ChangeType.ToString() + "' change in the following preset file: " + e.FullPath);
      this.EnforceSettingsReapply = true;
      PTMagicIntervalTimer_Elapsed(new object(), null);
      SettingsFiles.PresetFileWatcher.EnableRaisingEvents = true;
    }

    public bool RunStartupChecks()
    {
      bool result = true;
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
        this.Log.DoLogInfo("Configuration loaded.");
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

      if (!this.PTMagicConfiguration.GeneralSettings.Application.Exchange.Equals("Binance", StringComparison.InvariantCultureIgnoreCase)
        && !this.PTMagicConfiguration.GeneralSettings.Application.Exchange.Equals("Bittrex", StringComparison.InvariantCultureIgnoreCase)
        && !this.PTMagicConfiguration.GeneralSettings.Application.Exchange.Equals("BinanceUS", StringComparison.InvariantCultureIgnoreCase)
        && !this.PTMagicConfiguration.GeneralSettings.Application.Exchange.Equals("BinanceFutures", StringComparison.InvariantCultureIgnoreCase)
        && !this.PTMagicConfiguration.GeneralSettings.Application.Exchange.Equals("Poloniex", StringComparison.InvariantCultureIgnoreCase))
      {
        this.Log.DoLogError("Exchange '" + this.PTMagicConfiguration.GeneralSettings.Application.Exchange + "' specified in settings.general.json is invalid! Terminating process...");
        result = false;
      }

      if (this.PTMagicConfiguration.GeneralSettings.Application.IsEnabled)
      {
        result = RunProfitTrailerSettingsAPIChecks();
        try
        {
          if (this.PTMagicConfiguration.GeneralSettings.Application.TestMode) this.Log.DoLogWarn("TESTMODE ENABLED - No PT settings will be changed!");
          if (!String.IsNullOrEmpty(this.PTMagicConfiguration.GeneralSettings.Application.CoinMarketCapAPIKey)) this.Log.DoLogInfo("CoinMarketCap API KEY found");
          else this.Log.DoLogInfo("No CoinMarketCap API KEY specified! That's ok, but you can't use CoinMarketCap in your settings.analyzer.json");
        }
        catch (NullReferenceException)
        {
          this.Log.DoLogError("PTM failed to read the General Settings file. That means something in the file is either missing or incorrect.");
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
      if (!String.IsNullOrEmpty(this.PTMagicConfiguration.GeneralSettings.Application.ProfitTrailerLicense)) this.Log.DoLogInfo("Profit Trailer check: Profit Trailer license found"); else { this.Log.DoLogError("Profit Trailer check: No Profit Trailer license key specified!"); result = false; }
      if (!String.IsNullOrEmpty(this.PTMagicConfiguration.GeneralSettings.Application.ProfitTrailerServerAPIToken)) this.Log.DoLogInfo("Profit Trailer check: Profit Trailer Server API Token Specified"); else { this.Log.DoLogError("Profit Trailer check: No Server API Token specified."); result = false; }
      if (!String.IsNullOrEmpty(this.PTMagicConfiguration.GeneralSettings.Application.ProfitTrailerDefaultSettingName)) this.Log.DoLogInfo("Profit Trailer check: Profit Trailer default setting name specified"); else { this.Log.DoLogError("Profit Trailer check: No Profit Trailer default setting name specified!"); result = false; }
      if (!String.IsNullOrEmpty(this.PTMagicConfiguration.GeneralSettings.Application.ProfitTrailerMonitorURL)) this.Log.DoLogInfo("Profit Trailer check: Profit Trailer monitor URL found"); else { this.Log.DoLogError("Profit Trailer check: No Profit Trailer monitor URL specified!"); result = false; }
      if (SystemHelper.UrlIsReachable(this.PTMagicConfiguration.GeneralSettings.Application.ProfitTrailerMonitorURL)) this.Log.DoLogInfo("Profit Trailer check: Profit Trailer monitor connection test succeeded"); else { this.Log.DoLogError("Profit Trailer check: Your Profit Trailer monitor is not available!"); result = false; }
      this.Log.DoLogInfo(result ? "========== CHECKS FOR Profit Trailer COMPLETED! ==========" : "========== CHECKS FOR Profit Trailer FAILED! ==========");
      return result;
    }

    public void StartPTMagicIntervalTimer()
    {
      this.Timer = new System.Timers.Timer(this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.IntervalMinutes * 60 * 1000);
      this.Timer.Enabled = true;
      this.Timer.Elapsed += new System.Timers.ElapsedEventHandler(this.PTMagicIntervalTimer_Elapsed);
      this.Log.DoLogInfo("Checking market trends every " + this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.IntervalMinutes.ToString() + " minutes...");
      this.PTMagicIntervalTimer_Elapsed(null, null);
    }
    #endregion

    #region PTMagic Interval Methods

    public async void PTMagicIntervalTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
    {
      if (this.State != Constants.PTMagicBotState_Idle || !await _raidSemaphore.WaitAsync(0).ConfigureAwait(false)) return;
      try
      {
        try
        {
          this.State = Constants.PTMagicBotState_Running;
          this.WriteStateToFile();
          this.RunCount++;
          this.LastRuntime = DateTime.UtcNow;
          this.EnforceSettingsReapply = this.HaveSettingsChanged() || this.EnforceSettingsReapply;
          if (!PTMagicConfiguration.GeneralSettings.Application.IsEnabled) return;

          this.ValidateSettings();
          this.Log.DoLogInfo("");
          this.Log.DoLogInfo("##########################################################");
          this.Log.DoLogInfo("#********************************************************#");
          this.Log.DoLogInfo("Starting market trend check with Version " + this.CurrentVersion.Major + "." + this.CurrentVersion.Minor + "." + this.CurrentVersion.Build);
          this.LastRuntimeSummary = new Summary { LastRuntime = this.LastRuntime, Version = this.CurrentVersion.Major + "." + this.CurrentVersion.Minor + "." + this.CurrentVersion.Build };
          await this.CheckLatestGitHubVersionAsync(this.LastRuntimeSummary.Version).ConfigureAwait(false);
          this.LoadCurrentProfitTrailerProperties();
          this.LoadSMSSummaries();
          this.MarketInfos = BaseAnalyzer.GetMarketInfosFromFile(this.PTMagicConfiguration, this.Log);
          await this.BuildMarketDataAsync().ConfigureAwait(false);
          this.BuildMarketList();
          this.ValidateMarketList();
          this.BuildGlobalMarketTrends();

          GlobalSetting triggeredSetting = this.PTMagicConfiguration.AnalyzerSettings.GlobalSettings.Find(s => s.SettingName.Equals(this.DefaultSettingName, StringComparison.InvariantCultureIgnoreCase));
          List<string> matchedTriggers = new List<string>();
          this.CheckGlobalSettingsTriggers(ref triggeredSetting, ref matchedTriggers);
          this.ActivateSetting(ref triggeredSetting, ref matchedTriggers);
          this.ApplySingleMarketSettings();

          if (this.PTMagicConfiguration.GeneralSettings.Application.Exchange.Equals("BinanceFutures", StringComparison.InvariantCultureIgnoreCase))
          {
            var results = this.MarketList.FindAll(m => m.Contains("_", StringComparison.InvariantCultureIgnoreCase));
            if (results.Count > 0)
            {
              this.PairsLines.AddRange(new string[] { "", "# BinanceFutures Quarterly Contracts - Ignore list:", "###################################################" });
              foreach (var marketPair in results) this.PairsLines.Add(String.Format("{0}_trading_enabled = false", marketPair));
            }
          }

          this.SaveProfitTrailerProperties();
          this.SaveSingleMarketSettingsSummary();
          DateTime endTime = DateTime.UtcNow;
          int elapsedSeconds = (int)Math.Round(endTime.Subtract(this.LastRuntime).TotalSeconds, 0);
          this.TotalElapsedSeconds += elapsedSeconds;
          this.SaveRuntimeSummary(elapsedSeconds);
        }
        catch (Exception ex)
        {
          this.Log.DoLogCritical("A error occurred during the raid, the raid did not complete, but will try again next interval!", ex);
        }
        finally
        {
          this.Cleanup();
          this.State = Constants.PTMagicBotState_Idle;
          this.WriteStateToFile();
        }
      }
      finally
      {
        _raidSemaphore.Release();
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
          this.LastSettingFileCheck = DateTime.UtcNow;
          result = true;
          if (this.Timer.Interval != this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.IntervalMinutes * 60 * 1000)
          {
            this.Timer.Stop();
            this.Timer.Interval = this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.IntervalMinutes * 60 * 1000;
            this.Timer.Start();
          }
          SettingsFiles.CheckPresets(this.PTMagicConfiguration, this.Log, true);
        }
        catch (Exception ex)
        {
          Log.DoLogCritical("Error loading new configuration!", ex);
        }
      }
      else result = SettingsFiles.CheckPresets(this.PTMagicConfiguration, this.Log, false);
      return result;
    }

    private void ValidateSettings()
    {
      if (this.PTMagicConfiguration.GeneralSettings.Application.Exchange == null || String.IsNullOrEmpty(this.PTMagicConfiguration.GeneralSettings.Application.Exchange))
      {
        throw new Exception("Application.Exchange is invalid.");
      }
    }

    private async Task CheckLatestGitHubVersionAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
      if (this.LastVersionCheck < DateTime.UtcNow.AddMinutes(-30))
      {
        this.LatestVersion = await BaseAnalyzer.GetLatestGitHubReleaseAsync(this.Log, currentVersion, cancellationToken).ConfigureAwait(false);
        this.LastVersionCheck = DateTime.UtcNow;
      }
    }

    private void LoadCurrentProfitTrailerProperties()
    {
      this.Log.DoLogInfo("Loading current Profit Trailer properties from preset files...");
      SettingsHandler.CompileProperties(this, this.ActiveSetting, this.LastSettingsChange.ToLocalTime());
      if (this.PairsLines == null || this.DCALines == null || this.IndicatorsLines == null) throw new Exception("Unable to load all Profit Trailer properties!");
      this.Log.DoLogInfo("Properties loaded - P (" + this.PairsLines.Count + " lines) - D (" + this.DCALines.Count + " lines) - I (" + this.IndicatorsLines.Count + " lines).");
      this.LastRuntimeSummary.MainMarket = SettingsHandler.GetMainMarket(this.PTMagicConfiguration, this.PairsLines, this.Log);
    }

    private void LoadSMSSummaries()
    {
      this.SingleMarketSettingSummaries = new List<SingleMarketSettingSummary>();
    }

    private async Task BuildMarketDataAsync(CancellationToken cancellationToken = default)
    {
      if (!String.IsNullOrEmpty(this.PTMagicConfiguration.GeneralSettings.Application.CoinMarketCapAPIKey)) await CoinMarketCap.GetMarketDataAsync(this.PTMagicConfiguration, this.Log, cancellationToken).ConfigureAwait(false);
      else this.Log.DoLogInfo("No CMC API-Key specified. That's OK, but no CMC Data can be pulled.");

      if (this.PTMagicConfiguration.GeneralSettings.Application.Exchange.Equals("BinanceFutures", StringComparison.InvariantCultureIgnoreCase))
        this.ExchangeMarketList = await BinanceFutures.GetMarketDataAsync(this.LastRuntimeSummary.MainMarket, this.MarketInfos, this.PTMagicConfiguration, this.Log, cancellationToken).ConfigureAwait(false);
      else if (this.PTMagicConfiguration.GeneralSettings.Application.Exchange.Equals("Binance", StringComparison.InvariantCultureIgnoreCase))
        this.ExchangeMarketList = await Binance.GetMarketDataAsync(this.LastRuntimeSummary.MainMarket, this.MarketInfos, this.PTMagicConfiguration, this.Log, cancellationToken).ConfigureAwait(false);
      else if (this.PTMagicConfiguration.GeneralSettings.Application.Exchange.Equals("BinanceUS", StringComparison.InvariantCultureIgnoreCase))
        this.ExchangeMarketList = await BinanceUS.GetMarketDataAsync(this.LastRuntimeSummary.MainMarket, this.MarketInfos, this.PTMagicConfiguration, this.Log, cancellationToken).ConfigureAwait(false);
      else if (this.PTMagicConfiguration.GeneralSettings.Application.Exchange.Equals("Bittrex", StringComparison.InvariantCultureIgnoreCase))
        this.ExchangeMarketList = Bittrex.GetMarketData(this.LastRuntimeSummary.MainMarket, this.MarketInfos, this.PTMagicConfiguration, this.Log);
      else if (this.PTMagicConfiguration.GeneralSettings.Application.Exchange.Equals("Poloniex", StringComparison.InvariantCultureIgnoreCase))
        this.ExchangeMarketList = Poloniex.GetMarketData(this.LastRuntimeSummary.MainMarket, this.MarketInfos, this.PTMagicConfiguration, this.Log);

      if (this.ExchangeMarketList == null) throw new Exception("Unable to contact exchange for fresh market data.");
    }

    private void BuildMarketList()
    {
      string marketPairs = SettingsHandler.GetMarketPairs(this.PTMagicConfiguration, this.PairsLines, this.Log);
      if (marketPairs.ToLower().Equals("all") || marketPairs.ToLower().Equals("false") || marketPairs.ToLower().Equals("true") || String.IsNullOrEmpty(marketPairs)) this.MarketList = this.ExchangeMarketList;
      else
      {
        List<string> originalMarketList = SystemHelper.ConvertTokenStringToList(marketPairs, ",");
        foreach (string market in originalMarketList) this.MarketList.Add(SystemHelper.GetFullMarketName(this.LastRuntimeSummary.MainMarket, market, this.PTMagicConfiguration.GeneralSettings.Application.Exchange));
      }
    }

    private void ValidateMarketList()
    {
      if (!this.MarketList.Any(m => m.IndexOf(this.LastRuntimeSummary.MainMarket, StringComparison.InvariantCultureIgnoreCase) > -1)) throw new Exception("No valid pairs found for main market '" + this.LastRuntimeSummary.MainMarket + "'.");
    }

    private void BuildGlobalMarketTrends()
    {
      this.Log.DoLogInfo("Build global market trends...");
      this.SingleMarketTrendChanges = BaseAnalyzer.BuildMarketTrends("Exchange", this.LastRuntimeSummary.MainMarket, this.MarketList, "Volume", false, new Dictionary<string, List<MarketTrendChange>>(), this.PTMagicConfiguration, this.Log);
      this.GlobalMarketTrendChanges = new Dictionary<string, List<MarketTrendChange>>();
      this.GlobalMarketTrendChanges = BaseAnalyzer.BuildMarketTrends("CoinMarketCap", this.LastRuntimeSummary.MainMarket, new List<string>(), "", true, this.GlobalMarketTrendChanges, this.PTMagicConfiguration, this.Log);

      foreach (MarketTrend marketTrend in this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.MarketTrends.FindAll(mt => mt.Platform.Equals("Exchange", StringComparison.InvariantCultureIgnoreCase)))
      {
        if (this.SingleMarketTrendChanges.ContainsKey(marketTrend.Name))
        {
          int maxMarkets = this.SingleMarketTrendChanges[marketTrend.Name].Count;
          if (marketTrend.MaxMarkets > 0 && marketTrend.MaxMarkets <= maxMarkets) maxMarkets = marketTrend.MaxMarkets;
          this.GlobalMarketTrendChanges.Add(marketTrend.Name, this.SingleMarketTrendChanges[marketTrend.Name].Take(maxMarkets).ToList());
        }
      }

      this.AverageMarketTrendChanges = BaseAnalyzer.BuildGlobalMarketTrends(this.GlobalMarketTrendChanges, this.PTMagicConfiguration, this.Log);

      if (this.PTMagicConfiguration.GeneralSettings.Application.Exchange.Equals("BinanceFutures", StringComparison.InvariantCultureIgnoreCase))
      {
        BinanceFuturesAnalyticsRuntime.EvaluateConfiguredMarketTrends(
          this.GlobalMarketTrendChanges,
          this.SingleMarketTrendChanges,
          this.PTMagicConfiguration,
          this.Log);
      }

      this.Log.DoLogInfo("Global market trends built.");
    }

    private void CheckGlobalSettingsTriggers(ref GlobalSetting triggeredSetting, ref List<string> matchedTriggers)
    {
      this.Log.DoLogInfo("Checking global settings triggers...");
      foreach (GlobalSetting globalSetting in this.PTMagicConfiguration.AnalyzerSettings.GlobalSettings)
      {
        matchedTriggers = new List<string>();
        if (globalSetting.Triggers.Count == 0) continue;
        this.Log.DoLogInfo("Checking triggers for '" + globalSetting.SettingName + "'...");
        Dictionary<string, bool> triggerResults = new Dictionary<string, bool>();

        foreach (Trigger trigger in globalSetting.Triggers)
        {
          MarketTrend marketTrend = this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.MarketTrends.Find(mt => mt.Name == trigger.MarketTrendName);
          if (marketTrend == null)
          {
            this.Log.DoLogWarn("Market Trend '" + trigger.MarketTrendName + "' not found! Trigger ignored!");
            triggerResults[trigger.Tag] = false;
            continue;
          }

          double triggerValue;
          string metric = MarketTrendMetrics.Normalize(trigger.Metric);
          if (MarketTrendMetricTriggerResolver.IsPriceTrend(metric))
          {
            if (!this.AverageMarketTrendChanges.TryGetValue(marketTrend.Name, out triggerValue))
            {
              this.Log.DoLogError("Trigger '" + trigger.MarketTrendName + "' not found in AverageMarketTrendChanges. Trigger ignored.");
              triggerResults[trigger.Tag] = false;
              continue;
            }
          }
          else if (!MarketTrendMetricTriggerResolver.TryResolveGlobal(this.PTMagicConfiguration, marketTrend, metric, out triggerValue, out string error))
          {
            this.Log.DoLogError("GlobalSetting '" + globalSetting.SettingName + "' trigger '" + trigger.MarketTrendName + "' metric '" + metric + "' ignored: " + error);
            triggerResults[trigger.Tag] = false;
            continue;
          }

          bool isTriggered = triggerValue >= trigger.MinChange && triggerValue < trigger.MaxChange;
          triggerResults[trigger.Tag] = isTriggered;
          string unit = MarketTrendMetricTriggerResolver.GetUnit(metric);
          if (isTriggered)
          {
            this.Log.DoLogInfo("Trigger '" + trigger.MarketTrendName + "' metric '" + metric + "' triggered! Value = " + triggerValue.ToString("#,#0.00", new System.Globalization.CultureInfo("en-US")) + unit);
            matchedTriggers.Add(trigger.MarketTrendName + " / " + metric + " = " + triggerValue.ToString("#,#0.00", new System.Globalization.CultureInfo("en-US")) + unit);
          }
          else
          {
            this.Log.DoLogDebug("Trigger '" + trigger.MarketTrendName + "' metric '" + metric + "' not triggered. Value = " + triggerValue.ToString("#,#0.00", new System.Globalization.CultureInfo("en-US")) + unit);
          }
        }

        bool settingTriggered = false;
        if (globalSetting.TriggerConnection.ToLower() == "and") settingTriggered = triggerResults.Values.All(tr => tr);
        else if (globalSetting.TriggerConnection.ToLower() == "or") settingTriggered = triggerResults.Values.Any(tr => tr);
        else
        {
          string triggerConnection = globalSetting.TriggerConnection;
          foreach (var triggerResult in triggerResults)
          {
            if (String.IsNullOrEmpty(triggerResult.Key)) continue;
            triggerConnection = triggerConnection.Replace(triggerResult.Key, triggerResult.Value.ToString().ToLower());
          }
          try
          {
            settingTriggered = (bool)DynamicExpressionParser.ParseLambda(ParsingConfig.Default, new ParameterExpression[0], typeof(bool), triggerConnection).Compile().DynamicInvoke();
          }
          catch
          {
            this.Log.DoLogError("ERROR: Trigger Connection for global setting " + globalSetting.SettingName + " is invalid.");
          }
        }

        if (settingTriggered)
        {
          triggeredSetting = globalSetting;
          break;
        }
      }
    }

    private void ActivateSetting(ref GlobalSetting triggeredSetting, ref List<string> matchedTriggers)
    {
      if (triggeredSetting == null) return;
      if (this.EnforceSettingsReapply || !this.ActiveSettingName.Equals(triggeredSetting.SettingName, StringComparison.InvariantCultureIgnoreCase))
      {
        SettingsHandler.CompileProperties(this, triggeredSetting, DateTime.Now);
        this.GlobalSettingWritten = true;
        this.LastRuntimeSummary.LastGlobalSettingSwitch = this.LastRuntimeSummary.LastRuntime;
        this.LastRuntimeSummary.CurrentGlobalSetting = triggeredSetting;
        this.LastSettingsChange = DateTime.UtcNow;
      }
      else this.LastRuntimeSummary.CurrentGlobalSetting = triggeredSetting;
      this.ActiveSetting = this.LastRuntimeSummary.CurrentGlobalSetting;
      this.ActiveSettingName = this.ActiveSetting.SettingName;
    }

    private void ApplySingleMarketSettings()
    {
      if (this.PTMagicConfiguration.AnalyzerSettings.SingleMarketSettings.Count == 0) return;
      this.Log.DoLogInfo("Checking single market settings triggers for " + this.MarketList.Count + " markets...");

      foreach (string marketPair in this.MarketList)
      {
        bool stopTriggers = false;
        foreach (SingleMarketSetting marketSetting in this.PTMagicConfiguration.AnalyzerSettings.SingleMarketSettings)
        {
          if (stopTriggers) break;
          List<bool> triggerResults = new List<bool>();
          Dictionary<string, bool> triggerTagsResults = new Dictionary<string, bool>();
          List<string> matchedSingleMarketTriggers = new List<string>();
          int triggerIndex = 0;
          Dictionary<int, double> relevantTriggers = new Dictionary<int, double>();

          foreach (Trigger trigger in marketSetting.Triggers)
          {
            bool result = false;
            if (trigger.Min24hVolume > 0 || trigger.Max24hVolume < Constants.Max24hVolume)
            {
              List<MarketTrendChange> changes = this.SingleMarketTrendChanges[this.SingleMarketTrendChanges.Keys.Last()];
              MarketTrendChange mtc = changes.Find(m => m.Market.Equals(marketPair, StringComparison.InvariantCultureIgnoreCase));
              result = mtc != null && mtc.Volume24h >= trigger.Min24hVolume && mtc.Volume24h <= trigger.Max24hVolume;
            }
            else if (trigger.AgeDaysLowerThan > 0)
            {
              result = this.MarketInfos.TryGetValue(marketPair, out MarketInfo info) && (int)Math.Floor(DateTime.UtcNow.Subtract(info.FirstSeen).TotalDays) < trigger.AgeDaysLowerThan;
            }
            else
            {
              MarketTrend marketTrend = this.PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.MarketTrends.Find(mt => mt.Name.Equals(trigger.MarketTrendName, StringComparison.InvariantCultureIgnoreCase));
              string metric = MarketTrendMetrics.Normalize(trigger.Metric);
              double value = 0;

              if (marketTrend != null && !MarketTrendMetricTriggerResolver.IsPriceTrend(metric))
              {
                if (MarketTrendMetricTriggerResolver.TryResolveSingle(this.PTMagicConfiguration, marketTrend, metric, marketPair, out value, out string error))
                {
                  result = value >= trigger.MinChange && value < trigger.MaxChange;
                }
                else
                {
                  this.Log.DoLogDebug("'" + marketPair + "' - Metric trigger '" + trigger.MarketTrendName + " / " + metric + "' unavailable: " + error);
                }
              }
              else if (marketTrend != null && this.SingleMarketTrendChanges.TryGetValue(trigger.MarketTrendName, out List<MarketTrendChange> changes))
              {
                MarketTrendChange mtc = changes.Find(m => m.Market.Equals(marketPair, StringComparison.InvariantCultureIgnoreCase));
                if (mtc != null)
                {
                  value = mtc.TrendChange;
                  if (trigger.MarketTrendRelation.Equals(Constants.MarketTrendRelationRelative))
                  {
                    IEnumerable<MarketTrendChange> eligible = marketTrend.TrendThreshold == 0
                      ? changes
                      : changes.Where(m => m.TrendChange <= marketTrend.TrendThreshold && m.TrendChange >= -marketTrend.TrendThreshold);
                    if (eligible.Any()) value -= eligible.Average(m => m.TrendChange);
                  }
                  result = value >= trigger.MinChange && value < trigger.MaxChange;
                }
              }

              if (result)
              {
                relevantTriggers[triggerIndex] = value;
                string unit = MarketTrendMetricTriggerResolver.GetUnit(metric);
                matchedSingleMarketTriggers.Add(marketSetting.SettingName + ": " + trigger.MarketTrendName + " / " + metric + " = " + value.ToString("#,#0.00", new System.Globalization.CultureInfo("en-US")) + unit);
              }
            }

            triggerResults.Add(result);
            if (!String.IsNullOrEmpty(trigger.Tag)) triggerTagsResults[trigger.Tag] = result;
            triggerIndex++;
          }

          bool settingTriggered = false;
          if (marketSetting.TriggerConnection.Equals("and", StringComparison.OrdinalIgnoreCase)) settingTriggered = triggerResults.All(v => v);
          else if (marketSetting.TriggerConnection.Equals("or", StringComparison.OrdinalIgnoreCase)) settingTriggered = triggerResults.Any(v => v);
          else if (!String.IsNullOrWhiteSpace(marketSetting.TriggerConnection))
          {
            string expression = marketSetting.TriggerConnection;
            foreach (KeyValuePair<string, bool> pair in triggerTagsResults) expression = expression.Replace(pair.Key, pair.Value.ToString().ToLower());
            try { settingTriggered = (bool)DynamicExpressionParser.ParseLambda(ParsingConfig.Default, new ParameterExpression[0], typeof(bool), expression).Compile().DynamicInvoke(); } catch { settingTriggered = false; }
          }

          if (settingTriggered)
          {
            if (!this.TriggeredSingleMarketSettings.ContainsKey(marketPair)) this.TriggeredSingleMarketSettings[marketPair] = new List<SingleMarketSetting>();
            this.TriggeredSingleMarketSettings[marketPair].Add(marketSetting);
            if (!this.SingleMarketSettingsCount.ContainsKey(marketSetting.SettingName)) this.SingleMarketSettingsCount[marketSetting.SettingName] = 0;
            this.SingleMarketSettingsCount[marketSetting.SettingName]++;
            if (marketSetting.StopProcessWhenTriggered) stopTriggers = true;
          }
        }
      }

      if (this.TriggeredSingleMarketSettings.Count > 0)
      {
        var newSingleMarketSettings = SettingsHandler.CompileSingleMarketProperties(this, new Dictionary<string, List<string>>());
        if (_lastActiveSingleMarketSettings == null || haveSingleMarketSettingsHaveChanged(_lastActiveSingleMarketSettings, newSingleMarketSettings)) this.SingleMarketSettingChanged = true;
        _lastActiveSingleMarketSettings = newSingleMarketSettings;
      }
      else this.SingleMarketSettingChanged = SettingsHandler.RemoveSingleMarketSettings(this);
    }

    private bool haveSingleMarketSettingsHaveChanged(List<KeyValuePair<string, string>> oldMarketTriggers, List<KeyValuePair<string, string>> newMarketTriggers)
    {
      string oldSms = "", newSms = "";
      foreach (var entry in oldMarketTriggers) oldSms += string.Format("{0}: {1}|", entry.Key, entry.Value);
      foreach (var entry in newMarketTriggers) newSms += string.Format("{0}: {1}|", entry.Key, entry.Value);
      return !string.Equals(oldSms, newSms, StringComparison.OrdinalIgnoreCase);
    }

    private void SaveProfitTrailerProperties()
    {
      if (this.GlobalSettingWritten || this.SingleMarketSettingChanged)
      {
        this.Log.DoLogInfo("Saving properties using API...");
        if (!this.PTMagicConfiguration.GeneralSettings.Application.TestMode)
        {
          SettingsAPI.SendPropertyLinesToAPI(this.PairsLines, this.DCALines, this.IndicatorsLines, this.PTMagicConfiguration, this.Log);
          this.Log.DoLogInfo("Settings updates sent to PT!");
        }
        else this.Log.DoLogWarn("TESTMODE enabled -- no updates sent to PT!");
      }
      else this.Log.DoLogInfo("Nothing changed, no config written!");
    }

    private void SaveSingleMarketSettingsSummary()
    {
      JsonSerializerSettings smsSummaryJsonSettings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore };
      FileHelper.WriteTextToFile(Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + Constants.PTMagicPathData + Path.DirectorySeparatorChar, "SingleMarketSettingSummary.json", JsonConvert.SerializeObject(this.SingleMarketSettingSummaries, Formatting.None, smsSummaryJsonSettings));
    }

    private void SaveRuntimeSummary(int elapsedSeconds)
    {
      this.LastRuntimeSummary.LastRuntimeSeconds = elapsedSeconds;
      this.LastRuntimeSummary.CurrentGlobalSetting ??= this.ActiveSetting;
      string serialziedJson = JsonConvert.SerializeObject(this.LastRuntimeSummary);
      FileHelper.WriteTextToFile(Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + Constants.PTMagicPathData + Path.DirectorySeparatorChar, "LastRuntimeSummary.json", serialziedJson);
    }

    private void Cleanup()
    {
      this.GlobalSettingWritten = false;
      this.SingleMarketSettingChanged = false;
      this.EnforceSettingsReapply = false;
      this.PairsLines = null;
      this.DCALines = null;
      this.IndicatorsLines = null;
      this.SingleMarketTrendChanges = new Dictionary<string, List<MarketTrendChange>>();
      this.GlobalMarketTrendChanges = new Dictionary<string, List<MarketTrendChange>>();
      this.AverageMarketTrendChanges = new Dictionary<string, double>();
      this.SingleMarketSettingsCount = new Dictionary<string, int>();
      this.TriggeredSingleMarketSettings = new Dictionary<string, List<SingleMarketSetting>>();
      this.ExchangeMarketList = null;
      this.MarketList = new List<string>();
      this.LastRuntimeSummary = null;
    }
    #endregion
  }
}
