using FastbootEnhance.Core.Terminal;
using Xunit;

namespace FastbootEnhance.Core.Tests
{
    public class TerminalCommandTests
    {
        [Theory]
        [InlineData("fastboot devices", TerminalTool.Fastboot, "devices")]
        [InlineData("  ADB.exe   devices -l ", TerminalTool.Adb, "devices -l")]
        [InlineData("fastboot -s ABC getvar all", TerminalTool.Fastboot, "-s ABC getvar all")]
        public void The_tool_and_its_arguments_are_kept_as_typed(string line, TerminalTool tool, string arguments)
        {
            TerminalCommand command = TerminalCommand.Parse(line);
            Assert.Equal(TerminalProblem.None, command.Problem);
            Assert.Equal(tool, command.Tool);
            Assert.Equal(arguments, command.Arguments);
            Assert.Equal(TerminalRisk.None, command.Risk);
        }

        [Theory]
        [InlineData("", TerminalProblem.Empty)]
        [InlineData("cmd /c del *", TerminalProblem.NotAdbOrFastboot)]
        [InlineData("adb shell", TerminalProblem.InteractiveShell)]
        [InlineData("adb -s X shell -t", TerminalProblem.InteractiveShell)]
        [InlineData("fastboot flash boot \"C:\\my files\\boot.img", TerminalProblem.UnclosedQuote)]
        public void Lines_that_cannot_run_say_why(string line, TerminalProblem problem)
        {
            Assert.Equal(problem, TerminalCommand.Parse(line).Problem);
        }

        [Theory]
        [InlineData("fastboot flash abl_a abl.img", TerminalRisk.CriticalPartition, "abl_a")]
        [InlineData("fastboot -s X --slot all flash modem NON-HLOS.bin", TerminalRisk.CriticalPartition, "modem")]
        [InlineData("fastboot erase persist", TerminalRisk.CriticalPartition, "persist")]
        [InlineData("fastboot erase userdata", TerminalRisk.WipesData, null)]
        [InlineData("fastboot format:ext4 metadata", TerminalRisk.WipesData, null)]
        [InlineData("fastboot -w", TerminalRisk.WipesData, null)]
        [InlineData("fastboot flashing unlock", TerminalRisk.WipesData, null)]
        [InlineData("fastboot flashing lock", TerminalRisk.LocksBootloader, null)]
        [InlineData("fastboot oem lock", TerminalRisk.LocksBootloader, null)]
        [InlineData("adb shell su -c \"dd if=/sdcard/boot.img of=/dev/block/by-name/boot_a\"", TerminalRisk.RawBlockWrite, null)]
        [InlineData("fastboot flash boot boot.img", TerminalRisk.None, null)]
        [InlineData("fastboot flash userdata userdata.img", TerminalRisk.None, null)]
        [InlineData("adb shell dd if=/dev/block/by-name/boot_a of=/sdcard/boot.img", TerminalRisk.None, null)]
        public void Risky_commands_are_recognised(string line, TerminalRisk risk, string target)
        {
            TerminalCommand command = TerminalCommand.Parse(line);
            Assert.Equal(TerminalProblem.None, command.Problem);
            Assert.Equal(risk, command.Risk);
            Assert.Equal(target, command.RiskTarget);
        }

        [Fact]
        public void Quotes_group_words_and_are_removed()
        {
            TerminalCommand command = TerminalCommand.Parse("fastboot flash boot \"C:\\my files\\boot.img\"");
            Assert.Equal(new[] { "flash", "boot", "C:\\my files\\boot.img" }, command.Words);
            Assert.Equal("flash boot \"C:\\my files\\boot.img\"", command.Arguments);
        }
    }
}
