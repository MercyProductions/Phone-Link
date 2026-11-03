using System.Text;
using System.Xml.Linq;
using PhoneLink.DeviceCommunication.Diagnostics;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class ExperimentalIphoneInformationProbeTests
{
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
}
