---
name: dh-mysql-timeout-pool-diagnosis
description: '分析 DH.MySql 或 NewLife.MySql 的数据库超时与连接池问题。用于处理 读取数据包超时、连接 3306 超时、慢SQL、无法从连接池获取可用连接、MySqlPool.GetAsync、SqlClient.ReadPacketAsync、MySqlConnection.OpenAsync、ConnectionTimeout、CommandTimeout、连接池枯竭、数据库请求堆积，以及 MySql.Data 正常但切换到当前库后报错 的对照排查。'
argument-hint: '提供日志文件、异常堆栈、慢 SQL 片段或连接字符串配置'
---

# DH MySql Timeout Pool Diagnosis

用于分析当前仓库 DH.MySql 直接负责的数据库故障，重点区分网络连接超时、读包超时、SQL 执行过慢，以及连接池被拖垮后的次生异常。

## 何时使用

- 日志出现 读取数据包超时
- 日志出现 连接 host:3306 超时
- 日志出现 无法从连接池获取可用连接
- 日志出现 MySqlPool.GetAsync、SqlClient.ReadPacketAsync、MySqlConnection.OpenAsync
- 日志出现大量慢 SQL，随后应用请求开始堆积
- 同一套数据库、同一业务代码下，MySql.Data 一直正常，但切换到 DH.MySql 后开始报超时或连接池错误

## 先做什么

1. 先按时间线找第一批数据库异常，不要先盯连接池报错。
2. 把异常分成四类：
   - 慢 SQL
   - 建连超时
   - 读包超时
   - 连接池获取失败
3. 判断谁最先出现，谁就是主故障；后面的通常是连锁反应。

## 当前仓库里的直接对应关系

- 连接 host:3306 超时：优先对应 SqlClient.OpenAsync
- 读取数据包超时：优先对应 SqlClient.ReadPacketAsync
- 无法从连接池获取可用连接：优先对应 MySqlPool.GetAsync
- 命令超时配置：优先检查 MySqlConnectionStringBuilder、MySqlCommand、MySqlConnection

## 判断规则

### 规则 1：慢 SQL 先出现

如果日志先出现几十秒的慢 SQL，然后才出现读包超时或连接池错误，优先判断为数据库响应过慢或请求量堆积，而不是连接池本身先坏掉。

### 规则 1.1：首个慢点是 Insert Into ... ; Select LAST_INSERT_ID()

如果第一条关键慢 SQL 是单条 Insert，且 SQL 尾部还跟着 Select LAST_INSERT_ID()，不要直接把 72 秒全部等同于数据库端 Insert 执行时间。

这类场景要拆成三段看：

- Insert 语句本身在数据库里执行是否真的慢
- Insert 之后的首个 OK 包是否及时返回
- Select LAST_INSERT_ID() 这个下一结果集的读取是否卡住

如果随后异常堆栈落在 MySqlDataReader.NextResultAsync 或 SqlClient.ReadPacketAsync，优先怀疑当前库在多结果读取、读包超时策略或协议处理上的差异，而不是只盯着表写入性能。

### 规则 2：读包超时成片出现

如果大量异常都是 读取数据包超时(15s)，说明连接已建立，但在等待 MySQL 响应包时超时。优先检查：

- SQL 是否过慢
- MySQL 服务器是否卡顿
- 网络抖动是否导致包长时间收不到
- Timeout 是否被设置得过短

### 规则 3：连接 3306 超时成片出现

如果大量异常都是 连接 host:3306 超时(15000ms)，说明问题更靠前，发生在 TCP 建连阶段。优先检查：

- 数据库实例负载
- 网络链路
- 连接风暴
- 应用是否突然并发创建大量新连接

### 规则 4：连接池报错通常是次生故障

如果 无法从连接池获取可用连接 出现在大批超时之后，优先判断为连接池里的连接都被慢请求或超时请求占住、失活或反复重建，而不是单独的连接池逻辑错误。

### 规则 5：MySql.Data 正常而当前库异常

如果同一数据库、同一网络、同一业务 SQL 下，MySql.Data 长期正常，而切换到当前库后稳定出现读包超时、建连超时或连接池错误，应优先判断为当前库相关问题，而不是先把结论落到数据库整体不可用。

优先检查：

- 当前库是否把 ConnectionTimeout 同时用于建连阶段和读包阶段，导致与 MySql.Data 在相同配置下语义不同
- 当前库的读包超时是否直接跟随 ConnectionTimeout
- 当前库连接池借还、验活、Ping 逻辑是否在高并发下放大了故障
- 当前库是否使用了与 MySql.Data 不同的连接字符串参数、并发模型或批量执行路径
- 切换驱动时业务是否同时改了 UseServerPrepare、Pipeline 或其他驱动专属参数

输出时应明确写出：

- 数据库环境并非完全不可用，因为 MySql.Data 可正常工作
- 当前故障更接近当前库的实现差异、默认配置差异或兼容性问题
- 需要把对比排查聚焦到超时策略、连接池策略和协议读写行为

## 调测步骤

遇到首个异常就是慢 Insert 时，优先按下面顺序调测：

1. 先做单线程对照，不要一上来压并发。
2. 用同一张表、同一条业务数据、同一套连接参数，分别测四组调用：
   - MySql.Data 执行单独 Insert
   - MySql.Data 执行 Insert 后单独 Select LAST_INSERT_ID()
   - DH.MySql 执行单独 Insert
   - DH.MySql 执行 Insert 加 Select LAST_INSERT_ID()
3. 记录每组的 Open、ExecuteNonQuery、ExecuteScalar 总耗时。
4. 如果只有 DH.MySql 的 Insert 加 Select LAST_INSERT_ID() 明显异常，优先检查多结果读取路径。
5. 如果两边单独 Insert 都慢，回到数据库端查锁、IO、触发器、索引维护和 auto_increment 热点。
6. 需要协议层证据时，在当前库连接字符串临时打开 TracePackets 复现一次，只用于排障。
7. 排除驱动专属变量干扰，确保 Pipeline、UseServerPrepare 等选项与对照实验保持一致或明确关闭。
8. 如果本地没有 MySQL 环境，优先编写或运行基于 MemoryStream 的协议单元测试，先验证多结果读取、超时切换、连接池逻辑，再等待线上窗口或项目现场复现。
9. 如果日志出现大量并发建连超时，优先检查当前库连接字符串里的 MaxPoolSize 和 MinPoolSize，避免连接池上限过大把局部慢点放大成连接风暴。

## 输出要求

分析结果至少要给出：

- 第一批数据库异常是什么
- 哪一类异常是主故障
- 哪一类异常是连锁反应
- 这个问题与当前库直接相关的代码入口
- 如果 MySql.Data 正常，是否应优先归因为当前库差异
- 如果首个慢点是 Insert 加 LAST_INSERT_ID，慢在数据库执行、首个 OK 包，还是下一结果集读取
- 优先排查项
- 最小缓解建议

## 当前仓库优先检查文件

- [连接池实现](./references/dh-mysql-timeout-checklist.md)
- [超时与连接池排查清单](./references/dh-mysql-timeout-checklist.md)

需要深入分析时，继续读取 [超时与连接池排查清单](./references/dh-mysql-timeout-checklist.md)。