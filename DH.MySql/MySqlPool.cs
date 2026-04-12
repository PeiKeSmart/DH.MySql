using System.Collections.Concurrent;
using System.Diagnostics;
using NewLife.Collections;
using NewLife.Log;

namespace NewLife.MySql;

/// <summary>连接池。每个连接字符串一个连接池，管理多个可重用连接</summary>
public class MySqlPool : ObjectPool<SqlClient>
{
    /// <summary>设置</summary>
    public MySqlConnectionStringBuilder? Setting { get; set; }

    private readonly SemaphoreSlim _returnSignal = new(0, Int32.MaxValue);

    private IDictionary<String, String>? _Variables;
    private DateTime _nextTime;
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

    /// <summary>创建连接</summary>
    protected override SqlClient OnCreate()
    {
        var set = Setting ?? throw new ArgumentNullException(nameof(Setting));
        var connStr = set.ConnectionString;
        if (connStr.IsNullOrEmpty()) throw new InvalidOperationException("连接字符串不能为空");

        return new SqlClient(set);
    }

    /// <summary>异步创建并打开连接。只有打开成功后才会计入借出集合</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>已打开的连接</returns>
    protected override async Task<SqlClient?> OnCreateAsync(CancellationToken cancellationToken = default)
    {
        var client = OnCreate();
        await client.OpenAsync(cancellationToken).ConfigureAwait(false);
        client.LastActive = DateTime.Now;
        return client;
    }

    /// <summary>借出前检查连接是否仍然可用</summary>
    /// <param name="client">连接</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否可用</returns>
    protected override async Task<Boolean> OnGetAsync(SqlClient client, CancellationToken cancellationToken = default)
    {
        if (client == null) return false;
        if (client.Welcome == null) return false;

        if (!client.Active || !client.Reset()) return false;

        if (client.LastActive > DateTime.MinValue && client.LastActive.AddSeconds(60) < DateTime.Now)
            return await PingWithTimeoutAsync(client, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>归还前检查连接是否可继续复用</summary>
    /// <param name="client">连接</param>
    /// <returns>是否进入空闲池</returns>
    protected override Boolean OnReturn(SqlClient client) => client != null && client.Active && client.Welcome != null;

    /// <summary>异步获取连接。剔除无效连接</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>可用的数据库连接</returns>
    public override async Task<SqlClient> GetAsync(CancellationToken cancellationToken = default)
    {
        var timeout = (Setting?.ConnectionTimeout ?? 15) * 1000;
        if (timeout <= 0) timeout = 15000;

        var sw = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await base.GetAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsPoolExhausted(ex))
            {
                var remaining = timeout - (Int32)sw.ElapsedMilliseconds;
                if (remaining <= 0)
                    throw new TimeoutException($"获取连接池连接超时({timeout}ms)，最大连接数{Max}", ex);

                var signaled = await _returnSignal.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
                if (!signaled)
                    throw new TimeoutException($"获取连接池连接超时({timeout}ms)，最大连接数{Max}", ex);
            }
        }
    }

    /// <summary>归还连接并唤醒一个等待中的请求</summary>
    /// <param name="client">连接</param>
    /// <returns>是否成功归还到空闲池</returns>
    public override Boolean Return(SqlClient client)
    {
        var rs = base.Return(client);
        _returnSignal.Release();
        return rs;
    }

    private Boolean IsPoolExhausted(Exception ex)
    {
        if (Max <= 0) return false;

        var msg = ex.Message;
        return !msg.IsNullOrEmpty() && msg.Contains("达到或超过最大值", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>带短超时的异步 Ping 检测</summary>
    private static async Task<Boolean> PingWithTimeoutAsync(SqlClient client, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        return await client.PingAsync(cts.Token).ConfigureAwait(false);
    }
}

/// <summary>连接池管理。根据连接字符串，换取对应连接池</summary>
public class MySqlPoolManager
{
    private readonly ConcurrentDictionary<String, MySqlPool> _pools = new();
    /// <summary>获取连接池。连接字符串相同时共用连接池</summary>
    /// <param name="setting"></param>
    /// <returns></returns>
    public MySqlPool GetPool(MySqlConnectionStringBuilder setting) => _pools.GetOrAdd(setting.ConnectionString, k => CreatePool(setting));

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
            Min = Math.Max(0, setting.MinPoolSize),
            Max = Math.Max(1, setting.MaxPoolSize),
            IdleTime = 30,
            AllIdleTime = 300,
            //Log = ClientLog,

            //Callback = OnCreate,
        };

        return pool;
    }
}