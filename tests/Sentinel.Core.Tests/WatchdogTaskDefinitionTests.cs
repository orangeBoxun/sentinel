using Sentinel.Core.Vault;
using Xunit;

namespace Sentinel.Core.Tests;

/// <summary>
/// 「看门狗任务被停用了吗」—— 这是封印保险箱的判据之一，所以两个方向都要钉住：
/// 认得出明确的停用，同时**看不懂就当作没停用**（误封是不可逆的）。
/// </summary>
public class WatchdogTaskDefinitionTests
{
    private const string Disabled = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Sentinel 看门狗</Description>
          </RegistrationInfo>
          <Triggers>
            <TimeTrigger><Repetition><Interval>PT1M</Interval></Repetition></TimeTrigger>
          </Triggers>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <Enabled>false</Enabled>
          </Settings>
        </Task>
        """;

    private const string Running = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <Enabled>true</Enabled>
          </Settings>
        </Task>
        """;

    [Fact]
    public void Reads_the_disabled_state() =>
        Assert.True(WatchdogTaskDefinition.IsDisabled(Disabled));

    [Fact]
    public void A_running_task_is_not_disabled() =>
        Assert.False(WatchdogTaskDefinition.IsDisabled(Running));

    [Fact]
    public void Enabled_absent_means_enabled()
    {
        // 任务计划程序只在停用时写这一项：没有它 = 默认启用，不是"看不懂"。
        Assert.False(WatchdogTaskDefinition.IsDisabled(
            """<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Settings /></Task>"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("不是 XML")]
    [InlineData("<Task><Settings>")]   // 截断的
    public void Unreadable_definitions_are_not_treated_as_disabled(string? xml) =>
        Assert.False(WatchdogTaskDefinition.IsDisabled(xml));
}
