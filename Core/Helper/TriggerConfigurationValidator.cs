using System;
using System.Collections.Generic;
using System.Linq;
using Core.Main;
using Core.Main.DataObjects.PTMagicData;
using Core.MarketAnalyzer.MarketData;

namespace Core.Helper
{
  /// <summary>
  /// Validates the identities and expressions that must be resolved before a
  /// raid can make a decision. Metric availability/scope is deliberately a
  /// diagnostic warning: an invalid requested metric remains Unavailable in
  /// the runtime, including intentional TestMode diagnostic configurations.
  /// </summary>
  public static class TriggerConfigurationValidator
  {
    public static IReadOnlyList<string> Validate(AnalyzerSettings settings)
    {
      List<string> errors = new List<string>();
      if (settings?.MarketAnalyzer?.MarketTrends == null)
      {
        errors.Add("MarketAnalyzer.MarketTrends is required.");
        return errors;
      }

      bool hasOpenInterestMetric = settings.MarketAnalyzer.MarketTrends.Any(trend =>
        trend != null && FuturesOpenInterestFeatureEngine.HasMetrics(MarketTrendMetrics.ParseEnabled(trend)));
      if (hasOpenInterestMetric &&
        (settings.MarketAnalyzer.OpenInterestMaxRequestsPerRaid < 0 ||
         settings.MarketAnalyzer.OpenInterestMaxRequestsPerRaid > FuturesOpenInterestDataService.MaximumRequestsPerRaid))
        errors.Add("MarketAnalyzer.OpenInterestMaxRequestsPerRaid must be between 0 and 400.");

      bool hasFunding = settings.MarketAnalyzer.MarketTrends.Any(trend =>
        trend != null && FuturesFundingFeatureEngine.HasMetrics(MarketTrendMetrics.ParseEnabled(trend)));
      if (hasFunding &&
        (settings.MarketAnalyzer.FundingMaxRequestsPerRaid < 0 ||
         settings.MarketAnalyzer.FundingMaxRequestsPerRaid > FuturesFundingDataService.MaximumRequestsPerRaid))
        errors.Add("MarketAnalyzer.FundingMaxRequestsPerRaid must be between 0 and 400.");

      Dictionary<string, MarketTrend> trends = new Dictionary<string, MarketTrend>(StringComparer.OrdinalIgnoreCase);
      foreach (MarketTrend trend in settings.MarketAnalyzer.MarketTrends)
      {
        if (trend == null || String.IsNullOrWhiteSpace(trend.Name))
        {
          errors.Add("Every MarketTrend must have a nonempty Name.");
        }
        else if (!trends.TryAdd(trend.Name, trend))
        {
          errors.Add("Duplicate MarketTrend name '" + trend.Name + "' (names are case-insensitive).");
        }
        if (trend != null) ValidateMetricParameters(trend, errors);
        if (trend != null && String.Equals(trend.Platform, "Exchange", StringComparison.OrdinalIgnoreCase)
          && (trend.TrendMinutes <= 0 || trend.TrendMinutes % 5 != 0))
          errors.Add("MarketTrend '" + trend.Name + "' TrendMinutes must be a positive multiple of 5 for normalized Exchange analysis.");
      }

      if (settings.GlobalSettings == null)
      {
        errors.Add("GlobalSettings is required.");
      }
      else
      {
        GlobalSetting[] defaultSettings = settings.GlobalSettings.Where(setting => setting != null && setting.SettingName != null
          && setting.SettingName.IndexOf("default", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
        if (defaultSettings.Length == 0)
          errors.Add("GlobalSettings must contain a default fallback setting (a SettingName containing 'default').");

        GlobalSetting[] indeterminateFallbacks = settings.GlobalSettings
          .Where(setting => setting != null && setting.IsIndeterminateFallback).ToArray();
        if (indeterminateFallbacks.Length > 1)
        {
          errors.Add("At most one GlobalSetting may set IsIndeterminateFallback=true.");
        }
        else if (indeterminateFallbacks.Length == 1)
        {
          GlobalSetting fallback = indeterminateFallbacks[0];
          if (String.IsNullOrWhiteSpace(fallback.SettingName)
            || fallback.SettingName.IndexOf("default", StringComparison.OrdinalIgnoreCase) >= 0)
            errors.Add("IsIndeterminateFallback cannot be set on DEFAULT.");
          if (defaultSettings.Length != 1)
          {
            errors.Add("An IsIndeterminateFallback setting requires exactly one DEFAULT GlobalSetting.");
          }
          else
          {
            int defaultIndex = settings.GlobalSettings.IndexOf(defaultSettings[0]);
            int fallbackIndex = settings.GlobalSettings.IndexOf(fallback);
            if (defaultIndex != settings.GlobalSettings.Count - 1)
              errors.Add("DEFAULT must remain the final GlobalSetting when IsIndeterminateFallback is configured.");
            if (fallbackIndex != settings.GlobalSettings.Count - 2 || fallbackIndex != defaultIndex - 1)
              errors.Add("The IsIndeterminateFallback GlobalSetting must appear immediately before DEFAULT.");
          }
        }

        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GlobalSetting setting in settings.GlobalSettings)
        {
          if (!CheckSettingName(setting?.SettingName, "GlobalSetting", names, errors)) continue;
          string context = "GlobalSetting '" + setting.SettingName + "'";
          if (setting.Triggers == null)
          {
            errors.Add(context + " must have a Triggers array (empty is allowed for the default).");
            continue;
          }
          ValidateTriggers(setting.Triggers, setting.TriggerConnection, context, true, trends, errors);
        }
      }

      if (settings.SingleMarketSettings == null)
      {
        errors.Add("SingleMarketSettings is required (use an empty array when unused).");
      }
      else
      {
        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SingleMarketSetting setting in settings.SingleMarketSettings)
        {
          if (!CheckSettingName(setting?.SettingName, "SingleMarketSetting", names, errors)) continue;
          string context = "SingleMarketSetting '" + setting.SettingName + "'";
          if (setting.Triggers == null)
          {
            errors.Add(context + " must have a Triggers array.");
          }
          else
          {
            ValidateTriggers(setting.Triggers, setting.TriggerConnection, context, false, trends, errors);
          }

          // Null OffTriggers historically means no persistent off-condition.
          if (setting.OffTriggers == null || setting.OffTriggers.Count == 0) continue;
          if (!IsSimpleConnection(setting.OffTriggerConnection))
          {
            errors.Add(context + " OffTriggerConnection must be AND or OR; custom off expressions are not supported.");
          }

          HashSet<string> offTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
          for (int index = 0; index < setting.OffTriggers.Count; index++)
          {
            OffTrigger trigger = setting.OffTriggers[index];
            string source = context + " OffTrigger #" + (index + 1);
            if (trigger == null)
            {
              errors.Add(source + " is null.");
              continue;
            }
            CheckTag(trigger.Tag, source, false, offTags, errors);
            // Match the actual off-trigger precedence (time, volume, metric).
            bool hasVolume = trigger.Min24hVolume > 0 || trigger.Max24hVolume < Constants.Max24hVolume;
            if (trigger.HoursSinceTriggered <= 0 && !hasVolume)
            {
              CheckReference(trigger.MarketTrendName, source, trends, errors);
            }
          }
        }
      }
      return errors.AsReadOnly();
    }

    public static void ThrowIfInvalid(AnalyzerSettings settings)
    {
      IReadOnlyList<string> errors = Validate(settings);
      if (errors.Count > 0)
      {
        throw new InvalidOperationException("Invalid analyzer trigger configuration:" + Environment.NewLine +
          String.Join(Environment.NewLine, errors.Select(error => "- " + error)));
      }
    }

    /// <summary>
    /// Reports known metric contract problems without converting unavailable
    /// measurements to false or rejecting intentional diagnostic runs.
    /// </summary>
    public static IReadOnlyList<string> GetMetricWarnings(AnalyzerSettings settings)
    {
      List<string> warnings = new List<string>();
      if (settings?.MarketAnalyzer?.MarketTrends == null) return warnings;
      foreach (GlobalSetting setting in settings.GlobalSettings ?? new List<GlobalSetting>())
      {
        if (setting == null) continue;
        foreach (Trigger trigger in setting.Triggers ?? new List<Trigger>())
        {
          if (trigger != null) AddMetricWarning(settings, trigger.MarketTrendName, trigger.Metric,
            true, "GlobalSetting '" + setting.SettingName + "'", warnings);
        }
      }
      foreach (SingleMarketSetting setting in settings.SingleMarketSettings ?? new List<SingleMarketSetting>())
      {
        if (setting == null) continue;
        foreach (Trigger trigger in setting.Triggers ?? new List<Trigger>())
        {
          if (trigger != null && trigger.AgeDaysLowerThan <= 0 && trigger.Min24hVolume <= 0 && trigger.Max24hVolume >= Constants.Max24hVolume)
            AddMetricWarning(settings, trigger.MarketTrendName, trigger.Metric, false,
              "SingleMarketSetting '" + setting.SettingName + "'", warnings);
        }
        foreach (OffTrigger trigger in setting.OffTriggers ?? new List<OffTrigger>())
        {
          if (trigger != null && trigger.HoursSinceTriggered <= 0 && trigger.Min24hVolume <= 0 && trigger.Max24hVolume >= Constants.Max24hVolume)
            AddMetricWarning(settings, trigger.MarketTrendName, trigger.Metric, false,
              "SingleMarketSetting '" + setting.SettingName + "' OffTrigger", warnings);
        }
      }
      return warnings.AsReadOnly();
    }

    private static void AddMetricWarning(AnalyzerSettings settings, string trendName, string metric,
      bool global, string context, List<string> warnings)
    {
      MarketTrend trend = settings.MarketAnalyzer.MarketTrends.FirstOrDefault(item =>
        item != null && String.Equals(item.Name, trendName, StringComparison.OrdinalIgnoreCase));
      if (trend != null && !BinanceFuturesAnalyticsRuntime.ValidateMetricReference(trend, metric, global, out string error))
        warnings.Add(context + ": " + error + " The requested measurement remains unavailable.");
    }

    private static void ValidateTriggers(List<Trigger> triggers, string connection, string context,
      bool global, Dictionary<string, MarketTrend> trends, List<string> errors)
    {
      if (triggers.Count == 0) return;
      bool custom = !IsSimpleConnection(connection);
      HashSet<string> tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      Dictionary<string, TriggerTruthValue> values = new Dictionary<string, TriggerTruthValue>(StringComparer.OrdinalIgnoreCase);
      int errorCount = errors.Count;
      for (int index = 0; index < triggers.Count; index++)
      {
        Trigger trigger = triggers[index];
        string source = context + " Trigger #" + (index + 1);
        if (trigger == null)
        {
          errors.Add(source + " is null.");
          continue;
        }
        CheckTag(trigger.Tag, source, custom, tags, errors);
        if (!String.IsNullOrWhiteSpace(trigger.Tag)) values[trigger.Tag] = TriggerTruthValue.Unavailable;
        bool hasVolume = trigger.Min24hVolume > 0 || trigger.Max24hVolume < Constants.Max24hVolume;
        if (global || (!hasVolume && trigger.AgeDaysLowerThan <= 0))
          CheckReference(trigger.MarketTrendName, source, trends, errors);
      }

      if (String.IsNullOrWhiteSpace(connection))
      {
        errors.Add(context + " TriggerConnection is missing.");
      }
      else if (errors.Count == errorCount)
      {
        try
        {
          // Use the production parser/evaluator, with unknown measurements.
          // No synthetic true/false substitution can hide a malformed branch.
          ThreeValuedTriggerLogic.Evaluate(connection,
            Enumerable.Repeat(TriggerTruthValue.Unavailable, triggers.Count).ToArray(), values);
        }
        catch (Exception ex)
        {
          errors.Add(context + " TriggerConnection is invalid: " + ex.Message);
        }
      }
    }

    private static bool IsSimpleConnection(string connection) =>
      String.Equals(connection?.Trim(), "AND", StringComparison.OrdinalIgnoreCase) ||
      String.Equals(connection?.Trim(), "OR", StringComparison.OrdinalIgnoreCase);

    private static void ValidateMetricParameters(MarketTrend trend, List<string> errors)
    {
      string context = "MarketTrend '" + trend.Name + "' ";
      bool Enabled(params string[] metrics) => metrics.Any(metric => MarketTrendMetrics.IsEnabled(trend, metric));
      void Range(string field, int value, int minimum)
      {
        if (value < minimum || value > 1000) errors.Add(context + field + " must be between " + minimum + " and 1000.");
      }
      void Threshold(string field, double value)
      {
        if (!Double.IsFinite(value) || value < 0) errors.Add(context + field + " must be finite and nonnegative.");
      }
      if (Enabled(MarketTrendMetrics.ATRP, MarketTrendMetrics.MedianATRP, MarketTrendMetrics.VolatilityBreadth))
        Range("ATRPeriod", trend.ATRPeriod, 1);
      if (Enabled(MarketTrendMetrics.VolumeExpansion, MarketTrendMetrics.MedianVolumeExpansion,
        MarketTrendMetrics.VolumeExpansionBreadth, MarketTrendMetrics.StrongVolumeExpansionBreadth))
        Range("VolumeExpansionLookback", trend.VolumeExpansionLookback, 1);
      if (Enabled(MarketTrendMetrics.TopNVolumeShare)) Range("TopNVolumeCount", trend.TopNVolumeCount, 1);
      if (Enabled(MarketTrendMetrics.CorrelationToBTC, MarketTrendMetrics.MeanCorrelationToBTC, MarketTrendMetrics.MedianCorrelationToBTC))
        Range("CorrelationLookback", trend.CorrelationLookback, 3);
      if (Enabled(MarketTrendMetrics.VolatilityBreadth)) Threshold("ATRPBreadthThreshold", trend.ATRPBreadthThreshold);
      if (Enabled(MarketTrendMetrics.VolumeExpansionBreadth)) Threshold("VolumeExpansionBreadthThreshold", trend.VolumeExpansionBreadthThreshold);
      if (Enabled(MarketTrendMetrics.StrongVolumeExpansionBreadth)) Threshold("StrongVolumeExpansionThreshold", trend.StrongVolumeExpansionThreshold);
    }

    private static void CheckReference(string name, string context,
      Dictionary<string, MarketTrend> trends, List<string> errors)
    {
      if (String.IsNullOrWhiteSpace(name) || !trends.ContainsKey(name))
        errors.Add(context + " references missing MarketTrend '" + (name ?? "") + "'.");
    }

    private static void CheckTag(string tag, string context, bool required,
      HashSet<string> tags, List<string> errors)
    {
      if (String.IsNullOrWhiteSpace(tag))
      {
        if (required) errors.Add(context + " requires a nonempty Tag for its custom TriggerConnection.");
      }
      else if (!tags.Add(tag))
      {
        errors.Add(context + " duplicates Tag '" + tag + "' (tags are case-insensitive).");
      }
      else if (required)
      {
        try
        {
          // A tag must parse as its variable, never a Boolean literal or an
          // operator. Ask the same parser used by live custom expressions.
          TriggerTruthValue value = ThreeValuedTriggerLogic.Evaluate("(" + tag + ")",
            new[] { TriggerTruthValue.Unavailable },
            new Dictionary<string, TriggerTruthValue>(StringComparer.OrdinalIgnoreCase) { [tag] = TriggerTruthValue.Unavailable });
          if (value != TriggerTruthValue.Unavailable)
            errors.Add(context + " Tag '" + tag + "' must be a Boolean variable identifier, not a reserved literal.");
        }
        catch (Exception)
        {
          errors.Add(context + " Tag '" + tag + "' is not a valid Boolean variable identifier.");
        }
      }
    }

    private static bool CheckSettingName(string name, string kind, HashSet<string> names, List<string> errors)
    {
      if (String.IsNullOrWhiteSpace(name))
      {
        errors.Add("Every " + kind + " must have a nonempty SettingName.");
        return false;
      }
      if (!names.Add(name)) errors.Add("Duplicate " + kind + " name '" + name + "' (names are case-insensitive).");
      return true;
    }
  }
}
