using System.Linq;
using FastbootEnhance.Core.Usb;
using Xunit;

namespace FastbootEnhance.Core.Tests
{
    public class UsbDiagnosisTests
    {
        [Fact]
        public void A_phone_without_a_driver_is_found_by_vendor_or_name()
        {
            var findings = UsbDiagnosis.Diagnose(new[]
            {
                new PnpDevice("Android", @"USB\VID_0E8D&PID_201C\0123456789", 28),
                new PnpDevice("Unknown Device", @"USB\VID_2717&PID_FF48\abc", 28),
                new PnpDevice("Fastboot", @"USB\VID_1234&PID_5678\x", 28),
                new PnpDevice("USB Keyboard", @"USB\VID_046D&PID_C31C\x", 28),
            });
            Assert.Equal(3, findings.Count);
            Assert.All(findings, f => Assert.Equal(UsbFindingKind.MissingDriver, f.Kind));
            Assert.Equal("0E8D:201C", findings.First(f => f.Device.Name == "Android").UsbId);
        }

        [Fact]
        public void A_broken_driver_is_not_a_missing_one()
        {
            var finding = UsbDiagnosis.Diagnose(new[]
            {
                new PnpDevice("Android Bootloader Interface", @"USB\VID_18D1&PID_4EE0\x", 10),
            }).Single();
            Assert.Equal(UsbFindingKind.DriverProblem, finding.Kind);
        }

        [Theory]
        [InlineData(@"USB\VID_05C6&PID_9008\5&1", UsbFindingKind.QualcommEdl)]
        [InlineData(@"USB\VID_0E8D&PID_0003\5&1", UsbFindingKind.MediaTekDownload)]
        [InlineData(@"USB\VID_0e8d&pid_2000\5&1", UsbFindingKind.MediaTekDownload)]
        [InlineData(@"USB\VID_1782&PID_4D00\5&1", UsbFindingKind.UnisocDownload)]
        [InlineData(@"USB\VID_04E8&PID_685D\5&1", UsbFindingKind.SamsungDownload)]
        public void Download_modes_are_recognised_with_or_without_a_driver(string id, UsbFindingKind kind)
        {
            Assert.Equal(kind, UsbDiagnosis.Diagnose(new[] { new PnpDevice("x", id, 0) }).Single().Kind);
            Assert.Equal(kind, UsbDiagnosis.Diagnose(new[] { new PnpDevice("x", id, 28) }).Single().Kind);
        }

        [Fact]
        public void Working_interfaces_are_reported_after_the_problems()
        {
            var findings = UsbDiagnosis.Diagnose(new[]
            {
                new PnpDevice("Android Composite ADB Interface", @"USB\VID_18D1&PID_4EE7&MI_01\6", 0),
                new PnpDevice("Android Bootloader Interface", @"USB\VID_18D1&PID_4EE0\x", 0),
                new PnpDevice("Android", @"USB\VID_18D1&PID_D001\x", 28),
                new PnpDevice("USB Mass Storage Device", @"USB\VID_0781&PID_5567\x", 0),
            });
            Assert.Equal(new[] { UsbFindingKind.MissingDriver, UsbFindingKind.FastbootReady, UsbFindingKind.AdbReady },
                findings.Select(f => f.Kind));
        }

        [Fact]
        public void The_stand_in_list_is_read_line_by_line()
        {
            var devices = UsbDiagnosis.ParseList("Android|USB\\VID_0E8D&PID_201C\\1|28|\r\nbroken line\nPreloader|USB\\VID_0E8D&PID_2000\\2|0|Ports\n");
            Assert.Equal(2, devices.Count);
            Assert.Equal(28, devices[0].ErrorCode);
            Assert.Equal("Ports", devices[1].PnpClass);
        }
    }
}
