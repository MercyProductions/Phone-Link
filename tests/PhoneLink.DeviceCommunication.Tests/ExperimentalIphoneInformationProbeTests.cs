using System.Text;
using System.Xml.Linq;
using PhoneLink.DeviceCommunication.Diagnostics;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class ExperimentalIphoneInformationProbeTests
{
    [Fact]
    public void DeveloperModeQueryIsOnlyOneAllowlistedRead()
    {
        var bytes = ExperimentalIphoneInformationProbe.BuildReadRequest("DeveloperModeStatus", "com.apple.security.mac.amfi");
        var dictionary = ExperimentalUsbMuxProbe.ParseDictionary(bytes);
        Assert.Equal("GetValue", dictionary["Request"].Value);
        Assert.Equal("DeveloperModeStatus", dictionary["Key"].Value);
        Assert.Equal("com.apple.security.mac.amfi", dictionary["Domain"].Value);
        Assert.Equal(4, dictionary.Count);
        Assert.Throws<ArgumentException>(() => ExperimentalIphoneInformationProbe.BuildReadRequest("DeveloperModeStatus"));
        Assert.Throws<ArgumentException>(() => ExperimentalIphoneInformationProbe.BuildReadRequest("DeveloperModeStatus", "com.apple.mobile.battery"));
        Assert.Throws<ArgumentException>(() => ExperimentalIphoneInformationProbe.BuildReadRequest("EnableDeveloperMode", "com.apple.security.mac.amfi"));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void DeveloperFlagIsReportedOnlyForActualBoolean(string element, bool expected)
    {
        var result = ExperimentalIphoneInformationProbe.ParseDeveloperModeResponse(Response(new XElement(element)), DateTimeOffset.UtcNow);
        Assert.Equal(expected, result.ReportedEnabled);
        Assert.Equal(IphoneInformationFieldStatus.Available, result.Field.Status);
    }

    [Theory]
    [InlineData("integer", "1")]
    [InlineData("string", "true")]
    [InlineData("true", "unexpected")]
    public void InvalidDeveloperFlagNeverBecomesEnabledOrDisabled(string kind, string value)
    {
        var result = ExperimentalIphoneInformationProbe.ParseDeveloperModeResponse(Response(new XElement(kind, value)), DateTimeOffset.UtcNow);
        Assert.Null(result.ReportedEnabled);
        Assert.Equal(IphoneInformationFieldStatus.UnexpectedValue, result.Field.Status);
    }

    [Theory]
    [InlineData("GetProhibited", "GetProhibited")]
    [InlineData("Private error with identifiers", "OtherDeviceError")]
    public void DeniedDeveloperFlagStaysUnknownWithoutRawErrorEcho(string error, string expected)
    {
        var bytes = Encoding.UTF8.GetBytes(new XDocument(new XElement("plist", new XElement("dict",
            new XElement("key", "Request"), new XElement("string", "GetValue"),
            new XElement("key", "Error"), new XElement("string", error)))).ToString());
        var result = ExperimentalIphoneInformationProbe.ParseDeveloperModeResponse(bytes, DateTimeOffset.UtcNow);
        Assert.Null(result.ReportedEnabled);
        Assert.Equal(IphoneInformationFieldStatus.DeviceReportedError, result.Field.Status);
        Assert.Equal(expected, result.Field.DeviceErrorCode);
    }
    [Theory]
    [InlineData("UniqueDeviceID")]
    [InlineData("SerialNumber")]
    [InlineData("DeviceName")]
    [InlineData("")]
    public void RefusesUnapprovedInformationKeys(string key) =>
        Assert.Throws<ArgumentException>(() => ExperimentalIphoneInformationProbe.BuildReadRequest(key));

    [Fact]
    public void InformationRequestsAreNarrowGetValueQueries()
    {
        var body = Encoding.UTF8.GetString(ExperimentalIphoneInformationProbe.BuildReadRequest("ProductVersion"));
        Assert.Contains("GetValue", body);
        Assert.Contains("ProductVersion", body);
        Assert.DoesNotContain("SetValue", body);
        Assert.DoesNotContain("StartSession", body);
        Assert.Throws<ArgumentException>(() => ExperimentalIphoneInformationProbe.BuildReadRequest("BatteryCurrentCapacity", "unapproved-domain"));
        Assert.Contains("com.apple.mobile.battery", Encoding.UTF8.GetString(
            ExperimentalIphoneInformationProbe.BuildReadRequest("BatteryCurrentCapacity", "com.apple.mobile.battery")));
    }

    [Theory]
    [InlineData("iPhone99,1")]
    [InlineData("26.0")]
    [InlineData("23A100")]
    public void ParsesBoundedBasicInformation(string value)
    {
        Assert.Equal(value, ExperimentalIphoneInformationProbe.ParseReadResponse(Response(new XElement("string", value))));
    }

    [Fact]
    public void ProtectedOrUnexpectedValuesRemainUnknown()
    {
        var denied = Encoding.UTF8.GetBytes("<plist><dict><key>Request</key><string>GetValue</string><key>Error</key><string>PasswordProtected</string></dict></plist>");
        Assert.Null(ExperimentalIphoneInformationProbe.ParseReadResponse(denied));
        Assert.Null(ExperimentalIphoneInformationProbe.ParseReadResponse(Response(new XElement("string", "unexpected\ncontent"))));
        Assert.Null(ExperimentalIphoneInformationProbe.ParseReadResponse(Response(new XElement("string", new string('a', 65)))));
        Assert.Null(ExperimentalIphoneInformationProbe.ParseReadResponse(Response(new XElement("dict"))));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("100", 100)]
    [InlineData("101", null)]
    [InlineData("-1", null)]
    [InlineData("unknown", null)]
    public void BatteryLevelIsNotBatteryHealthAndMustBeInRange(string value, int? expected)
    {
        Assert.Equal(expected, ExperimentalIphoneInformationProbe.ParseBatteryPercentage(new XElement("integer", value)));
    }

    [Fact]
    public void ParsesOnlyActualPlistBooleanFlags()
    {
        Assert.True(ExperimentalIphoneInformationProbe.ParseFlag(new XElement("true")));
        Assert.False(ExperimentalIphoneInformationProbe.ParseFlag(new XElement("false")));
        Assert.Null(ExperimentalIphoneInformationProbe.ParseFlag(new XElement("string", "true")));
        Assert.Null(ExperimentalIphoneInformationProbe.ParseFlag(null));
    }

    private static byte[] Response(XElement value) => Encoding.UTF8.GetBytes(
        new XElement("plist", new XElement("dict", new XElement("key", "Request"), new XElement("string", "GetValue"),
            new XElement("key", "Value"), value)).ToString());

    [Theory]
    [InlineData("PasswordProtected")]
    [InlineData("GetProhibited")]
    [InlineData("MissingKey")]
    [InlineData("MissingValue")]
    [InlineData("SessionInactive")]
    [InlineData("InvalidHostID")]
    public void RetainsOnlyAllowlistedDeviceErrorNames(string code)
    {
        var payload = Encoding.UTF8.GetBytes($"<plist><dict><key>Request</key><string>GetValue</string><key>Error</key><string>{code}</string><key>Value</key><integer>90</integer></dict></plist>");
        var field = ExperimentalIphoneInformationProbe.ParseFieldResponse(payload, "BatteryCurrentCapacity", "com.apple.mobile.battery");
        Assert.Equal(IphoneInformationFieldStatus.DeviceReportedError, field.Status);
        Assert.Equal(code, field.DeviceErrorCode);
        Assert.Null(ExperimentalIphoneInformationProbe.ParseReadResponse(payload));
    }

    [Fact]
    public void DoesNotPublishUnrecognizedDeviceErrorContent()
    {
        var payload = Encoding.UTF8.GetBytes("<plist><dict><key>Request</key><string>GetValue</string><key>Error</key><string>private serial device-name</string></dict></plist>");
        var field = ExperimentalIphoneInformationProbe.ParseFieldResponse(payload, "ProductType");
        Assert.Equal("OtherDeviceError", field.DeviceErrorCode);
        Assert.DoesNotContain("private", field.ToString());
    }

    [Fact]
    public void DistinguishesOmittedValueFromInvalidValueAndReportedError()
    {
        var omitted = Encoding.UTF8.GetBytes("<plist><dict><key>Request</key><string>GetValue</string></dict></plist>");
        var invalid = Response(new XElement("integer", "101"));
        Assert.Equal(IphoneInformationFieldStatus.MissingValue,
            ExperimentalIphoneInformationProbe.ParseFieldResponse(omitted, "ProductType").Status);
        Assert.Equal(IphoneInformationFieldStatus.UnexpectedValue,
            ExperimentalIphoneInformationProbe.ParseFieldResponse(invalid, "BatteryCurrentCapacity", "com.apple.mobile.battery").Status);
        Assert.Equal(IphoneInformationFieldStatus.Available,
            ExperimentalIphoneInformationProbe.ParseFieldResponse(Response(new XElement("false")), "BatteryIsCharging", "com.apple.mobile.battery").Status);
        Assert.Throws<ArgumentException>(() => ExperimentalIphoneInformationProbe.ParseFieldResponse(omitted, "SerialNumber"));
    }

    [Fact]
    public void FailureResultAndIncorrectRequestDoNotBecomeSuccessfulInformation()
    {
        var failed = Encoding.UTF8.GetBytes("<plist><dict><key>Request</key><string>GetValue</string><key>Result</key><string>Failure</string><key>Value</key><string>26.0</string></dict></plist>");
        Assert.Equal(IphoneInformationFieldStatus.DeviceReportedError,
            ExperimentalIphoneInformationProbe.ParseFieldResponse(failed, "ProductVersion").Status);
        Assert.Null(ExperimentalIphoneInformationProbe.ParseReadResponse(failed));
        var unrelated = Encoding.UTF8.GetBytes("<plist><dict><key>Request</key><string>SetValue</string><key>Value</key><string>26.0</string></dict></plist>");
        Assert.Throws<InvalidDataException>(() => ExperimentalIphoneInformationProbe.ParseFieldResponse(unrelated, "ProductVersion"));
    }
}
