# DH.MySql 超时与连接池排查清单

## 当前库里最相关的代码面

- DH.MySql/SqlClient.cs
  - OpenAsync: 负责 TCP 建连超时，超时报错形如 连接 host:3306 超时(15000ms)
  - ReadPacketAsync: 负责接收 MySQL 响应包，超时报错形如 读取数据包超时(15s)
- DH.MySql/MySqlPool.cs
  - GetAsync: 负责从池中借连接、验活、Ping、剔除坏连接
  - 当连续拿到失效连接并超过重试次数时，会抛出 无法从连接池获取可用连接
- DH.MySql/MySqlConnectionStringBuilder.cs
  - 默认 ConnectionTimeout=15
  - 默认 CommandTimeout=30
- DH.MySql/MySqlConnection.cs
  - 打开连接时从连接池借连接
  - 默认网络读写超时跟随 ConnectionTimeout
- DH.MySql/MySqlCommand.cs 与 DH.MySql/MySqlDataReader.cs
  - 命令执行阶段临时切到 CommandTimeout
  - 读结果阶段再切回 ReadPhaseTimeout

## 当前日志的典型判定顺序

1. 先出现 慢SQL[72,662ms]。
2. 随后出现 读取数据包超时(15s)。
3. 同时出现大量 连接 host:3306 超时(15000ms)。
4. 最后出现 无法从连接池获取可用连接。

这类顺序通常说明：

- 数据库端或网络端已经明显变慢
- 应用侧默认 15 秒连接和读包超时开始集中触发
- 原有连接在等待结果，新连接又建不起来
- 连接池借连接失败是后果，不是首发原因

## 当前这条 72 秒慢 Insert 的专项判断

日志里的 SQL 不是纯 Insert，而是：

- Insert Into DL_HistoricalData_368(...)
- 后面紧跟 Select LAST_INSERT_ID()

同时堆栈落在：

- MySqlCommand.ExecuteScalarAsync
- MySqlDataReader.NextResultAsync
- SqlClient.ReadPacketAsync

这意味着当前要先区分三种可能：

1. 数据库端 Insert 本身真的执行了 72 秒。
2. Insert 已执行完，但驱动等待首个响应包时卡住。
3. Insert 已执行完，首个 OK 包也处理了，但在读取后续 Select LAST_INSERT_ID() 结果集时卡住。

在没有对照试验前，不要把这 72 秒直接定性成“数据库插入本身慢”。

## 建议的最小对照调测

先做最小 A/B，用同一条数据重复测试：

1. DH.MySql ExecuteNonQuery 只跑 Insert。
2. DH.MySql ExecuteScalar 跑 Insert;Select LAST_INSERT_ID()。
3. MySql.Data ExecuteNonQuery 只跑 Insert。
4. MySql.Data 执行等价的插入取回自增值流程。

记录：

- Open 耗时
- ExecuteNonQuery 耗时
- ExecuteScalar 耗时
- 是否稳定卡在 NextResultAsync / ReadPacketAsync

如果只有第 2 组明显异常，优先看当前库的多结果读取和超时语义。
如果第 1 组也慢，优先看数据库端写入性能。

## 如何区分是不是当前库本身缺陷

更像当前库直接参与的问题：

- 异常堆栈落在 NewLife.MySql.SqlClient.ReadPacketAsync
- 异常堆栈落在 NewLife.MySql.SqlClient.OpenAsync
- 异常堆栈落在 NewLife.MySql.MySqlPool.GetAsync
- Timeout 值明显来自当前库默认值 15 秒
- 相同环境下 MySql.Data 正常，而切换到当前库后开始稳定报错

不应当归因给当前库的现象：

- 反射扫描程序集失败
- Web 中间件、Swagger、StarAgent 等启动信息
- 业务 SQL 本身几十秒以上未返回

## 优先排查项

1. MySQL 实例是否在当时出现慢查询、连接数飙升、CPU 或 IO 打满。
2. 应用是否在短时间并发初始化大量实体表和缓存，造成连接风暴。
3. 连接字符串里的 ConnectionTimeout 和 CommandTimeout 是否过低。
4. 是否存在大批长事务、锁等待、批量插入或热点表竞争。
5. 应用侧是否有未及时关闭 DataReader 或连接，导致池中连接长期不归还。

## MySql.Data 对照判断

如果用户已经确认 MySql.Data 在同一数据库、同一网络、同一业务路径下持续正常，那么 Copilot 应优先给出下面的判断：

- 这不是“数据库整体不可用”的充分证据
- 这更像当前库的默认超时、连接池行为、协议读写处理或兼容性差异
- 连接池耗尽要按结果看，先回到最早出现的读包超时或建连超时

优先建议对比：

1. 两边的连接字符串是否完全一致，尤其是 ConnectionTimeout、CommandTimeout、Pooling 相关参数。
2. 当前库是否把 ConnectionTimeout 不仅用于建连，还直接作为网络读超时，而 MySql.Data 在同配置下未必采用相同语义。
3. 当前库的连接池验活与 Ping 逻辑，是否在大量失活连接时放大借连接成本。
4. 当前库是否启用了 MySql.Data 没有使用的特性，例如 Pipeline、UseServerPrepare 或不同的批量执行模式。
5. 异常是否总是先落在 SqlClient.ReadPacketAsync / OpenAsync，再扩散到 MySqlPool.GetAsync。
6. 对于 InsertAndGetIdentity 场景，异常是否集中落在 NextResultAsync，也就是读取后续结果集而不是发送 Insert 本身。

## 给 Copilot 的输出模板

可以直接按下面结构输出：

- 主故障：读包超时和建连超时，属于数据库访问链路问题
- 次生故障：连接池无可用连接，是前面超时堆积后的结果
- 与当前库直接相关的代码：SqlClient.OpenAsync、SqlClient.ReadPacketAsync、MySqlPool.GetAsync
- MySql.Data 对照：同环境下官方驱动正常，优先怀疑当前库差异，而不是先判定数据库整体故障
- 与当前库无关或弱相关的问题：程序集反射扫描异常、其他中间件日志
- 建议先看：数据库慢查询日志、连接数、线程数、RDS 监控、应用并发初始化行为