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
    [DisplayName("累计超时预算会按剩余时间递减")]
    public void TimeoutBudget_ShouldUseRemainingWindow()
    {
        var client = new SqlClient
        {
            Timeout = 15
        };

        client.RestartTimeoutBudget(15);
        client.ConsumeTimeoutBudget(5_000);

        Assert.Equal(10_000, client.GetRemainingTimeoutMilliseconds(15));
    }

    [Fact]
    [DisplayName("切换超时值时会重建累计超时预算")]
    public void TimeoutBudget_ShouldRestartWhenTimeoutChanges()
    {
        var client = new SqlClient
        {
            Timeout = 15
        };

        client.RestartTimeoutBudget(15);
        client.ConsumeTimeoutBudget(5_000);

        Assert.Equal(30_000, client.GetRemainingTimeoutMilliseconds(30));
    }
}