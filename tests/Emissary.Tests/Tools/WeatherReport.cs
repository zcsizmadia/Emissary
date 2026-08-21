namespace Emissary.Tests.Tools;

/// <summary>A weather report for one city.</summary>
/// <param name="City">The city the report covers.</param>
/// <param name="TemperatureC">Temperature in the requested unit.</param>
/// <param name="Unit">The temperature unit.</param>
/// <param name="Station">The reporting station, if known.</param>
[ClaudeSchema]
public sealed partial record WeatherReport(
    string City,
    double TemperatureC,
    TemperatureUnit Unit = TemperatureUnit.Celsius,
    Address? Station = null);
