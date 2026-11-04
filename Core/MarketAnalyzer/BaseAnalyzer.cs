using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Core.Main;
using Core.Helper;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer
{
  public class BaseAnalyzer
  {
    private const int MaxHttpRetries = 3;
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly object MarketDataFileIndexLock = new object();
    private static readonly Dictionary<string, MarketDataFileIndexEntry> MarketDataFileIndexes = new Dictionary<string, MarketDataFileIndexEntry>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Dictionary<string, Dictionary<string, Market>>> MarketDataSnapshotCaches = new Dictionary<string, Dictionary<string, Dictionary<string, Market>>>(StringComparer.OrdinalIgnoreCase);

    private sealed class MarketDataFileIndexEntry
    {
      public DateTime DirectoryLastWriteTimeUtc { get; set; }
      public List<FileInfo> Files { get; set; }
    }

    private static HttpClient CreateHttpClient()
    {
      SocketsHttpHandler handler = new SocketsHttpHandler
      {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
      };

      HttpClient client = new HttpClient(handler)
      {
        Timeout = TimeSpan.FromSeconds(30)
      };

      client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
      client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));

      return client;
    }

    private static HttpRequestMessage CreateRequest(string url, (string header, string value)[] headers = null)
    {
      HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);

      if (headers != null)
      {
        foreach (var header in headers)
        {
          if (!request.Headers.TryAddWithoutValidation(header.header, header.value))
          {
            request.Dispose();
            throw new InvalidOperationException($"Unable to add HTTP header '{header.header}' whilst calling {url}.");
          }
        }
      }

      return request;
    }

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode)
    {
      int status = (int)statusCode;
      return status == 408 || status == 429 || status == 500 || status == 502 || status == 503 || status == 504;
    }

    private static bool IsBinanceIpBan(HttpStatusCode statusCode)
    {
      return (int)statusCode == 418;
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int retryNumber)
    {
      if (response?.Headers?.RetryAfter != null)
      {
        if (response.Headers.RetryAfter.Delta.HasValue)
        {
          return response.Headers.RetryAfter.Delta.Value > TimeSpan.Zero ? response.Headers.RetryAfter.Delta.Value : TimeSpan.Zero;
        }

        if (response.Headers.RetryAfter.Date.HasValue)
        {
          TimeSpan delay = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
          return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
      }

      return TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, retryNumber - 1)));
    }

    private static string GetResponseContext(HttpResponseMessage response)
    {
      List<string> context = new List<string>();

      if (response?.Headers?.RetryAfter != null)
      {
        if (response.Headers.RetryAfter.Delta.HasValue)
        {
          context.Add("Retry-After=" + response.Headers.RetryAfter.Delta.Value.TotalSeconds.ToString("0.###") + "s");
        }
        else if (response.Headers.RetryAfter.Date.HasValue)
        {
          context.Add("Retry-After=" + response.Headers.RetryAfter.Date.Value.ToString("O"));
        }
      }

      if (response != null)
      {
        foreach (var header in response.Headers.Where(h => h.Key.StartsWith("X-MBX-", StringComparison.OrdinalIgnoreCase) || h.Key.StartsWith("X-SAPI-", StringComparison.OrdinalIgnoreCase)))
        {
          context.Add(header.Key + "=" + String.Join(",", header.Value));
        }
      }

      return context.Count > 0 ? String.Join("; ", context) : "";
    }

    private static void ValidateResponse(HttpResponseMessage response, string url, string responseString)
    {
      if (response.IsSuccessStatusCode)
      {
        return;
      }

      string context = GetResponseContext(response);
      string message = $"Error whilst calling {url} - HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
      if (!String.IsNullOrEmpty(context))
      {
        message += " - " + context;
      }
      if (!String.IsNullOrEmpty(responseString))
      {
        message += " - " + responseString;
      }

      throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static void LogRetry(LogHelper log, string url, int retryNumber, TimeSpan delay, HttpStatusCode? statusCode = null, string reason = null)
    {
      string status = statusCode.HasValue ? $"HTTP {(int)statusCode.Value} {statusCode.Value}" : reason;
      log.DoLogWarn($"HTTP GET retry {retryNumber}/{MaxHttpRetries} for {url} after {delay.TotalSeconds:0.###} seconds" + (!String.IsNullOrEmpty(status) ? $" ({status})." : "."));
    }

    public static string GetJsonStringFromURL(string url, LogHelper log, (string header, string value)[] headers = null)
    {
      int attempt = 0;
      while (true)
      {
        try
        {
          using HttpRequestMessage request = CreateRequest(url, headers);
          BinanceRateLimiter.WaitIfNeeded(request.RequestUri, log);
          using HttpResponseMessage response = HttpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
          BinanceRateLimiter.ObserveResponse(request.RequestUri, response);
          using Stream responseStream = response.Content.ReadAsStream();
          using StreamReader reader = new StreamReader(responseStream);

          string responseString = reader.ReadToEnd();
          if (response.IsSuccessStatusCode)
          {
            return responseString;
          }

          if (IsBinanceIpBan(response.StatusCode))
          {
            ValidateResponse(response, url, responseString);
          }

          if (IsRetryableStatusCode(response.StatusCode) && attempt < MaxHttpRetries)
          {
            attempt++;
            TimeSpan delay = GetRetryDelay(response, attempt);
            LogRetry(log, url, attempt, delay, response.StatusCode);
            Thread.Sleep(delay);
            continue;
          }

          ValidateResponse(response, url, responseString);
        }
        catch (OperationCanceledException ex)
        {
          if (attempt < MaxHttpRetries)
          {
            attempt++;
            TimeSpan delay = GetRetryDelay(null, attempt);
            LogRetry(log, url, attempt, delay, null, "timeout");
            Thread.Sleep(delay);
            continue;
          }

          log.DoLogError($"Timeout whilst calling {url} - {ex.Message}");
          throw;
        }
        catch (HttpRequestException ex)
        {
          if (!ex.StatusCode.HasValue && attempt < MaxHttpRetries)
          {
            attempt++;
            TimeSpan delay = GetRetryDelay(null, attempt);
            LogRetry(log, url, attempt, delay, null, "network error");
            Thread.Sleep(delay);
            continue;
          }

          log.DoLogError($"HTTP error whilst calling {url}\nError: {ex.Message}");
          throw;
        }
        catch (Exception ex)
        {
          log.DoLogError($"Error whilst calling {url}\nError: {ex.Message}");
          throw;
        }
      }
    }

    public static async Task<string> GetJsonStringFromURLAsync(string url, LogHelper log, (string header, string value)[] headers = null, CancellationToken cancellationToken = default)
    {
      int attempt = 0;
      while (true)
      {
        try
        {
          using HttpRequestMessage request = CreateRequest(url, headers);
          await BinanceRateLimiter.WaitIfNeededAsync(request.RequestUri, log, cancellationToken).ConfigureAwait(false);
          using HttpResponseMessage response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
          BinanceRateLimiter.ObserveResponse(request.RequestUri, response);
          string responseString = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

          if (response.IsSuccessStatusCode)
          {
            return responseString;
          }

          if (IsBinanceIpBan(response.StatusCode))
          {
            ValidateResponse(response, url, responseString);
          }

          if (IsRetryableStatusCode(response.StatusCode) && attempt < MaxHttpRetries)
          {
            attempt++;
            TimeSpan delay = GetRetryDelay(response, attempt);
            LogRetry(log, url, attempt, delay, response.StatusCode);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            continue;
          }

          ValidateResponse(response, url, responseString);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
          log.DoLogError($"Request cancelled whilst calling {url} - {ex.Message}");
          throw;
        }
        catch (OperationCanceledException ex)
        {
          if (attempt < MaxHttpRetries)
          {
            attempt++;
            TimeSpan delay = GetRetryDelay(null, attempt);
            LogRetry(log, url, attempt, delay, null, "timeout");
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            continue;
          }

          log.DoLogError($"Timeout whilst calling {url} - {ex.Message}");
          throw;
        }
        catch (HttpRequestException ex)
        {
          if (!ex.StatusCode.HasValue && attempt < MaxHttpRetries)
          {
            attempt++;
            TimeSpan delay = GetRetryDelay(null, attempt);
            LogRetry(log, url, attempt, delay, null, "network error");
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            continue;
          }

          log.DoLogError($"HTTP error whilst calling {url}\nError: {ex.Message}");
          throw;
        }
        catch (Exception ex)
        {
          log.DoLogError($"Error whilst calling {url}\nError: {ex.Message}");
          throw;
        }
      }
    }

    public static Dictionary<string, dynamic> GetJsonFromURL(string url, LogHelper log, (string header, string value)[] headers = null)
    {
      string jsonString = GetJsonStringFromURL(url, log, headers);
      return JsonConvert.DeserializeObject<Dictionary<string, dynamic>>(jsonString);
    }

    public static async Task<Dictionary<string, dynamic>> GetJsonFromURLAsync(string url, LogHelper log, (string header, string value)[] headers = null, CancellationToken cancellationToken = default)
    {
      string jsonString = await GetJsonStringFromURLAsync(url, log, headers, cancellationToken).ConfigureAwait(false);
      return JsonConvert.DeserializeObject<Dictionary<string, dynamic>>(jsonString);
    }

    public static Newtonsoft.Json.Linq.JObject GetSimpleJsonObjectFromURL(string url, LogHelper log, (string header, string value)[] headers = null)
    {
      string jsonString = GetJsonStringFromURL(url, log, headers);
      return JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(jsonString);
    }

    public static async Task<Newtonsoft.Json.Linq.JObject> GetSimpleJsonObjectFromURLAsync(string url, LogHelper log, (string header, string value)[] headers = null, CancellationToken cancellationToken = default)
    {
      string jsonString = await GetJsonStringFromURLAsync(url, log, headers, cancellationToken).ConfigureAwait(false);
      return JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(jsonString);
    }

    public static List<dynamic> GetSimpleJsonListFromURL(string url, LogHelper log)
    {
      string jsonString = GetJsonStringFromURL(url, log, null);
      return JsonConvert.DeserializeObject<List<dynamic>>(jsonString);
    }

    public static async Task<List<dynamic>> GetSimpleJsonListFromURLAsync(string url, LogHelper log, CancellationToken cancellationToken = default)
    {
      string jsonString = await GetJsonStringFromURLAsync(url, log, null, cancellationToken).ConfigureAwait(false);
      return JsonConvert.DeserializeObject<List<dynamic>>(jsonString);
    }

    public static Newtonsoft.Json.Linq.JArray GetSimpleJsonArrayFromURL(string url, LogHelper log)
    {
      string jsonString = GetJsonStringFromURL(url, log, null);
      return JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JArray>(jsonString);
    }

    public static async Task<Newtonsoft.Json.Linq.JArray> GetSimpleJsonArrayFromURLAsync(string url, LogHelper log, CancellationToken cancellationToken = default)
    {
      string jsonString = await GetJsonStringFromURLAsync(url, log, null, cancellationToken).ConfigureAwait(false);
      return JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JArray>(jsonString);
    }

    public static string GetLatestGitHubRelease(LogHelper log, string defaultVersion)
    {
      string result = defaultVersion;

      try
      {
        string baseUrl = "https://api.github.com/repos/PTMagicians/PTMagic/releases/latest";

        Newtonsoft.Json.Linq.JObject jsonObject = GetSimpleJsonObjectFromURL(baseUrl, log, new (string header, string value)[] { ("User-Agent", "PTMagic.Import") });
        if (jsonObject != null)
        {
          result = jsonObject.GetValue("tag_name").ToString();
        }
      }
      catch (Exception ex)
      {
        log.DoLogDebug("GitHub version check error: " + ex.Message);
      }
      return result;
    }

    public static async Task<string> GetLatestGitHubReleaseAsync(LogHelper log, string defaultVersion, CancellationToken cancellationToken = default)
    {
      string result = defaultVersion;

      try
      {
        string baseUrl = "https://api.github.com/repos/PTMagicians/PTMagic/releases/latest";

        Newtonsoft.Json.Linq.JObject jsonObject = await GetSimpleJsonObjectFromURLAsync(baseUrl, log, new (string header, string value)[] { ("User-Agent", "PTMagic.Import") }, cancellationToken).ConfigureAwait(false);
        if (jsonObject != null)
        {
          result = jsonObject.GetValue("tag_name").ToString();
        }
      }
      catch (Exception ex)
      {
        log.DoLogDebug("GitHub version check error: " + ex.Message);
      }
      return result;
    }

    public static double GetMainFiatCurrencyRate(string currency, string FreeCurrencyAPI, LogHelper log)
    {
      double result = 1;
      string baseUrl = "https://free.currencyconverterapi.com/api/v5/convert?q=USD_" + currency + "&compact=y&apiKey=" + FreeCurrencyAPI;

      log.DoLogDebug("https://free.currencyconverterapi.com - Getting latest exchange rates...");
      Newtonsoft.Json.Linq.JObject jsonObject = GetSimpleJsonObjectFromURL(baseUrl, log, null);
      if (jsonObject != null)
      {
        log.DoLogDebug("https://free.currencyconverterapi.com - Received latest exchange rates.");

        result = (double)jsonObject["USD_" + currency]["val"];
        log.DoLogInfo("https://free.currencyconverterapi.com - Latest exchange rate for USD to " + currency + " is " + result);
      }
      return result;
    }

    public static ConcurrentDictionary<string, MarketInfo> GetMarketInfosFromFile(PTMagicConfiguration systemConfiguration, LogHelper log)
    {
      ConcurrentDictionary<string, MarketInfo> result = new ConcurrentDictionary<string, MarketInfo>();

      string marketInfoFilePath = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + Constants.PTMagicPathData + Path.DirectorySeparatorChar + Constants.PTMagicPathExchange + Path.DirectorySeparatorChar + "MarketInfo.json";
      if (File.Exists(marketInfoFilePath))
      {
        try
        {
          result = JsonConvert.DeserializeObject<ConcurrentDictionary<string, MarketInfo>>(System.IO.File.ReadAllText(marketInfoFilePath));
        }
        catch (Exception ex)
        {
          log.DoLogDebug(ex.Message);
        }
      }
      if (result == null)
      {
        result = new ConcurrentDictionary<string, MarketInfo>();
      }
      return result;
    }

    public static void SaveMarketInfosToFile(ConcurrentDictionary<string, MarketInfo> marketInfos, PTMagicConfiguration systemConfiguration, LogHelper log)
    {
      FileHelper.WriteTextToFile(Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + Constants.PTMagicPathData + Path.DirectorySeparatorChar + Constants.PTMagicPathExchange + Path.DirectorySeparatorChar, "MarketInfo.json", JsonConvert.SerializeObject(marketInfos));
    }

    private static List<FileInfo> GetMarketDataFileIndex(string platform)
    {
      string dataDirectoryPath = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + Constants.PTMagicPathData + Path.DirectorySeparatorChar + platform + Path.DirectorySeparatorChar;
      DateTime directoryLastWriteTimeUtc = Directory.GetLastWriteTimeUtc(dataDirectoryPath);

      lock (MarketDataFileIndexLock)
      {
        if (MarketDataFileIndexes.TryGetValue(platform, out MarketDataFileIndexEntry cachedIndex)
          && cachedIndex.DirectoryLastWriteTimeUtc == directoryLastWriteTimeUtc)
        {
          return cachedIndex.Files;
        }
      }

      DirectoryInfo dataDirectory = new DirectoryInfo(dataDirectoryPath);
      List<FileInfo> marketFiles = dataDirectory.EnumerateFiles("MarketData*")
        .Select(x => { x.Refresh(); return x; })
        .ToList();

      lock (MarketDataFileIndexLock)
      {
        MarketDataFileIndexes[platform] = new MarketDataFileIndexEntry
        {
          DirectoryLastWriteTimeUtc = directoryLastWriteTimeUtc,
          Files = marketFiles
        };
        MarketDataSnapshotCaches.Remove(platform);
      }

      return marketFiles;
    }

    private static FileInfo FindMarketDataFile(List<FileInfo> marketFiles, DateTime maxDateTime)
    {
      FileInfo marketFile = null;
      foreach (FileInfo candidate in marketFiles)
      {
        if (candidate.LastWriteTimeUtc <= maxDateTime
          && (marketFile == null || candidate.LastWriteTimeUtc > marketFile.LastWriteTimeUtc))
        {
          marketFile = candidate;
        }
      }

      if (marketFile != null)
      {
        return marketFile;
      }

      foreach (FileInfo candidate in marketFiles)
      {
        if (candidate.LastWriteTimeUtc >= maxDateTime
          && (marketFile == null || candidate.LastWriteTimeUtc < marketFile.LastWriteTimeUtc))
        {
          marketFile = candidate;
        }
      }

      return marketFile;
    }

    public static Dictionary<string, Market> GetMarketDataFromFile(PTMagicConfiguration systemConfiguration, LogHelper log, string platform, DateTime maxDateTime, string marketCaption)
    {
      Dictionary<string, Market> result = new Dictionary<string, Market>();
      List<FileInfo> marketFiles = GetMarketDataFileIndex(platform);
      FileInfo marketFile = FindMarketDataFile(marketFiles, maxDateTime);

      if (marketFile == null)
      {
        log.DoLogDebug(platform + " - Not able to load " + marketCaption + " market data. Loading next youngest market file instead.");
        log.DoLogDebug(platform + " - Not able to load " + marketCaption + " market data. No market data files found.");
        return result;
      }

      if (marketFile.LastWriteTimeUtc > maxDateTime)
      {
        log.DoLogDebug(platform + " - Not able to load " + marketCaption + " market data. Loading next youngest market file instead.");
      }

      log.DoLogDebug(platform + " - " + marketCaption + " market data loaded (" + marketFile.LastWriteTimeUtc.ToString() + ")");

      try
      {
        string snapshotKey = marketFile.FullName;
        lock (MarketDataFileIndexLock)
        {
          if (MarketDataSnapshotCaches.TryGetValue(platform, out Dictionary<string, Dictionary<string, Market>> platformCache)
            && platformCache.TryGetValue(snapshotKey, out Dictionary<string, Market> cachedSnapshot))
          {
            return cachedSnapshot;
          }
        }

        // Get JSON object
        result = JsonConvert.DeserializeObject<Dictionary<string, Market>>(marketFile.OpenText().ReadToEnd());
        if (result != null)
        {
          lock (MarketDataFileIndexLock)
          {
            if (!MarketDataSnapshotCaches.TryGetValue(platform, out Dictionary<string, Dictionary<string, Market>> platformCache))
            {
              platformCache = new Dictionary<string, Dictionary<string, Market>>(StringComparer.OrdinalIgnoreCase);
              MarketDataSnapshotCaches[platform] = platformCache;
            }
            platformCache[snapshotKey] = result;
          }
        }
      }
      catch (Exception ex)
      {
        log.DoLogCritical(ex.Message, ex);
      }
      return result;
    }


  }
}
