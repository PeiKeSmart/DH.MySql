using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using NewLife;
using NewLife.MySql;

namespace UnitTest;

/// <summary>连接池共享测试</summary>
[Collection(TestCollections.ReadOnly)]
public class MySqlPoolTests
{
    private static String _ConnStr = DALTests.GetConnStr();

    [Fact]
    [DisplayName("多个连接共享同一个连接池")]
    public void WhenMultipleConnectionsOpenedThenSamePoolUsed()
    {
        var factory = MySqlClientFactory.Instance;
        var poolManager = factory.PoolManager;

        using var conn1 = new MySqlConnection(_ConnStr);
        using var conn2 = new MySqlConnection(_ConnStr);

        conn1.Open();
        conn2.Open();

        // 通过相同连接字符串获取的池应该是同一实例
        var pool1 = poolManager.GetPool(conn1.Setting);
        var pool2 = poolManager.GetPool(conn2.Setting);
        Assert.Same(pool1, pool2);

        conn1.Close();
        conn2.Close();
    }

    //[Fact]
    //[DisplayName("不同工厂实例共享同一个池管理器")]
    //public void WhenDifferentFactoryInstancesThenSamePoolManager()
    //{
    //    // 默认所有工厂实例共享同一个静态池管理器
    //    var factory1 = new MySqlClientFactory();
    //    var factory2 = new MySqlClientFactory();

    //    Assert.Same(factory1.PoolManager, factory2.PoolManager);
    //}

    [Fact]
    [DisplayName("连接关闭后客户端归还到池")]
    public void WhenConnectionClosedThenClientReturnedToPool()
    {
        using var conn = new MySqlConnection(_ConnStr);
        conn.Open();

        Assert.Equal(ConnectionState.Open, conn.State);
        Assert.NotNull(conn.Client);

        var pool = conn.Factory.PoolManager.GetPool(conn.Setting);
        var totalBefore = pool.Total;

        conn.Close();

        Assert.Equal(ConnectionState.Closed, conn.State);
        Assert.Null(conn.Client);
    }

    //[Fact]
    //[DisplayName("连接字符串大小写不敏感时共享连接池")]
    //public void WhenConnectionStringCaseInsensitiveThenSamePoolUsed()
    //{
    //    var factory = MySqlClientFactory.Instance;
    //    var poolManager = factory.PoolManager;

    //    var setting1 = new MySqlConnectionStringBuilder(_ConnStr);
    //    var setting2 = new MySqlConnectionStringBuilder(_ConnStr.ToUpper());

    //    // OrdinalIgnoreCase 比较
    //    var pool1 = poolManager.GetPool(setting1);
    //    var pool2 = poolManager.GetPool(setting2);

    //    // 连接字符串大小写不同时也应该共享池
    //    Assert.Same(pool1, pool2);
    //}

    [Fact]
    [DisplayName("OnCreate在Setting为null时抛出异常")]
    public void WhenSettingNullThenOnCreateThrows()
    {
        var pool = new MySqlPool();

        Assert.Throws<ArgumentNullException>(() => pool.Get());
    }

    [Fact]
    [DisplayName("OnCreate使用Setting创建SqlClient")]
    public void WhenSettingProvidedThenClientHasSameSetting()
    {
        var setting = new MySqlConnectionStringBuilder
        {
            Server = "localhost",
            Port = 3306,
            UserID = "root",
            Password = "test",
        };
        var pool = new MySqlPool { Setting = setting };

        var client = pool.Get();

        Assert.NotNull(client);
        Assert.Same(setting, client.Setting);

        client.Dispose();
    }

    [Fact]
    [DisplayName("Variables缓存设置后可读取")]
    public void WhenVariablesSetThenCanGet()
    {
        var pool = new MySqlPool();
        var vars = new Dictionary<String, String> { ["max_allowed_packet"] = "67108864" };

        pool.Variables = vars;

        Assert.NotNull(pool.Variables);
        Assert.Same(vars, pool.Variables);
    }

    [Fact]
    [DisplayName("Variables未设置时返回null")]
    public void WhenVariablesNotSetThenReturnsNull()
    {
        var pool = new MySqlPool();

        Assert.Null(pool.Variables);
    }

    [Fact]
    [DisplayName("Variables设置null后返回null")]
    public void WhenVariablesSetNullThenReturnsNull()
    {
        var pool = new MySqlPool();
        pool.Variables = new Dictionary<String, String> { ["key"] = "value" };
        pool.Variables = null;

        // 设置 null 后内部值为 null，getter 返回 null
        Assert.Null(pool.Variables);
    }

    [Fact]
    [DisplayName("CreatePool设置默认连接池参数")]
    public void WhenCreatePoolThenDefaultSettingsApplied()
    {
        var manager = new MySqlPoolManager();
        var setting = new MySqlConnectionStringBuilder
        {
            Server = "localhost",
            Port = 3306,
            UserID = "root",
            Password = "test",
        };

        var pool = manager.GetPool(setting);

        Assert.NotNull(pool);
        Assert.Same(setting, pool.Setting);
        Assert.Equal(0, pool.Min);
        Assert.Equal(100, pool.Max);
        Assert.Equal(30, pool.IdleTime);
        Assert.Equal(300, pool.AllIdleTime);
    }

    [Fact]
    [DisplayName("CreatePool尊重连接字符串中的池大小设置")]
    public void WhenCreatePoolThenUseConnectionStringPoolSettings()
    {
        var manager = new MySqlPoolManager();
        var setting = new MySqlConnectionStringBuilder
        {
            Server = "localhost",
            Port = 3306,
            UserID = "root",
            Password = "test",
            MinimumPoolSize = 2,
            MaximumPoolSize = 5,
        };

        var pool = manager.GetPool(setting);

        Assert.Equal(2, pool.Min);
        Assert.Equal(5, pool.Max);
    }

    [Fact]
    [DisplayName("相同连接字符串返回同一个连接池")]
    public void WhenSameConnectionStringThenSamePoolReturned()
    {
        var manager = new MySqlPoolManager();
        var connStr = "Server=localhost;Port=3306;UserID=root;Password=test";
        var setting1 = new MySqlConnectionStringBuilder(connStr);
        var setting2 = new MySqlConnectionStringBuilder(connStr);

        var pool1 = manager.GetPool(setting1);
        var pool2 = manager.GetPool(setting2);

        Assert.Same(pool1, pool2);
    }

    [Fact]
    [DisplayName("不同连接字符串返回不同连接池")]
    public void WhenDifferentConnectionStringThenDifferentPoolReturned()
    {
        var manager = new MySqlPoolManager();
        var setting1 = new MySqlConnectionStringBuilder
        {
            Server = "host1",
            Port = 3306,
            UserID = "root",
            Password = "test",
        };
        var setting2 = new MySqlConnectionStringBuilder
        {
            Server = "host2",
            Port = 3306,
            UserID = "root",
            Password = "test",
        };

        var pool1 = manager.GetPool(setting1);
        var pool2 = manager.GetPool(setting2);

        Assert.NotSame(pool1, pool2);
    }

    [Fact]
    [DisplayName("Get获取新连接时Welcome为null")]
    public void WhenGetNewClientThenWelcomeIsNull()
    {
        var setting = new MySqlConnectionStringBuilder
        {
            Server = "localhost",
            Port = 3306,
            UserID = "root",
            Password = "test",
        };
        var pool = new MySqlPool { Setting = setting };

        var client = pool.Get();

        Assert.NotNull(client);
        Assert.Null(client.Welcome);
        Assert.False(client.Active);

        client.Dispose();
    }

    [Fact]
    [DisplayName("连接池满时等待归还后可继续获取连接")]
    public async Task WhenPoolExhaustedThenWaitUntilConnectionReturned()
    {
        var setting = new MySqlConnectionStringBuilder
        {
            Server = "localhost",
            Port = 3306,
            UserID = "root",
            Password = "test",
            ConnectionTimeout = 1,
            MaximumPoolSize = 1,
        };
        var pool = new MySqlPool { Setting = setting, Min = 0, Max = 1 };

        var client1 = await pool.GetAsync().ConfigureAwait(false);
        var watch = Stopwatch.StartNew();

        var task = pool.GetAsync();
        await Task.Delay(150).ConfigureAwait(false);
        pool.Return(client1);

        var client2 = await task.ConfigureAwait(false);

        Assert.True(watch.ElapsedMilliseconds >= 100);
        Assert.Same(client1, client2);

        pool.Return(client2);
    }

    [Fact]
    [DisplayName("连接池满且超时后抛出超时异常")]
    public async Task WhenPoolExhaustedThenTimeout()
    {
        var setting = new MySqlConnectionStringBuilder
        {
            Server = "localhost",
            Port = 3306,
            UserID = "root",
            Password = "test",
            ConnectionTimeout = 1,
            MaximumPoolSize = 1,
        };
        var pool = new MySqlPool { Setting = setting, Min = 0, Max = 1 };

        var client = await pool.GetAsync().ConfigureAwait(false);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => pool.GetAsync()).ConfigureAwait(false);

        Assert.Contains("从连接池获取连接超时", ex.Message);
        pool.Return(client);
    }

    [Fact]
    [DisplayName("ClearPool后再次获取返回新的连接池实例")]
    public void WhenClearPoolThenManagerCreatesNewPool()
    {
        var manager = new MySqlPoolManager();
        var setting = new MySqlConnectionStringBuilder("Server=localhost;Port=3306;UserID=root;Password=test");

        var pool1 = manager.GetPool(setting);
        manager.ClearPool(setting);
        var pool2 = manager.GetPool(setting);

        Assert.NotSame(pool1, pool2);
        Assert.True(pool1.BeingCleared);
    }

    [Fact]
    [DisplayName("ClearAllPools后再次获取返回新的连接池实例")]
    public void WhenClearAllPoolsThenManagerCreatesNewPools()
    {
        var manager = new MySqlPoolManager();
        var setting1 = new MySqlConnectionStringBuilder("Server=host1;Port=3306;UserID=root;Password=test");
        var setting2 = new MySqlConnectionStringBuilder("Server=host2;Port=3306;UserID=root;Password=test");

        var pool1 = manager.GetPool(setting1);
        var pool2 = manager.GetPool(setting2);

        manager.ClearAllPools();

        var pool3 = manager.GetPool(setting1);
        var pool4 = manager.GetPool(setting2);

        Assert.NotSame(pool1, pool3);
        Assert.NotSame(pool2, pool4);
        Assert.True(pool1.BeingCleared);
        Assert.True(pool2.BeingCleared);
    }

    [Fact]
    [DisplayName("刚归还到连接池的连接可跳过Ping验活")]
    public void WhenConnectionReturnedRecentlyThenSkipPing()
    {
        var now = DateTime.UtcNow;

        Assert.False(MySqlPool.ShouldPing(now.AddSeconds(-1), now, 3));
    }

    [Fact]
    [DisplayName("闲置较久的连接需要执行Ping验活")]
    public void WhenConnectionIdleTooLongThenPingRequired()
    {
        var now = DateTime.UtcNow;

        Assert.True(MySqlPool.ShouldPing(now.AddSeconds(-10), now, 3));
        Assert.True(MySqlPool.ShouldPing(DateTime.MinValue, now, 3));
    }

    [Fact]
    [DisplayName("PoolPingWindow为0时每次都执行Ping验活")]
    public void WhenPoolPingWindowIsZeroThenAlwaysPing()
    {
        var now = DateTime.UtcNow;

        Assert.True(MySqlPool.ShouldPing(now.AddSeconds(-1), now, 0));
    }
}
