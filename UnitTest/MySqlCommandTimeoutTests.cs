using System.ComponentModel;
using NewLife.MySql;

namespace UnitTest;

[Collection(TestCollections.InMemory)]
public class MySqlCommandTimeoutTests
{
    [Fact]
    [DisplayName("命令默认继承连接字符串中的超时设置")]
    public void CommandTimeout_ShouldInheritFromConnectionSetting()
    {
        var conn = new MySqlConnection("server=localhost;database=test;command timeout=66");

        using var cmd = new MySqlCommand(conn, "select 1");

        Assert.Equal(66, cmd.CommandTimeout);
    }

    [Fact]
    [DisplayName("读取阶段超时优先使用命令超时")]
    public void ReadPhaseTimeout_ShouldPreferCommandTimeout()
    {
        var conn = new MySqlConnection("server=localhost;database=test;connection timeout=15;command timeout=66");

        using var cmd = new MySqlCommand(conn, "select 1")
        {
            CommandTimeout = 37
        };

        var reader = new MySqlDataReader
        {
            Command = cmd,
            CommandPhaseTimeout = cmd.CommandTimeout,
            ReadPhaseTimeout = cmd.CommandTimeout > 0 ? cmd.CommandTimeout : conn.Setting.CommandTimeout
        };

        Assert.Equal(37, reader.CommandPhaseTimeout);
        Assert.Equal(37, reader.ReadPhaseTimeout);
    }

    [Fact]
    [DisplayName("命令阶段超时在命令未显式设置时回退到连接字符串CommandTimeout")]
    public void CommandPhaseTimeout_ShouldFallbackToConnectionCommandTimeout()
    {
        var conn = new MySqlConnection("server=localhost;database=test;connection timeout=15;command timeout=66");

        var timeout = MySqlCommand.GetEffectiveCommandTimeout(0, conn, 15);

        Assert.Equal(66, timeout);
    }

    [Fact]
    [DisplayName("命令阶段超时不应错误退回到连接超时")]
    public void CommandPhaseTimeout_ShouldNotFallbackToConnectionTimeout()
    {
        var conn = new MySqlConnection("server=localhost;database=test;connection timeout=15;command timeout=30");

        var timeout = MySqlCommand.GetEffectiveCommandTimeout(0, conn, 15);

        Assert.Equal(30, timeout);
    }

    [Fact]
    [DisplayName("读包超时按单次读取独立计算")]
    public void ReadTimeout_ShouldUseFullWindowEveryRead()
    {
        Assert.Equal(15_000, SqlClient.GetReadTimeoutMilliseconds(15));
        Assert.Equal(15_000, SqlClient.GetReadTimeoutMilliseconds(15));
    }

    [Fact]
    [DisplayName("读包超时为0时不限制等待")]
    public void ReadTimeout_ShouldBeInfiniteWhenTimeoutIsZero()
    {
        Assert.Equal(global::System.Threading.Timeout.Infinite, SqlClient.GetReadTimeoutMilliseconds(0));
    }
}