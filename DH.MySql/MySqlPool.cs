using System.Collections.Concurrent;
using System.Diagnostics;
using NewLife.Collections;
using NewLife.Log;
using NewLife.MySql.Common;

namespace NewLife.MySql;

/// <summary>连接池。每个连接字符串一个连接池，管理多个可重用连接</summary>
public class MySqlPool : ObjectPool<SqlClient>
{
    private readonly SemaphoreSlim _returnSignal = new(0, Int32.MaxValue);

    /// <summary>设置</summary>
    public MySqlConnectionStringBuilder? Setting { get; set; }

    /// <summary>当前连接池是否正在清理。清理期间归还的连接会直接销毁。</summary>
    public Boolean BeingCleared { get; private set; }

    private IDictionary<String, String>? _Variables;
    private DateTime _nextTime;
    private String? _serverVersion;
    private String? _serverVersionComment;
    private DatabaseType _databaseType;
    private DateTime _serverInfoExpire;
    /// <summary>服务器变量。缓存10分钟后自动过期</summary>
    public IDictionary<String, String>? Variables
    {
        get
        {
            if (_Variables == null || _nextTime < DateTime.UtcNow) return null;

            return _Variables;
        }
        set
        {
            _Variables = value;
            _nextTime = DateTime.UtcNow.AddMinutes(10);
        }
    }

    /// <summary>尝试获取缓存的服务器信息</summary>
    /// <param name="serverVersion">服务器版本</param>
    /// <param name="serverVersionComment">版本注释</param>
    /// <param name="databaseType">数据库类型</param>
    /// <returns>是否命中缓存</returns>
    public Boolean TryGetServerInfo(out String? serverVersion, out String? serverVersionComment, out DatabaseType databaseType)
    {
        if (_serverVersion.IsNullOrEmpty() || _serverInfoExpire < DateTime.UtcNow)
        {
            serverVersion = null;
            serverVersionComment = null;
            databaseType = DatabaseType.MySQL;
            return false;
        }

        serverVersion = _serverVersion;
        serverVersionComment = _serverVersionComment;
        databaseType = _databaseType;
        return true;
    }

    /// <summary>缓存服务器信息</summary>
    /// <param name="serverVersion">服务器版本</param>
    /// <param name="serverVersionComment">版本注释</param>
    /// <param name="databaseType">数据库类型</param>
    public void SetServerInfo(String serverVersion, String? serverVersionComment, DatabaseType databaseType)
    {
        _serverVersion = serverVersion;
        _serverVersionComment = serverVersionComment;
        _databaseType = databaseType;
        _serverInfoExpire = DateTime.UtcNow.AddMinutes(10);
    }

    /// <summary>创建连接</summary>
    protected override SqlClient OnCreate()
    {
        var set = Setting ?? throw new ArgumentNullException(nameof(Setting));
        var connStr = set.ConnectionString;
        if (connStr.IsNullOrEmpty()) throw new InvalidOperationException("连接字符串不能为空");

        return new SqlClient(set);
    }

    /// <summary>异步获取连接。剔除无效连接</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>可用的数据库连接</returns>
    public new async Task<SqlClient> GetAsync(CancellationToken cancellationToken = default)
    {
        var set = Setting ?? throw new ArgumentNullException(nameof(Setting));
        var waitMs = set.ConnectionTimeout * 1000;
        if (waitMs <= 0) waitMs = 15000;

        var retryCount = 0;
        var start = Stopwatch.StartNew();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SqlClient client;
            try
            {
                client = base.Get();
            }
            catch (Exception ex) when (IsPoolExhausted(ex))
            {
                var remain = waitMs - (Int32)start.ElapsedMilliseconds;
                if (remain <= 0 || !await _returnSignal.WaitAsync(remain, cancellationToken).ConfigureAwait(false))
                    throw new TimeoutException($"从连接池获取连接超时({waitMs}ms)");

                continue;
            }

            // 新创建的连接尚未打开，直接返回由调用方打开
            if (client.Welcome == null) return client;

            if (await ValidateAsync(client, set, cancellationToken).ConfigureAwait(false)) return client;

            client.TryDispose();
            if (retryCount++ > 10) throw new InvalidOperationException("无法从连接池获取可用连接");
        }
    }

    /// <summary>归还连接到连接池，并唤醒等待中的请求</summary>
    /// <param name="value">连接</param>
    public new void Return(SqlClient value)
    {
        if (BeingCleared)
        {
            value.TryDispose();
            return;
        }

        value.LastReturnedTime = DateTime.UtcNow;
        base.Return(value);
        _returnSignal.Release();
    }

    /// <summary>清理连接池中的空闲连接，并标记后续归还连接直接销毁。</summary>
    public new void Clear()
    {
        BeingCleared = true;

        while (FreeCount > 0)
        {
            SqlClient? client = null;
            try
            {
                client = base.Get();
            }
            catch
            {
                break;
            }

            client.TryDispose();
        }
    }

    private static async Task<Boolean> ValidateAsync(SqlClient client, MySqlConnectionStringBuilder set, CancellationToken cancellationToken)
    {
        if (!client.Active) return false;

        var previousTimeout = client.Timeout;
        var connectionTimeout = set.ConnectionTimeout;
        if (connectionTimeout > 0) client.Timeout = connectionTimeout;
        try
        {
            // 先清理可能残留的协议数据，再按连接超时做一次验活。
            if (!client.Reset()) return false;

            if (ShouldPing(client.LastReturnedTime, DateTime.UtcNow, set.PoolPingWindow) && !await client.PingAsync(cancellationToken).ConfigureAwait(false)) return false;
            if (set.ConnectionReset)
                await client.ResetConnectionAsync(cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (previousTimeout > 0) client.Timeout = previousTimeout;
        }
    }

    internal static Boolean ShouldPing(DateTime lastReturnedTime, DateTime now, Int32 poolPingWindow)
    {
        if (lastReturnedTime <= DateTime.MinValue) return true;

        if (poolPingWindow <= 0) return true;

        return now - lastReturnedTime > TimeSpan.FromSeconds(poolPingWindow);
    }

    private static Boolean IsPoolExhausted(Exception ex) => ex.Message.Contains("申请失败") || ex.Message.Contains("最大值");
}

/// <summary>连接池管理。根据连接字符串，换取对应连接池</summary>
public class MySqlPoolManager
{
    private readonly ConcurrentDictionary<String, MySqlPool> _pools = new();
    /// <summary>获取连接池。连接字符串相同时共用连接池</summary>
    /// <param name="setting"></param>
    /// <returns></returns>
    public MySqlPool GetPool(MySqlConnectionStringBuilder setting) => _pools.GetOrAdd(setting.ConnectionString, k => CreatePool(setting));

    /// <summary>清理指定连接字符串对应的连接池</summary>
    public void ClearPool(MySqlConnectionStringBuilder setting)
    {
        if (_pools.TryRemove(setting.ConnectionString, out var pool)) pool.Clear();
    }

    /// <summary>清理所有连接池</summary>
    public void ClearAllPools()
    {
        foreach (var item in _pools.ToArray())
        {
            if (_pools.TryRemove(item.Key, out var pool)) pool.Clear();
        }
    }

    /// <summary>创建连接池</summary>
    /// <param name="setting"></param>
    /// <returns></returns>
    protected virtual MySqlPool CreatePool(MySqlConnectionStringBuilder setting)
    {
        using var span = DefaultTracer.Instance?.NewSpan("db:mysql:CreatePool", setting.ConnectionString);

        var pool = new MySqlPool
        {
            //Name = Name + "Pool",
            //Instance = this,
            Setting = setting,
            Min = Math.Max(0, setting.MinimumPoolSize),
            Max = setting.MaximumPoolSize > 0 ? setting.MaximumPoolSize : 100,
            IdleTime = 30,
            AllIdleTime = 300,
            //Log = ClientLog,

            //Callback = OnCreate,
        };

        if (pool.Min > pool.Max) pool.Min = pool.Max;

        return pool;
    }
}