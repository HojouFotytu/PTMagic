using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using Core.Helper;
using Core.Main;
using Core.Main.DataObjects.PTMagicData;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Core.ProfitTrailer
{
  /// <summary>
  /// Controls PT's native global SOM flag without submitting any properties.
  /// Success means HTTP acknowledgement, not a separate readback of PT state.
  /// </summary>
  public static class GlobalSellOnlyModeAPI
  {
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly HttpClient SharedHttpClient = new HttpClient(new SocketsHttpHandler
    {
      AllowAutoRedirect = false,
      PooledConnectionLifetime = TimeSpan.FromMinutes(5),
      PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
      AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    }) { Timeout = TimeSpan.FromSeconds(30) };

    public static ConfigurationDeliveryResult Set(PTMagicConfiguration configuration, bool enabled,
      LogHelper log, Func<HttpClient> clientFactory = null, Func<bool> canSend = null)
    {
      bool attempted = false;
      HttpClient client = null;
      string action = enabled ? "enable" : "disable";
      try
      {
        Application application = configuration?.GeneralSettings?.Application;
        if (application == null) return Failure(action, "Application configuration is unavailable.", false);

        // This guard also protects callers outside the raid coordinator.
        if (application.TestMode)
        {
          if (canSend != null && !canSend()) return Failure(action, "The current raid is not fresh.", false, "Deferred");
          log?.DoLogInfo("TESTMODE: global SOM " + action + " preview; no API request sent.");
          return new ConfigurationDeliveryResult
          {
            Succeeded = true, Simulated = true, Status = "TestMode global SOM " + action + " preview"
          };
        }

        if (!TryBuildTargets(application, enabled, out List<Uri> targets, out string targetError))
          return Failure(action, targetError, false);

        client = clientFactory != null ? clientFactory() : SharedHttpClient;
        if (client == null) return Failure(action, "HTTP client is unavailable.", false);

        for (int index = 0; index < targets.Count; index++)
        {
          // A slow earlier target must not authorize release from an expired raid.
          if (canSend != null && !canSend())
            return Failure(action, "The current raid is no longer fresh; remaining targets were not contacted.", attempted, "Deferred");

          log?.DoLogInfo("Requesting global SOM " + action + " for PT target " + (index + 1) + "/" + targets.Count + ".");
          using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
          using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, targets[index]);
          attempted = true;
          using HttpResponseMessage response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .GetAwaiter().GetResult();

          if (!response.IsSuccessStatusCode)
            return Failure(action, "PT target " + (index + 1) + " returned HTTP " + (int)response.StatusCode + ".", attempted);

          string mediaType = response.Content?.Headers.ContentType?.MediaType ?? "";
          if (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
            return Failure(action, "PT target " + (index + 1) + " returned an HTML page instead of an API acknowledgement.", attempted);

          string body = response.Content == null ? "" : ReadBoundedBody(response.Content, timeout.Token);
          string responseError = GetResponseError(body, mediaType, enabled);
          if (responseError != null)
            return Failure(action, "PT target " + (index + 1) + " " + responseError, attempted);

          log?.DoLogInfo("PT target " + (index + 1) + " acknowledged global SOM " + action + ".");
        }

        return new ConfigurationDeliveryResult
        {
          Succeeded = true, Attempted = attempted,
          Status = "Global SOM " + action + " acknowledged"
        };
      }
      catch (OperationCanceledException)
      {
        return Failure(action, "The API request timed out or was canceled; state is unconfirmed and will be retried.", attempted);
      }
      catch (Exception exception)
      {
        // Exception messages can contain the token-bearing request URI.
        return Failure(action, "API request failed (" + exception.GetType().Name + "); state is unconfirmed.", attempted);
      }
      finally
      {
        if (clientFactory != null) client?.Dispose();
      }
    }

    private static ConfigurationDeliveryResult Failure(string action, string reason, bool attempted, string status = "Failed") =>
      new ConfigurationDeliveryResult
      {
        Succeeded = false, Attempted = attempted, Status = status,
        Error = "Global SOM " + action + ": " + reason
      };

    private static bool TryBuildTargets(Application application, bool enabled, out List<Uri> targets, out string error)
    {
      targets = new List<Uri>();
      error = "";
      if (String.IsNullOrWhiteSpace(application.ProfitTrailerMonitorURL)
        || String.IsNullOrWhiteSpace(application.ProfitTrailerServerAPIToken))
      {
        error = "ProfitTrailerMonitorURL and ProfitTrailerServerAPIToken are required.";
        return false;
      }

      string[] extraUrls = SplitExtras(application.ProfitTrailerMonitorURLXtra);
      string[] extraTokens = SplitExtras(application.ProfitTrailerServerAPITokenXtra);
      if (extraUrls.Any(String.IsNullOrWhiteSpace)
        || extraTokens.Any(String.IsNullOrWhiteSpace)
        || (extraTokens.Length > 0 && extraTokens.Length != extraUrls.Length))
      {
        error = "Extra API tokens must match the extra PT URLs in order, without empty entries; leave all extra tokens blank to reuse the primary token.";
        return false;
      }

      string[] urls = new[] { application.ProfitTrailerMonitorURL.Trim() }.Concat(extraUrls).ToArray();
      for (int index = 0; index < urls.Length; index++)
      {
        if (!Uri.TryCreate(urls[index], UriKind.Absolute, out Uri baseUri)
          || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
          || !String.IsNullOrEmpty(baseUri.Query) || !String.IsNullOrEmpty(baseUri.Fragment)
          || !String.IsNullOrEmpty(baseUri.UserInfo))
        {
          error = "PT target " + (index + 1) + " requires an HTTP(S) base URL without credentials, query, or fragment.";
          return false;
        }

        // Append a relative route to preserve installations under a context path.
        Uri directory = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        Uri endpoint = new Uri(directory, "api/v2/globalSellOnlyMode");
        string token = index == 0 || extraTokens.Length == 0
          ? application.ProfitTrailerServerAPIToken : extraTokens[index - 1];
        targets.Add(new UriBuilder(endpoint)
        {
          Query = "token=" + Uri.EscapeDataString(token) + "&enabled=" + (enabled ? "true" : "false")
        }.Uri);
      }
      return true;
    }

    private static string[] SplitExtras(string value) => String.IsNullOrWhiteSpace(value)
      ? Array.Empty<string>() : value.Split(',').Select(item => item.Trim()).ToArray();

    private static string ReadBoundedBody(HttpContent content, CancellationToken cancellationToken)
    {
      if (content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > MaximumResponseBytes)
        throw new InvalidDataException("API response exceeds its size limit.");
      using Stream stream = content.ReadAsStreamAsync(cancellationToken).GetAwaiter().GetResult();
      using MemoryStream body = new MemoryStream();
      byte[] buffer = new byte[4096];
      int count;
      while ((count = stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).GetAwaiter().GetResult()) > 0)
      {
        if (body.Length + count > MaximumResponseBytes)
          throw new InvalidDataException("API response exceeds its size limit.");
        body.Write(buffer, 0, count);
      }
      return Encoding.UTF8.GetString(body.ToArray());
    }

    private static string GetResponseError(string body, string mediaType, bool enabled)
    {
      string trimmed = (body ?? "").Trim().TrimStart('\uFEFF').TrimStart();
      if (trimmed.StartsWith("<", StringComparison.Ordinal))
        return "returned markup instead of an API acknowledgement.";
      if (trimmed.Length == 0) return null;
      bool expectsJson = mediaType.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0
        || trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal)
        || trimmed.Equals("true", StringComparison.Ordinal) || trimmed.Equals("false", StringComparison.Ordinal);
      if (!expectsJson) return null;

      JToken json;
      try { json = JToken.Parse(trimmed); }
      catch (JsonException) { return "returned invalid JSON."; }
      // A bare false cannot confirm enabling. When disabling it can be the
      // requested new value, so do not mistake it for a verified failure schema.
      if (enabled && json.Type == JTokenType.Boolean && !json.Value<bool>())
        return "returned false for the enable request.";
      if (json is JObject result)
      {
        foreach (JProperty property in result.Properties())
        {
          if (property.Name.Equals("success", StringComparison.OrdinalIgnoreCase)
            && property.Value.Type == JTokenType.Boolean && !property.Value.Value<bool>())
            return "explicitly reported failure.";
          if ((property.Name.Equals("error", StringComparison.OrdinalIgnoreCase)
            || property.Name.Equals("errors", StringComparison.OrdinalIgnoreCase)) && HasError(property.Value))
            return "explicitly reported an error.";
        }
      }
      return null;
    }

    private static bool HasError(JToken value) => value.Type switch
    {
      JTokenType.Null or JTokenType.Undefined => false,
      JTokenType.Boolean => value.Value<bool>(),
      JTokenType.String => !String.IsNullOrWhiteSpace(value.Value<string>()),
      JTokenType.Array or JTokenType.Object => value.HasValues,
      JTokenType.Integer or JTokenType.Float => value.Value<double>() != 0,
      _ => true
    };
  }
}
