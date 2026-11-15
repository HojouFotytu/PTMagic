// Only connection settings are stubbed. The test links the actual production exporter source.
namespace Core.Main
{
  public sealed class PTMagicConfiguration
  {
    public General GeneralSettings { get; } = new General();
    public sealed class General { public ApplicationSettings Application { get; } = new ApplicationSettings(); }
    public sealed class ApplicationSettings
    {
      public string ProfitTrailerMonitorURL { get; set; } = "https://pt.invalid/instance/";
      public string ProfitTrailerServerAPIToken { get; set; } = "secret-token+&?";
      public string InstanceName { get; set; } = "TestInstance";
    }
  }
}
