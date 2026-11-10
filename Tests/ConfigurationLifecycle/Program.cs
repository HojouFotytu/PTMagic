using System.Net;
using System.Reflection;
using System.Text;
using Core.Helper;
using Core.Main;
using Core.Main.DataObjects.PTMagicData;
using Core.MarketAnalyzer.MarketData;
using Core.ProfitTrailer;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Engine = Core.Main.PTMagic;

internal static class Program
{
  private static readonly LogHelper Log = new LogHelper(NullLogger<LogHelper>.Instance);
  private static int _passed;
  private static readonly string[] InvalidMainMarkets =
    { null, "", " \t ", "US DT", "USD/T", "USD_T", "USDT?symbol=BTC", "USDT=USD", "\"USDT\"", "USD\uFF34", "u\u017Fdt" };

  private static int Main()
  {
    try
    {
      Check("main market parsing uses the exact key and preserves normalized non-USDT quotes", () =>
      {
        using var fixture = new Fixture();
        foreach (var quote in new[] { (Input: "btc", Expected: "BTC"), (Input: "usdc", Expected: "USDC"), (Input: "usd1", Expected: "USD1") })
        {
          var pairs = new List<string>
          {
            null, "", " # MARKET = USDT", "! MARKET = USD", "MARKET_CAP = ETH", "MARKETS = USDT", "DEFAULT_market = BNB",
            " \tmArKeT \t = " + quote.Input + " \t "
          };
          Require(SettingsHandler.GetMainMarket(fixture.Configuration, pairs, Log) == quote.Expected);
        }
      });
      Check("missing blank commented and prefix-only market definitions fail without a default quote", () =>
      {
        using var fixture = new Fixture();
        foreach (List<string> pairs in new List<string>[]
        {
          null, new List<string>(), new List<string> { null, "", " \t " },
          new List<string> { "# MARKET = USDT", "! MARKET = BTC", "MARKET_CAP = USDC", "MARKETS = ETH" },
          new List<string> { "MARKET" }, new List<string> { "MARKET =" }, new List<string> { " market \t= \t " }
        })
          ExpectPairsMarketError(() => SettingsHandler.GetMainMarket(fixture.Configuration, pairs, Log));
      });
      Check("malformed PAIRS quote values fail before becoming API symbols", () =>
      {
        using var fixture = new Fixture();
        foreach (string quote in InvalidMainMarkets)
          ExpectPairsMarketError(() => SettingsHandler.GetMainMarket(fixture.Configuration, new List<string> { "market = " + quote }, Log));
      });
      Check("null PAIRS DCA and INDICATORS groups inherit all default preset assignments", () =>
      {
        using var fixture = new Fixture();
        string[] pairs = File.ReadAllLines(fixture.DefaultPreset("pairs.properties"));
        string[] dca = File.ReadAllLines(fixture.DefaultPreset("dca.properties"));
        string[] indicators = File.ReadAllLines(fixture.DefaultPreset("indicators.properties"));
        var risk = fixture.Configuration.AnalyzerSettings.GlobalSettings.Single(setting => setting.SettingName == "Risk");
        risk.PairsProperties = null; risk.DCAProperties = null; risk.IndicatorsProperties = null;
        fixture.SelectRisk();
        Require(pairs.All(fixture.Bot.PairsLines.Contains) && dca.All(fixture.Bot.DCALines.Contains)
          && indicators.All(fixture.Bot.IndicatorsLines.Contains));
        Require(SettingsHandler.GetMainMarket(fixture.Configuration, fixture.Bot.PairsLines, Log) == "USDT");
        Require(fixture.Sent.Count == 0 && fixture.SomCalls.Count == 0 && !File.Exists(fixture.Checkpoint));
      });
      Check("fresh-summary bootstrap resolves the selected market without changing acknowledgement", () =>
      {
        foreach (bool previouslyAcknowledged in new[] { false, true })
        {
          using var fixture = new Fixture(); fixture.SelectRisk();
          if (previouslyAcknowledged) Call(fixture.Bot, "SaveProfitTrailerProperties");
          string confirmed = JsonConvert.SerializeObject(fixture.Bot.ConfigurationDelivery.Confirmed);
          string checkpoint = File.Exists(fixture.Checkpoint) ? File.ReadAllText(fixture.Checkpoint) : null;
          int sends = fixture.Sent.Count;
          File.WriteAllLines(fixture.DefaultPreset("pairs.properties"),
            new[] { "# fixture", "market = bTc", "DEFAULT_sell_only_mode_enabled = false" });
          fixture.Bot.LastRuntimeSummary = new Summary();
          fixture.Bot.PairsLines = null; fixture.Bot.DCALines = null; fixture.Bot.IndicatorsLines = null;
          Set(fixture.Bot, "_raidAnalysisBuilt", false);
          Call(fixture.Bot, "LoadCurrentProfitTrailerProperties");
          Require(fixture.Bot.LastRuntimeSummary.MainMarket == "BTC" && fixture.Bot.ActiveSettingName == "Risk");
          Require(fixture.Bot.PairsLines.Contains("DEFAULT_sell_only_mode_enabled = true"));
          Require(JsonConvert.SerializeObject(fixture.Bot.ConfigurationDelivery.Confirmed) == confirmed
            && fixture.Sent.Count == sends && fixture.SomCalls.Count == 0);
          Require(File.Exists(fixture.Checkpoint) == previouslyAcknowledged);
          if (previouslyAcknowledged) Require(File.ReadAllText(fixture.Checkpoint) == checkpoint);
        }
      });
      Check("missing market during bootstrap preserves the owned guard and sends no normal configuration", () =>
      {
        using var fixture = new Fixture(); Call(fixture.Bot, "EnsureStartupSellOnlyMode");
        string guardCheckpoint = File.ReadAllText(fixture.GuardCheckpoint);
        File.WriteAllLines(fixture.DefaultPreset("pairs.properties"),
          new[] { "# MARKET = USDT", "MARKET_CAP = USDC", "DEFAULT_sell_only_mode_enabled = false" });
        fixture.Bot.LastRuntimeSummary = new Summary(); Set(fixture.Bot, "_raidAnalysisBuilt", false);
        var error = ExpectPairsMarketError(() => Call(fixture.Bot, "LoadCurrentProfitTrailerProperties"));
        Require(error.Message.Contains("Default", StringComparison.Ordinal));
        Call(fixture.Bot, "RestoreRaidState"); Call(fixture.Bot, "UpdateDeliverySummary");
        Require(fixture.SomCalls.SequenceEqual(new[] { true }) && fixture.Sent.Count == 0);
        Require(fixture.Bot.LastRuntimeSummary.StartupSellOnlyModeActive && fixture.Bot.LastRuntimeSummary.StartupSellOnlyModeManualReviewRequired
          && Get<bool>(fixture.Bot, "_firstSuccessfulRaidPending"));
        Require(fixture.Bot.ConfigurationDelivery.Confirmed.AppliedPayloadHash == "" && !File.Exists(fixture.Checkpoint)
          && File.ReadAllText(fixture.GuardCheckpoint) == guardCheckpoint);
      });
      Check("all exchange price adapters return the USDT identity without I/O", () =>
      {
        using var fixture = new Fixture(); var canceled = new CancellationToken(true);
        foreach (var adapter in PriceAdapters(fixture))
          foreach (string quote in new[] { "USDT", "usdt", " \tuSdT \t" })
            Require(adapter.Read(quote, canceled).GetAwaiter().GetResult() == 1.0);
      });
      Check("USD identity is Binance US only and other quotes cannot silently become USDT", () =>
      {
        using var fixture = new Fixture(); var canceled = new CancellationToken(true);
        foreach (var adapter in PriceAdapters(fixture))
        {
          if (adapter.Name == "BinanceUS") Require(adapter.Read(" usd ", canceled).GetAwaiter().GetResult() == 1.0);
          else ExpectException<OperationCanceledException>(() => adapter.Read("USD", canceled).GetAwaiter().GetResult());
          foreach (string quote in new[] { "BTC", "USDC" })
            ExpectException<OperationCanceledException>(() => adapter.Read(quote, canceled).GetAwaiter().GetResult());
        }
      });
      Check("exchange price adapters reject invalid quotes before I/O cancellation", () =>
      {
        using var fixture = new Fixture(); var canceled = new CancellationToken(true);
        foreach (var adapter in PriceAdapters(fixture))
          foreach (string quote in InvalidMainMarkets)
          {
            var error = ExpectException<ArgumentException>(() => adapter.Read(quote, canceled).GetAwaiter().GetResult());
            Require(error.ParamName == "mainMarket");
          }
      });
      Check("exchange market-data adapters reject invalid quotes before collection or futures metadata refresh", () =>
      {
        using var fixture = new Fixture(); var canceled = new CancellationToken(true);
        foreach (var adapter in MarketDataAdapters(fixture))
          foreach (string quote in InvalidMainMarkets)
          {
            var error = ExpectException<ArgumentException>(() => adapter.Read(quote, canceled).GetAwaiter().GetResult());
            Require(error.ParamName == "mainMarket");
          }
        Require(fixture.Bot.MarketInfos.Count == 0 && !Directory.Exists(Path.Combine(fixture.Root, Constants.PTMagicPathData, Constants.PTMagicPathExchange)));
      });
      Check("delivery success advances confirmed acknowledgement", () =>
      {
        var coordinator = new ConfigurationDeliveryCoordinator(); var payload = Payload("A");
        var result = coordinator.Deliver(payload, _ => ConfigurationDeliveryResult.Applied(), false);
        Require(result.Succeeded && !coordinator.Pending && coordinator.Confirmed.AppliedPayloadHash == payload.PayloadHash && coordinator.Confirmed.AppliedAtUtc.HasValue);
      });
      Check("failed delivery preserves acknowledgement and identical retry sends", () =>
      {
        var coordinator = new ConfigurationDeliveryCoordinator(); var payload = Payload("A"); int calls = 0;
        var failed = coordinator.Deliver(payload, _ => { calls++; return ConfigurationDeliveryResult.Failed("fake failure"); }, false);
        Require(!failed.Succeeded && coordinator.Pending && coordinator.Confirmed.AppliedPayloadHash == "");
        var retried = coordinator.Deliver(payload, _ => { calls++; return ConfigurationDeliveryResult.Applied(); }, false);
        Require(retried.Succeeded && calls == 2 && !coordinator.Pending);
      });
      Check("partial B failure forces resend when desired returns to confirmed A", () =>
      {
        var coordinator = new ConfigurationDeliveryCoordinator(); var a = Payload("A"); int calls = 0;
        coordinator.Deliver(a, _ => { calls++; return ConfigurationDeliveryResult.Applied(); }, false);
        coordinator.Deliver(Payload("B"), _ => { calls++; return ConfigurationDeliveryResult.Failed("second target failed"); }, false);
        Require(coordinator.Confirmed.AppliedPayloadHash == a.PayloadHash && coordinator.Pending);
        coordinator.Deliver(a, _ => { calls++; return ConfigurationDeliveryResult.Applied(); }, false);
        Require(calls == 3 && !coordinator.Pending && coordinator.Confirmed.AppliedPayloadHash == a.PayloadHash);
      });
      Check("unchanged acknowledged payload skips transport but force replays", () =>
      {
        var coordinator = new ConfigurationDeliveryCoordinator(); var payload = Payload("A"); int calls = 0;
        ConfigurationDeliveryResult Send(ConfigurationPayload _) { calls++; return ConfigurationDeliveryResult.Applied(); }
        coordinator.Deliver(payload, Send, false);
        var unchanged = coordinator.Deliver(payload, Send, false);
        Require(calls == 1 && unchanged.Succeeded && !unchanged.Attempted);
        coordinator.Deliver(payload, Send, false, true); Require(calls == 2);
      });
      Check("transport exceptions and null acknowledgement remain pending", () =>
      {
        var coordinator = new ConfigurationDeliveryCoordinator();
        Require(!coordinator.Deliver(Payload("A"), _ => throw new TimeoutException("fake timeout"), false).Succeeded && coordinator.Pending);
        Require(!coordinator.Deliver(Payload("A"), _ => null, false).Succeeded && coordinator.Pending);
        Require(coordinator.Confirmed.AppliedPayloadHash == "");
      });
      Check("comments do not dirty payload but destination changes do", () =>
      {
        var first = Payload("A");
        var comments = new ConfigurationPayload("A", new[] { "# a new timestamp", "DEFAULT_sell_only_mode_enabled = false", "" }, first.Dca, first.Indicators, "destination");
        Require(first.PayloadHash == comments.PayloadHash);
        var moved = new ConfigurationPayload("A", first.Pairs, first.Dca, first.Indicators, "other destination");
        Require(first.PayloadHash != moved.PayloadHash);
      });
      Check("TestMode preview neither calls sender nor overwrites checkpoint", () =>
      {
        using var fixture = new Fixture(); var coordinator = new ConfigurationDeliveryCoordinator();
        coordinator.Deliver(Payload("A"), _ => ConfigurationDeliveryResult.Applied(), false);
        string path = Path.Combine(fixture.Root, "acknowledgement.json"); coordinator.SaveConfirmed(path);
        string before = File.ReadAllText(path); int calls = 0;
        var preview = coordinator.Deliver(Payload("B"), _ => { calls++; return ConfigurationDeliveryResult.Applied(); }, true);
        coordinator.SaveConfirmed(path);
        Require(preview.Simulated && calls == 0 && coordinator.Confirmed.AppliedSettingName == "A" && File.ReadAllText(path) == before);
      });
      Check("checkpoint reload contains only acknowledgement metadata", () =>
      {
        using var fixture = new Fixture(); var coordinator = new ConfigurationDeliveryCoordinator();
        var payload = new ConfigurationPayload("A", new[] { "secret-property = dummy-sensitive-value" }, Array.Empty<string>(), Array.Empty<string>(), "destination-hash");
        coordinator.Deliver(payload, _ => ConfigurationDeliveryResult.Applied(), false);
        string path = Path.Combine(fixture.Root, "acknowledgement.json"); coordinator.SaveConfirmed(path);
        var restored = ConfigurationDeliveryCoordinator.Load(path);
        Require(restored.AppliedPayloadHash == payload.PayloadHash && !File.ReadAllText(path).Contains("dummy-sensitive-value", StringComparison.Ordinal));
      });
      Check("global SOM POST preserves base path encodes query and sends no properties", () =>
      {
        using var fixture = new Fixture(); var wire = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK));
        fixture.Configuration.GeneralSettings.Application.ProfitTrailerMonitorURL = "http://pt-one.invalid/example/";
        fixture.Configuration.GeneralSettings.Application.ProfitTrailerServerAPIToken = "dummy token&=+#?/%";
        Require(SomHttp(fixture, wire, true).Succeeded && wire.Calls == 1);
        Uri request = wire.Uris.Single(); var query = Query(request);
        Require(wire.Methods.Single() == HttpMethod.Post && request.AbsolutePath == "/example/api/v2/globalSellOnlyMode");
        Require(query.Count == 2 && query["enabled"] == "true" && query["token"] == "dummy token&=+#?/%");
        Require(wire.Bodies.Single() == "" && !request.Query.Contains("pairsData", StringComparison.Ordinal)
          && !request.Query.Contains("configName", StringComparison.Ordinal) && !request.Query.Contains("license", StringComparison.Ordinal));
      });
      Check("global SOM disable is rejected before any HTTP request", () =>
      {
        using var fixture = new Fixture(); var wire = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        Require(!SomHttp(fixture, wire, false).Succeeded && wire.Calls == 0);
      });
      Check("global SOM uses distinct API tokens for additional PT targets", () =>
      {
        using var fixture = new Fixture(); fixture.AddSecondTarget();
        fixture.Configuration.GeneralSettings.Application.ProfitTrailerMonitorURLXtra = "http://pt-two.invalid/second/";
        fixture.Configuration.GeneralSettings.Application.ProfitTrailerServerAPITokenXtra = "dummy-token-two";
        var wire = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK));
        Require(SomHttp(fixture, wire, true).Succeeded && wire.Calls == 2);
        Require(Query(wire.Uris[0])["token"] == "dummy-token-one" && Query(wire.Uris[1])["token"] == "dummy-token-two");
        Require(wire.Uris[1].AbsolutePath == "/second/api/v2/globalSellOnlyMode" && wire.Methods.All(method => method == HttpMethod.Post));
      });
      Check("blank extra token list explicitly reuses the primary API token", () =>
      {
        using var fixture = new Fixture(); fixture.AddSecondTarget();
        var wire = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK));
        Require(SomHttp(fixture, wire, true).Succeeded && wire.Calls == 2);
        Require(wire.Uris.All(uri => Query(uri)["token"] == "dummy-token-one"));
      });
      Check("global SOM token count mismatch fails before any HTTP request", () =>
      {
        using var fixture = new Fixture(); fixture.AddSecondTarget();
        fixture.Configuration.GeneralSettings.Application.ProfitTrailerServerAPITokenXtra = "dummy-token-two,dummy-token-three";
        var wire = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK));
        Require(!SomHttp(fixture, wire, true).Succeeded && wire.Calls == 0);
      });
      Check("global SOM missing primary token fails before any HTTP request", () =>
      {
        using var fixture = new Fixture(); fixture.Configuration.GeneralSettings.Application.ProfitTrailerServerAPIToken = "";
        var wire = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK));
        Require(!SomHttp(fixture, wire, true).Succeeded && wire.Calls == 0);
      });
      Check("global SOM rejects unauthorized server-error and redirect responses", () =>
      {
        using var fixture = new Fixture();
        foreach (HttpStatusCode status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.InternalServerError, HttpStatusCode.Redirect, HttpStatusCode.NotModified })
        {
          var wire = new FakeHttp(_ => new HttpResponseMessage(status));
          Require(!SomHttp(fixture, wire, true).Succeeded && wire.Calls == 1);
        }
      });
      Check("global SOM rejects a successful HTTP login page", () =>
      {
        using var fixture = new Fixture();
        var wire = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK)
          { Content = new StringContent("<!doctype html><html>Login</html>", Encoding.UTF8, "text/html") });
        Require(!SomHttp(fixture, wire, true).Succeeded && wire.Calls == 1);
      });
      Check("global SOM rejects explicit API failure without exposing response text", () =>
      {
        using var fixture = new Fixture();
        var wire = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK)
          { Content = new StringContent("{\"success\":false,\"message\":\"dummy-sensitive-response\"}", Encoding.UTF8, "application/json") });
        var result = SomHttp(fixture, wire, true);
        Require(!result.Succeeded && wire.Calls == 1 && !result.Error.Contains("dummy-sensitive-response", StringComparison.Ordinal));
      });
      Check("global SOM timeout stays failed and redacts token-bearing exception text", () =>
      {
        using var fixture = new Fixture();
        var wire = new FakeHttp(_ => throw new TaskCanceledException("http://pt-one.invalid/api?token=dummy-token-one"));
        var result = SomHttp(fixture, wire, true);
        Require(!result.Succeeded && wire.Calls == 1 && !result.Error.Contains("dummy-token-one", StringComparison.Ordinal)
          && !result.Error.Contains("pt-one.invalid", StringComparison.Ordinal));
      });
      Check("global SOM partial multi-target failure cannot acknowledge all targets", () =>
      {
        using var fixture = new Fixture(); fixture.AddSecondTarget();
        var wire = new FakeHttp(index => new HttpResponseMessage(index == 1 ? HttpStatusCode.OK : HttpStatusCode.InternalServerError));
        Require(!SomHttp(fixture, wire, true).Succeeded && wire.Calls == 2);
      });
      Check("global SOM disable remains blocked with multiple targets and any freshness state", () =>
      {
        using var fixture = new Fixture(); fixture.AddSecondTarget();
        var wire = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK));
        Require(!SomHttp(fixture, wire, false, () => true).Succeeded && wire.Calls == 0);
        Require(!SomHttp(fixture, wire, false, () => false).Succeeded && wire.Calls == 0);
      });
      Check("HTTP 200 sends encoded PAIRS DCA and INDICATORS", () =>
      {
        using var fixture = new Fixture(); var wire = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var result = Http(fixture, wire);
        string decoded = WebUtility.UrlDecode(wire.Bodies.Single());
        Require(result.Succeeded && wire.Calls == 1 && decoded.Contains("pairsData=MARKET = USDT") && decoded.Contains("dcaData=DEFAULT_DCA_enabled = true") && decoded.Contains("indicatorsData=RSI_candle_period = 300"));
      });
      Check("HTTP 304 acknowledges unchanged target configuration", () =>
      {
        using var fixture = new Fixture(); var wire = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        Require(Http(fixture, wire).Succeeded && wire.Calls == 1);
      });
      Check("HTTP 401 cannot acknowledge configuration", () =>
      {
        using var fixture = new Fixture(); var wire = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Require(!Http(fixture, wire).Succeeded && wire.Calls == 1);
      });
      Check("HTTP 500 cannot acknowledge configuration", () =>
      {
        using var fixture = new Fixture(); var wire = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        Require(!Http(fixture, wire).Succeeded && wire.Calls == 1);
      });
      Check("HTTP timeout retries three times then fails without waiting", () =>
      {
        using var fixture = new Fixture(); var wire = new FakeHttp(_ => throw new TaskCanceledException("deterministic fake timeout"));
        Require(!Http(fixture, wire).Succeeded && wire.Calls == 4);
      });
      Check("HTTP timeout followed by acknowledgement succeeds", () =>
      {
        using var fixture = new Fixture(); var wire = new FakeHttp(index => index == 1 ? throw new TimeoutException("fake timeout") : new HttpResponseMessage(HttpStatusCode.OK));
        Require(Http(fixture, wire).Succeeded && wire.Calls == 2);
      });
      Check("all HTTP targets must acknowledge the same payload", () =>
      {
        using var fixture = new Fixture(); fixture.AddSecondTarget();
        var wire = new FakeHttp(index => new HttpResponseMessage(index == 1 ? HttpStatusCode.OK : HttpStatusCode.InternalServerError));
        Require(!Http(fixture, wire).Succeeded && wire.Calls == 2 && wire.Hosts.SequenceEqual(new[] { "pt-one.invalid", "pt-two.invalid" }));
      });
      Check("freshness expiration between HTTP targets stops later sends", () =>
      {
        using var fixture = new Fixture(); fixture.AddSecondTarget(); var wire = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK));
        Require(!Http(fixture, wire, () => wire.Calls == 0).Succeeded && wire.Calls == 1);
      });
      Check("target license mismatch fails before transport", () =>
      {
        using var fixture = new Fixture(); fixture.Configuration.GeneralSettings.Application.ProfitTrailerMonitorURLXtra = "http://pt-two.invalid/";
        var wire = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK));
        Require(!Http(fixture, wire).Succeeded && wire.Calls == 0);
      });
      Check("startup API guard leaves all compiled property sets unchanged", () =>
      {
        using var fixture = new Fixture();
        var pairs = fixture.Bot.PairsLines.ToArray(); var dca = fixture.Bot.DCALines.ToArray(); var indicators = fixture.Bot.IndicatorsLines.ToArray();
        Call(fixture.Bot, "EnsureStartupSellOnlyMode");
        Require(fixture.SomCalls.SequenceEqual(new[] { true }) && fixture.Sent.Count == 0);
        Require(fixture.Bot.PairsLines.SequenceEqual(pairs) && fixture.Bot.DCALines.SequenceEqual(dca) && fixture.Bot.IndicatorsLines.SequenceEqual(indicators));
        Require(fixture.Bot.ConfigurationDelivery.Confirmed.AppliedPayloadHash == "" && !File.Exists(fixture.Checkpoint));
        Require(Get<bool>(fixture.Bot, "_startupGuardActive") && !Get<bool>(fixture.Bot, "_startupGuardPending")
          && Get<bool>(fixture.Bot, "_startupGuardManualReviewRequired"));
      });
      Check("startup API guard does not overwrite the normal configuration acknowledgement", () =>
      {
        using var fixture = new Fixture(); Call(fixture.Bot, "SaveProfitTrailerProperties");
        string before = File.ReadAllText(fixture.Checkpoint); int sends = fixture.Sent.Count;
        Call(fixture.Bot, "InitializeDeliveryTracking"); Call(fixture.Bot, "EnsureStartupSellOnlyMode");
        Require(fixture.SomCalls.SequenceEqual(new[] { true }) && fixture.Sent.Count == sends && File.ReadAllText(fixture.Checkpoint) == before);
      });
      Check("startup SOM remains enabled after conclusive acknowledged normal delivery", () =>
      {
        using var fixture = new Fixture(); Call(fixture.Bot, "EnsureStartupSellOnlyMode");
        Set(fixture.Bot, "_selectionIndeterminate", true); fixture.CompileClean(); Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.Sent.Count == 0 && fixture.SomCalls.SequenceEqual(new[] { true }));
        Set(fixture.Bot, "_selectionIndeterminate", false);
        fixture.Next = ConfigurationDeliveryResult.Failed("transport failure");
        fixture.CompileClean(); Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.Sent.Count == 1 && fixture.SomCalls.SequenceEqual(new[] { true }));
        fixture.Next = ConfigurationDeliveryResult.Applied();
        fixture.CompileClean(); Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.Sent.Count == 2 && fixture.SomCalls.SequenceEqual(new[] { true }));
        Require(Get<bool>(fixture.Bot, "_startupGuardActive") &&
          !Get<bool>(fixture.Bot, "_startupGuardPending") &&
          !Get<bool>(fixture.Bot, "_firstSuccessfulRaidPending"));
        Require(fixture.Events.SequenceEqual(new[] { "som:true", "configuration", "configuration" }));
      });
      Check("failed SOM enable never automatically retries over a user's manual choice", () =>
      {
        using var fixture = new Fixture(); fixture.SomNext = ConfigurationDeliveryResult.Failed("offline target");
        bool threw = false;
        try { Call(fixture.Bot, "EnsureStartupSellOnlyMode"); } catch (InvalidOperationException) { threw = true; }
        Require(threw && Get<bool>(fixture.Bot, "_startupGuardPending") &&
          Get<bool>(fixture.Bot, "_startupGuardManualReviewRequired"));
        Require(fixture.Sent.Count == 0 && !fixture.Bot.ConfigurationDelivery.Pending);
        fixture.SomNext = ConfigurationDeliveryResult.Applied();
        Call(fixture.Bot, "EnsureStartupSellOnlyMode");
        Require(fixture.SomCalls.SequenceEqual(new[] { true }) && fixture.Sent.Count == 0);
        Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.Sent.Count == 0 &&
          fixture.Bot.LastRuntimeSummary.ConfigurationDeliveryStatus == "Deferred");
      });
      Check("stale raid sends no normal configuration and retains startup guard", () =>
      {
        using var fixture = new Fixture(); Call(fixture.Bot, "EnsureStartupSellOnlyMode"); fixture.CompileClean();
        Set(fixture.Bot, "_raidAnalysisBuilt", false); SetStatic(typeof(BinanceFuturesAnalyticsRuntime), "_currentRaidAsOfUtc", DateTimeOffset.UtcNow.AddHours(-1));
        Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.Sent.Count == 0 && fixture.SomCalls.SequenceEqual(new[] { true })
          && fixture.Bot.LastRuntimeSummary.ConfigurationDeliveryStatus == "Deferred" &&
          Get<bool>(fixture.Bot, "_startupGuardManualReviewRequired"));
      });
      Check("normal settings acknowledgement never triggers SOM off", () =>
      {
        using var fixture = new Fixture(); Call(fixture.Bot, "EnsureStartupSellOnlyMode");
        fixture.SelectRisk(); Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.Sent.Count == 1 && fixture.SomCalls.SequenceEqual(new[] { true }));
        Require(fixture.Bot.ConfigurationDelivery.Confirmed.AppliedSettingName == "Risk" &&
          !Get<bool>(fixture.Bot, "_firstSuccessfulRaidPending"));
        fixture.BeforeRaid(); fixture.CompileClean();
        Call(fixture.Bot, "EnsureStartupSellOnlyMode"); Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.SomCalls.SequenceEqual(new[] { true }) &&
          Get<bool>(fixture.Bot, "_startupGuardManualReviewRequired"));
      });
      Check("stale and indeterminate raids never release global SOM", () =>
      {
        using var fixture = new Fixture(); Call(fixture.Bot, "EnsureStartupSellOnlyMode");
        Set(fixture.Bot, "_selectionIndeterminate", true);
        Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.Sent.Count == 0 && fixture.SomCalls.SequenceEqual(new[] { true }));
        Set(fixture.Bot, "_selectionIndeterminate", false);
        fixture.AfterConfigurationSend = () => SetStatic(typeof(BinanceFuturesAnalyticsRuntime),
          "_currentRaidAsOfUtc", DateTimeOffset.UtcNow.AddHours(-1));
        Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.Sent.Count == 1 && fixture.SomCalls.SequenceEqual(new[] { true }));
      });
      Check("startup guard checkpoint restores pending control without storing credentials or properties", () =>
      {
        using var fixture = new Fixture(); bool intentSavedBeforeEnable = false;
        fixture.BeforeSomSend = enabled =>
        {
          if (enabled)
          {
            var state = StartupSellOnlyModeState.Load(fixture.GuardCheckpoint);
            intentSavedBeforeEnable = state?.EnableIntentPending == true &&
              state.ManualReviewRequired && !state.EnabledAcknowledged;
          }
        };
        Call(fixture.Bot, "EnsureStartupSellOnlyMode");
        Require(intentSavedBeforeEnable); fixture.BeforeSomSend = null;
        string json = File.ReadAllText(fixture.GuardCheckpoint);
        Require(!json.Contains("dummy-token-one", StringComparison.Ordinal) && !json.Contains("pt-one.invalid", StringComparison.Ordinal)
          && !json.Contains("DEFAULT_sell_only_mode_enabled", StringComparison.Ordinal));
        Set(fixture.Bot, "_startupGuardPending", false); Set(fixture.Bot, "_startupGuardActive", false);
        Call(fixture.Bot, "InitializeDeliveryTracking");
        Require(!Get<bool>(fixture.Bot, "_startupGuardPending") &&
          Get<bool>(fixture.Bot, "_startupGuardManualReviewRequired"));
        Call(fixture.Bot, "EnsureStartupSellOnlyMode");
        Require(fixture.SomCalls.SequenceEqual(new[] { true }) && fixture.Sent.Count == 0);
      });
      Check("startup opt-out sends no SOM request when no owned operation is pending", () =>
      {
        using var fixture = new Fixture(); fixture.Configuration.GeneralSettings.Application.StartupSellOnlyMode = false;
        Call(fixture.Bot, "EnsureStartupSellOnlyMode"); Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.SomCalls.Count == 0 && fixture.Sent.Count == 1 && !File.Exists(fixture.GuardCheckpoint));
      });
      Check("startup opt-out never disables previously enabled SOM", () =>
      {
        using var fixture = new Fixture(); Call(fixture.Bot, "EnsureStartupSellOnlyMode");
        fixture.Configuration.GeneralSettings.Application.StartupSellOnlyMode = false;
        Call(fixture.Bot, "InitializeDeliveryTracking");
        Call(fixture.Bot, "EnsureStartupSellOnlyMode"); Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.SomCalls.SequenceEqual(new[] { true }) && fixture.Sent.Count == 1 &&
          Get<bool>(fixture.Bot, "_startupGuardManualReviewRequired"));
      });
      Check("first two startup raids display SOM notice, third clears", () =>
      {
        using var fixture = new Fixture();
        Call(fixture.Bot, "EnsureStartupSellOnlyMode");
        for (int raid = 1; raid <= 3; raid++)
        {
          Call(fixture.Bot, "CountStartupSomNoticeRaid");
          Call(fixture.Bot, "UpdateDeliverySummary");
          Require(fixture.Bot.LastRuntimeSummary.StartupSellOnlyModeNoticeVisible == (raid <= 2));
        }
        Require(fixture.SomCalls.SequenceEqual(new[] { true }));
        Require(StartupSellOnlyModeState.Load(fixture.GuardCheckpoint).NoticeRaidCount == 3);
      });
      Check("legacy release checkpoint migrates into review-only without disabling SOM", () =>
      {
        using var fixture = new Fixture();
        var fingerprint = typeof(Engine).GetMethod("StartupGuardDestinationFingerprint",
          BindingFlags.Instance | BindingFlags.NonPublic);
        string destination = (string)fingerprint.Invoke(fixture.Bot, null);
        File.WriteAllText(fixture.GuardCheckpoint, JsonConvert.SerializeObject(new
        {
          SchemaVersion = 1, DestinationHash = destination,
          PendingRelease = true, EnabledAcknowledged = true
        }));
        var restored = StartupSellOnlyModeState.Load(fixture.GuardCheckpoint);
        Require(restored.SchemaVersion == 2 && restored.ManualReviewRequired &&
          !restored.EnableIntentPending);
        fixture.Configuration.GeneralSettings.Application.StartupSellOnlyMode = false;
        Call(fixture.Bot, "InitializeDeliveryTracking");
        Call(fixture.Bot, "EnsureStartupSellOnlyMode");
        Require(fixture.SomCalls.Count == 0 &&
          Get<bool>(fixture.Bot, "_startupGuardManualReviewRequired"));
      });
      Check("a new process can enable the next startup without any automatic release", () =>
      {
        using var fixture = new Fixture();
        Call(fixture.Bot, "EnsureStartupSellOnlyMode");
        Call(fixture.Bot, "CountStartupSomNoticeRaid");
        // Simulate process restart with a matching previous enable checkpoint.
        Set(fixture.Bot, "_startupEnableAttemptedThisProcess", false);
        Call(fixture.Bot, "InitializeDeliveryTracking");
        Call(fixture.Bot, "EnsureStartupSellOnlyMode");
        Call(fixture.Bot, "CountStartupSomNoticeRaid");
        Require(fixture.SomCalls.SequenceEqual(new[] { true, true }) &&
          fixture.Bot.LastRuntimeSummary.StartupSellOnlyModeNoticeVisible &&
          StartupSellOnlyModeState.Load(fixture.GuardCheckpoint).NoticeRaidCount == 1);
      });
      Check("TestMode previews startup enable without PT API calls or live checkpoint writes", () =>
      {
        using var fixture = new Fixture(); fixture.Configuration.GeneralSettings.Application.TestMode = true; Call(fixture.Bot, "EnsureDeliveryContext");
        Call(fixture.Bot, "EnsureStartupSellOnlyMode"); Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.SomCalls.Count == 0 && fixture.Sent.Count == 0 && !File.Exists(fixture.GuardCheckpoint) && !File.Exists(fixture.Checkpoint));
        Require(!Get<bool>(fixture.Bot, "_startupGuardPending"));
      });
      Check("TestMode cannot change persisted live global SOM ownership", () =>
      {
        using var fixture = new Fixture(); Call(fixture.Bot, "EnsureStartupSellOnlyMode"); string before = File.ReadAllText(fixture.GuardCheckpoint);
        fixture.Configuration.GeneralSettings.Application.TestMode = true; Call(fixture.Bot, "EnsureDeliveryContext");
        Call(fixture.Bot, "EnsureStartupSellOnlyMode"); Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.SomCalls.SequenceEqual(new[] { true }) && fixture.Sent.Count == 0 && File.ReadAllText(fixture.GuardCheckpoint) == before);
      });
      Check("volume-only configuration still defers when its completed raid pin expires", () =>
      {
        using var fixture = new Fixture(); SetStatic(typeof(BinanceFuturesAnalyticsRuntime), "_currentRaidAsOfUtc", DateTimeOffset.UtcNow.AddHours(-1));
        Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.Sent.Count == 0 && fixture.Bot.LastRuntimeSummary.ConfigurationDeliveryStatus == "Deferred");
      });
      Check("last SMS removal on clean rebuilt properties still sends removal", () =>
      {
        using var fixture = new Fixture(); fixture.Bot.MarketList = new List<string> { "BTCUSDT" }; fixture.RecentVolume(100);
        Call(fixture.Bot, "ApplySingleMarketSettings"); Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.Sent.Count == 1 && fixture.Sent[0].Pairs.Any(line => line.StartsWith("BTC_sell_only_mode_enabled", StringComparison.Ordinal)));
        fixture.BeforeRaid(); fixture.CompileClean(); fixture.RecentVolume(1);
        Require(!fixture.Bot.PairsLines.Any(line => line.Contains("PTMagic_SingleMarketSettings", StringComparison.Ordinal)));
        Call(fixture.Bot, "ApplySingleMarketSettings");
        Require(fixture.Bot.SingleMarketSettingChanged && fixture.Bot.TriggeredSingleMarketSettings.Count == 0 && fixture.Bot.SingleMarketSettingSummaries.Count == 0);
        Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.Sent.Count == 2 && !fixture.Sent[1].Pairs.Any(line => line.StartsWith("BTC_sell_only_mode_enabled", StringComparison.Ordinal)));
      });
      Check("failed switch restores applied view and successful fresh retry commits desired setting", () =>
      {
        using var fixture = new Fixture(); Call(fixture.Bot, "SaveProfitTrailerProperties"); fixture.BeforeRaid();
        fixture.SelectRisk(); fixture.Next = ConfigurationDeliveryResult.Failed("fake failure"); Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.Bot.ActiveSettingName == "Default" && fixture.Bot.LastRuntimeSummary.AppliedGlobalSettingName == "Default" && fixture.Bot.LastRuntimeSummary.DesiredGlobalSettingName == "Risk" && fixture.Bot.ConfigurationDelivery.Pending);
        fixture.Next = ConfigurationDeliveryResult.Applied(); fixture.SelectRisk(); Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.Sent.Count == 3 && fixture.Bot.ActiveSettingName == "Risk" && fixture.Bot.ConfigurationDelivery.Confirmed.AppliedSettingName == "Risk");
      });
      Check("real TestMode save never sends or changes live acknowledgement", () =>
      {
        using var fixture = new Fixture(); Call(fixture.Bot, "SaveProfitTrailerProperties");
        string checkpoint = File.ReadAllText(fixture.Checkpoint); int sends = fixture.Sent.Count;
        fixture.Configuration.GeneralSettings.Application.TestMode = true; Call(fixture.Bot, "EnsureDeliveryContext"); fixture.SelectRisk();
        Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.Sent.Count == sends && File.ReadAllText(fixture.Checkpoint) == checkpoint && fixture.Bot.ConfigurationDelivery.Confirmed.AppliedSettingName == "Default" && fixture.Bot.LastRuntimeSummary.ConfigurationDeliveryStatus.StartsWith("TestMode", StringComparison.Ordinal));
      });
      Check("same-name configuration reload retries changed properties after failure", () =>
      {
        using var fixture = new Fixture(); Call(fixture.Bot, "SaveProfitTrailerProperties"); string priorHash = fixture.Bot.ConfigurationDelivery.Confirmed.AppliedPayloadHash;
        fixture.BeforeRaid(); fixture.Bot.ActiveSetting.PairsProperties["DEFAULT_sell_only_mode_enabled"] = true; fixture.CompileClean();
        fixture.Next = ConfigurationDeliveryResult.Failed("fake failure"); Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.Bot.ConfigurationDelivery.Pending && fixture.Bot.ConfigurationDelivery.Confirmed.AppliedPayloadHash == priorHash);
        fixture.Next = ConfigurationDeliveryResult.Applied(); fixture.CompileClean(); Call(fixture.Bot, "SaveProfitTrailerProperties");
        Require(fixture.Sent.Count == 3 && fixture.Bot.ConfigurationDelivery.Confirmed.AppliedSettingName == "Default" && fixture.Bot.ConfigurationDelivery.Confirmed.AppliedPayloadHash != priorHash);
      });
      Check("TestMode SMS persistence cannot replace live off-trigger latch", () =>
      {
        using var fixture = new Fixture(); fixture.Bot.SingleMarketSettingSummaries = new List<SingleMarketSettingSummary> { fixture.SmsSummary("BTCUSDT") };
        Call(fixture.Bot, "SaveSingleMarketSettingsSummary"); string livePath = Path.Combine(fixture.Root, Constants.PTMagicPathData, "SingleMarketSettingSummary.json"); string live = File.ReadAllText(livePath);
        fixture.Configuration.GeneralSettings.Application.TestMode = true; fixture.Bot.SingleMarketSettingSummaries = new List<SingleMarketSettingSummary> { fixture.SmsSummary("ETHUSDT") };
        Call(fixture.Bot, "SaveSingleMarketSettingsSummary");
        Require(File.ReadAllText(livePath) == live && File.Exists(Path.Combine(fixture.Root, Constants.PTMagicPathData, "SingleMarketSettingSummary.TestMode.json")));
        fixture.Configuration.GeneralSettings.Application.TestMode = false; Call(fixture.Bot, "LoadSMSSummaries");
        Require(fixture.Bot.SingleMarketSettingSummaries.Single().Market == "BTCUSDT");
      });
      Check("missing volume remains unknown in AND with a known age condition", () =>
      {
        using var fixture = new Fixture(); fixture.Bot.MarketList = new List<string> { "BTCUSDT" };
        fixture.Sms.Triggers.Add(new Trigger { Tag = "Age", AgeDaysLowerThan = 7 });
        fixture.Bot.MarketInfos["BTCUSDT"] = new MarketInfo { Name = "BTCUSDT", FirstSeen = DateTime.UtcNow.AddDays(-1) };
        Call(fixture.Bot, "ApplySingleMarketSettings");
        Require(fixture.Bot.TriggeredSingleMarketSettings.Count == 0 && Get<bool>(fixture.Bot, "_selectionIndeterminate"));
      });
      Check("unknown activation retains existing SMS without off-triggers", () =>
      {
        using var fixture = new Fixture(); fixture.Bot.MarketList = new List<string> { "BTCUSDT" };
        var original = fixture.SmsSummary("BTCUSDT"); fixture.Bot.SingleMarketSettingSummaries.Add(original);
        Call(fixture.Bot, "ApplySingleMarketSettings");
        Require(fixture.Bot.TriggeredSingleMarketSettings.ContainsKey("BTCUSDT") && ReferenceEquals(original, fixture.Bot.SingleMarketSettingSummaries.Single()) && Get<bool>(fixture.Bot, "_selectionIndeterminate"));
      });
      Check("sentinel first-seen time keeps age unavailable", () => AgeUnavailable(Constants.confMinDate));
      Check("future first-seen time keeps age unavailable", () => AgeUnavailable(DateTime.UtcNow.AddDays(1)));
      Check("nonfinite volume keeps activation unavailable", () =>
      {
        using var fixture = new Fixture(); fixture.Bot.MarketList = new List<string> { "BTCUSDT" }; fixture.RecentVolume(Double.NaN);
        Call(fixture.Bot, "ApplySingleMarketSettings");
        Require(fixture.Bot.TriggeredSingleMarketSettings.Count == 0 && Get<bool>(fixture.Bot, "_selectionIndeterminate"));
      });
      Check("missing off-trigger AND known true cannot remove active SMS", () =>
      {
        using var fixture = OffFixture("AND"); Call(fixture.Bot, "ApplySingleMarketSettings");
        Require(fixture.Bot.SingleMarketSettingSummaries.Count == 1 && fixture.Bot.TriggeredSingleMarketSettings.ContainsKey("BTCUSDT") && Get<bool>(fixture.Bot, "_selectionIndeterminate"));
      });
      Check("missing off-trigger OR known true can conclusively remove SMS", () =>
      {
        using var fixture = OffFixture("OR"); Call(fixture.Bot, "ApplySingleMarketSettings");
        Require(fixture.Bot.SingleMarketSettingSummaries.Count == 0 && fixture.Bot.TriggeredSingleMarketSettings.Count == 0 && !Get<bool>(fixture.Bot, "_selectionIndeterminate"));
      });
      Check("negated missing global reference cannot activate candidate", () =>
      {
        using var fixture = new Fixture(); var risk = fixture.Configuration.AnalyzerSettings.GlobalSettings.Single(setting => setting.SettingName == "Risk");
        risk.Triggers.Add(new Trigger { MarketTrendName = "DeletedTrend", Tag = "Missing" }); risk.TriggerConnection = "!Missing";
        object[] arguments = { fixture.Bot.ActiveSetting, new List<string>() }; Call(fixture.Bot, "CheckGlobalSettingsTriggers", arguments);
        Require(((GlobalSetting)arguments[0]).SettingName == "Default" && Get<bool>(fixture.Bot, "_selectionIndeterminate"));
      });
      Console.WriteLine("Configuration lifecycle checks passed: " + _passed + ".");
      return 0;
    }
    catch (Exception error) { Console.Error.WriteLine("Configuration lifecycle FAILED: " + error); return 1; }
  }

  private static Fixture OffFixture(string connection)
  {
    var fixture = new Fixture(); fixture.Bot.MarketList = new List<string> { "BTCUSDT" }; fixture.RecentVolume(1);
    fixture.Sms.OffTriggerConnection = connection;
    fixture.Sms.OffTriggers = new List<OffTrigger> { new OffTrigger { MarketTrendName = "DeletedTrend" }, new OffTrigger { HoursSinceTriggered = 1 } };
    fixture.Bot.SingleMarketSettingSummaries.Add(fixture.SmsSummary("BTCUSDT")); return fixture;
  }

  private static void AgeUnavailable(DateTime firstSeen)
  {
    using var fixture = new Fixture(); fixture.Bot.MarketList = new List<string> { "BTCUSDT" };
    fixture.Sms.Triggers = new List<Trigger> { new Trigger { Tag = "Age", AgeDaysLowerThan = 7 } };
    fixture.Bot.MarketInfos["BTCUSDT"] = new MarketInfo { Name = "BTCUSDT", FirstSeen = firstSeen };
    Call(fixture.Bot, "ApplySingleMarketSettings");
    Require(fixture.Bot.TriggeredSingleMarketSettings.Count == 0 && Get<bool>(fixture.Bot, "_selectionIndeterminate"));
  }

  private static ConfigurationPayload Payload(string setting) => new ConfigurationPayload(setting,
    new[] { "DEFAULT_sell_only_mode_enabled = false" }, new[] { "DEFAULT_DCA_enabled = true" }, new[] { "RSI_candle_period = 300" }, "destination");

  private static (string Name, Func<string, CancellationToken, Task<double>> Read)[] PriceAdapters(Fixture fixture) =>
    new (string, Func<string, CancellationToken, Task<double>>)[]
    {
      ("Binance", (quote, token) => Core.MarketAnalyzer.Binance.GetMainCurrencyPriceAsync(quote, fixture.Configuration, Log, token)),
      ("BinanceFutures", (quote, token) => Core.MarketAnalyzer.BinanceFutures.GetMainCurrencyPriceAsync(quote, fixture.Configuration, Log, token)),
      ("BinanceUS", (quote, token) => Core.MarketAnalyzer.BinanceUS.GetMainCurrencyPriceAsync(quote, fixture.Configuration, Log, token))
    };

  private static (string Name, Func<string, CancellationToken, Task<List<string>>> Read)[] MarketDataAdapters(Fixture fixture) =>
    new (string, Func<string, CancellationToken, Task<List<string>>>)[]
    {
      ("Binance", (quote, token) => Core.MarketAnalyzer.Binance.GetMarketDataAsync(quote, fixture.Bot.MarketInfos, fixture.Configuration, Log, token)),
      ("BinanceFutures", (quote, token) => Core.MarketAnalyzer.BinanceFutures.GetMarketDataAsync(quote, fixture.Bot.MarketInfos, fixture.Configuration, Log, token)),
      ("BinanceUS", (quote, token) => Core.MarketAnalyzer.BinanceUS.GetMarketDataAsync(quote, fixture.Bot.MarketInfos, fixture.Configuration, Log, token))
    };

  private static ConfigurationDeliveryResult Http(Fixture fixture, FakeHttp wire, Func<bool> canSend = null) =>
    SettingsAPI.SendPropertyLinesToAPI(new List<string> { "MARKET = USDT" }, new List<string> { "DEFAULT_DCA_enabled = true" },
      new List<string> { "RSI_candle_period = 300" }, fixture.Configuration, Log, wire.Client, canSend);

  private static ConfigurationDeliveryResult SomHttp(Fixture fixture, FakeHttp wire, bool enabled, Func<bool> canSend = null) =>
    GlobalSellOnlyModeAPI.Set(fixture.Configuration, enabled, Log, wire.Client, canSend);

  private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?')
    .Split('&', StringSplitOptions.RemoveEmptyEntries)
    .Select(part => part.Split('=', 2))
    .ToDictionary(parts => WebUtility.UrlDecode(parts[0]), parts => parts.Length > 1 ? WebUtility.UrlDecode(parts[1]) : "", StringComparer.Ordinal);

  private sealed class FakeHttp
  {
    private readonly Func<int, HttpResponseMessage> _response;
    public int Calls { get; private set; }
    public List<string> Bodies { get; } = new List<string>();
    public List<string> Hosts { get; } = new List<string>();
    public List<Uri> Uris { get; } = new List<Uri>();
    public List<HttpMethod> Methods { get; } = new List<HttpMethod>();
    public FakeHttp(Func<int, HttpResponseMessage> response) { _response = response; }
    public HttpClient Client() => new HttpClient(new Handler(this));
    private sealed class Handler : HttpMessageHandler
    {
      private readonly FakeHttp _owner;
      public Handler(FakeHttp owner) { _owner = owner; }
      protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
        if (request.RequestUri.Host != "pt-one.invalid" && request.RequestUri.Host != "pt-two.invalid")
          throw new InvalidOperationException("Unexpected fake target; no real network is permitted.");
        _owner.Calls++; _owner.Hosts.Add(request.RequestUri.Host);
        _owner.Uris.Add(request.RequestUri); _owner.Methods.Add(request.Method);
        _owner.Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        return _owner._response(_owner.Calls);
      }
    }
  }

  private sealed class Fixture : IDisposable
  {
    private readonly string _previousDirectory = Directory.GetCurrentDirectory();
    private readonly object _previousRaidPin = typeof(BinanceFuturesAnalyticsRuntime).GetField("_currentRaidAsOfUtc", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "PTMagic-ConfigurationLifecycle-" + Guid.NewGuid().ToString("N"));
    public string Checkpoint => Path.Combine(Root, Constants.PTMagicPathData, "ConfigurationDeliveryState.json");
    public string GuardCheckpoint => Path.Combine(Root, Constants.PTMagicPathData, "StartupSellOnlyModeState.json");
    public string DefaultPreset(string fileName) => Path.Combine(Root, Constants.PTMagicPathPresets, "Default", fileName);
    public Engine Bot { get; }
    public PTMagicConfiguration Configuration { get; }
    public SingleMarketSetting Sms => Configuration.AnalyzerSettings.SingleMarketSettings[0];
    public List<ConfigurationPayload> Sent { get; } = new List<ConfigurationPayload>();
    public List<bool> SomCalls { get; } = new List<bool>();
    public List<string> Events { get; } = new List<string>();
    public ConfigurationDeliveryResult Next { get; set; } = ConfigurationDeliveryResult.Applied();
    public ConfigurationDeliveryResult SomNext { get; set; } = ConfigurationDeliveryResult.Applied();
    public Action AfterConfigurationSend { get; set; }
    public Action<bool> BeforeSomSend { get; set; }

    public Fixture()
    {
      Directory.CreateDirectory(Root); Directory.CreateDirectory(Path.Combine(Root, Constants.PTMagicPathData));
      Directory.SetCurrentDirectory(Root); SetStatic(typeof(BinanceFuturesAnalyticsRuntime), "_currentRaidAsOfUtc", DateTimeOffset.UtcNow);
      var general = new GeneralSettings
      {
        Application = new Application
        {
          TestMode = false, Exchange = "BinanceFutures", StartupSellOnlyMode = true, MaxAnalysisAgeSeconds = 300,
          DecisionHistoryEnabled = false, ProfitTrailerMonitorURL = "http://pt-one.invalid/", ProfitTrailerLicense = "dummy-license-one", ProfitTrailerDefaultSettingName = "fixture",
          ProfitTrailerServerAPIToken = "dummy-token-one"
        },
        Monitor = new Core.Main.DataObjects.PTMagicData.Monitor(), Backup = new Backup { IsEnabled = false }, Telegram = new Core.Main.DataObjects.PTMagicData.Telegram { IsEnabled = false }
      };
      var analyzer = new AnalyzerSettings
      {
        MarketAnalyzer = new MarketAnalyzer { StoreDataMaxHours = 24, MarketTrends = new List<MarketTrend>() },
        GlobalSettings = new List<GlobalSetting>
        {
          new GlobalSetting
          {
            SettingName = "Default", PairsProperties = new Dictionary<string, object> { ["File"] = "pairs.properties" },
            DCAProperties = new Dictionary<string, object> { ["File"] = "dca.properties" }, IndicatorsProperties = new Dictionary<string, object> { ["File"] = "indicators.properties" }
          },
          new GlobalSetting { SettingName = "Risk", PairsProperties = new Dictionary<string, object> { ["DEFAULT_sell_only_mode_enabled"] = true } }
        },
        SingleMarketSettings = new List<SingleMarketSetting>
        {
          new SingleMarketSetting
          {
            SettingName = "Protection", Triggers = new List<Trigger> { new Trigger { Tag = "Volume", Min24hVolume = 10 } },
            PairsProperties = new Dictionary<string, object> { ["sell_only_mode_enabled"] = true }
          }
        }
      };
      File.WriteAllText(Path.Combine(Root, "settings.general.json"), JsonConvert.SerializeObject(new GeneralSettingsWrapper { GeneralSettings = general }));
      File.WriteAllText(Path.Combine(Root, "settings.analyzer.json"), JsonConvert.SerializeObject(new AnalyzerSettingsWrapper { AnalyzerSettings = analyzer }));
      string presets = Path.Combine(Root, Constants.PTMagicPathPresets, "Default"); Directory.CreateDirectory(presets);
      File.WriteAllLines(Path.Combine(presets, "pairs.properties"), new[] { "# fixture", "MARKET = USDT", "DEFAULT_sell_only_mode_enabled = false", "ETH_sell_only_mode_enabled = false", "LTC_sell_only_mode = false", "DEFAULT_sell_strategy = GAIN" });
      File.WriteAllLines(Path.Combine(presets, "dca.properties"), new[] { "# fixture", "DEFAULT_DCA_enabled = true" });
      File.WriteAllLines(Path.Combine(presets, "indicators.properties"), new[] { "# fixture", "RSI_candle_period = 300" });
      Configuration = new PTMagicConfiguration(Root);
      GlobalSetting active = Configuration.AnalyzerSettings.GlobalSettings[0];
      Bot = new Engine(Log,
        payload => { Sent.Add(payload); Events.Add("configuration"); AfterConfigurationSend?.Invoke(); return Next; },
        enabled => { BeforeSomSend?.Invoke(enabled); SomCalls.Add(enabled); Events.Add(enabled ? "som:true" : "som:false"); return SomNext; })
      {
        PTMagicConfiguration = Configuration, ActiveSetting = active, ActiveSettingName = active.SettingName,
        DefaultSettingName = active.SettingName, LastRuntime = DateTime.UtcNow,
        LastRuntimeSummary = new Summary { MainMarket = "USDT", CurrentGlobalSetting = active }
      };
      Call(Bot, "InitializeDeliveryTracking"); Set(Bot, "_raidAnalysisBuilt", true); CompileClean(); BeforeRaid();
    }

    public void CompileClean() => SettingsHandler.CompileProperties(Bot, Bot.ActiveSetting, DateTime.UtcNow);
    public void BeforeRaid()
    {
      Set(Bot, "_raidPriorSetting", Bot.ActiveSetting); Set(Bot, "_raidPriorSettingsChange", Bot.LastSettingsChange);
      Set(Bot, "_raidPriorSms", JsonConvert.DeserializeObject<List<SingleMarketSettingSummary>>(JsonConvert.SerializeObject(Bot.SingleMarketSettingSummaries)));
      Set(Bot, "_selectionIndeterminate", false); Set(Bot, "_normalDecisionCommittedThisRaid", false); Set(Bot, "_raidAnalysisBuilt", true);
      Bot.TriggeredSingleMarketSettings = new Dictionary<string, List<SingleMarketSetting>>(); Bot.SingleMarketSettingsCount = new Dictionary<string, int>();
      Bot.GlobalSettingWritten = false; Bot.SingleMarketSettingChanged = false;
    }
    public void SelectRisk()
    {
      Bot.ActiveSetting = Configuration.AnalyzerSettings.GlobalSettings.Single(setting => setting.SettingName == "Risk");
      Bot.ActiveSettingName = "Risk"; Bot.LastRuntimeSummary.CurrentGlobalSetting = Bot.ActiveSetting; CompileClean();
    }
    public void RecentVolume(double volume) => Set(Bot, "_recentMarketSnapshots", new Dictionary<string, Market>(StringComparer.OrdinalIgnoreCase)
      { ["BTCUSDT"] = new Market { Name = "BTCUSDT", Symbol = "BTCUSDT", Volume24h = volume, Price = 100 } });
    public SingleMarketSettingSummary SmsSummary(string market) => new SingleMarketSettingSummary
    {
      Market = market, SingleMarketSetting = Sms, ActivationDateTimeUTC = DateTime.UtcNow.AddHours(-3),
      TriggerSnapshot = new TriggerSnapshot { LastPrice = 100, Last24hVolume = 100, MatchedTriggersContent = new List<string> { "Protection: Volume" } }
    };
    public void AddSecondTarget()
    {
      Configuration.GeneralSettings.Application.ProfitTrailerMonitorURLXtra = "http://pt-two.invalid/";
      Configuration.GeneralSettings.Application.ProfitTrailerLicenseXtra = "dummy-license-two";
    }
    public void Dispose()
    {
      SetStatic(typeof(BinanceFuturesAnalyticsRuntime), "_currentRaidAsOfUtc", _previousRaidPin);
      Directory.SetCurrentDirectory(_previousDirectory); Directory.Delete(Root, true);
    }
  }

  private static object Call(object target, string name, params object[] arguments)
  {
    try { return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, arguments); }
    catch (TargetInvocationException error) when (error.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
  }
  private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
  private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
  private static void SetStatic(Type type, string name, object value) => type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, value);
  private static TException ExpectException<TException>(Action action) where TException : Exception
  {
    try { action(); } catch (TException error) { return error; }
    throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
  }
  private static InvalidOperationException ExpectPairsMarketError(Action action)
  {
    var error = ExpectException<InvalidOperationException>(action);
    Require(error.Message.Contains("PAIRS", StringComparison.OrdinalIgnoreCase)
      && error.Message.Contains("market", StringComparison.OrdinalIgnoreCase));
    return error;
  }
  private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
  private static void Check(string name, Action test) { test(); _passed++; Console.WriteLine("PASS " + name); }
}
