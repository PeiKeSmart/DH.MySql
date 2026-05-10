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
    [DisplayName("读取阶段优先使用命令超时而不是连接超时")]
    public void ReadPhaseTimeout_ShouldPreferCommandTimeout()
    {
        var conn = new MySqlConnection("server=localhost;database=test;connection timeout=15;command timeout=66");
        using var cmd = new MySqlCommand(conn, "select 1");

        var reader = new MySqlDataReader
        {
            Command = cmd,
            OriginalTimeout = conn.ConnectionTimeout,
            CommandPhaseTimeout = cmd.CommandTimeout,
            ReadPhaseTimeout = cmd.CommandTimeout > 0 ? cmd.CommandTimeout : conn.ConnectionTimeout
        };

        Assert.Equal(66, reader.CommandPhaseTimeout);
        Assert.Equal(66, reader.ReadPhaseTimeout);
        Assert.NotEqual(conn.ConnectionTimeout, reader.ReadPhaseTimeout);
    }

    [Fact]
    [DisplayName("打开连接后的默认读超时优先使用命令超时")]
    public void ConnectionDefaultClientTimeout_ShouldPreferCommandTimeout()
    {
        var setting = new MySqlConnectionStringBuilder("server=localhost;database=test;connection timeout=15;command timeout=66");

        Assert.Equal(66, MySqlConnection.GetDefaultClientTimeout(setting));
    }
}