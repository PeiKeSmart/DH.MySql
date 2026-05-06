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

## 输出要求

分析结果至少要给出：

- 第一批数据库异常是什么
- 哪一类异常是主故障
- 哪一类异常是连锁反应
- 这个问题与当前库直接相关的代码入口
- 如果 MySql.Data 正常，是否应优先归因为当前库差异
- 优先排查项
- 最小缓解建议

## 当前仓库优先检查文件

- [连接池实现](./references/dh-mysql-timeout-checklist.md)
- [超时与连接池排查清单](./references/dh-mysql-timeout-checklist.md)

需要深入分析时，继续读取 [超时与连接池排查清单](./references/dh-mysql-timeout-checklist.md)。