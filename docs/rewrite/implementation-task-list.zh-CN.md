# Orleans + C++ Sub2API 重写实现任务清单

> 目标：为小型执行模型提供一个可以反复读取、逐项实现、逐项验证的任务队列。
> 本文描述的是当前工作树 `/root/apitf` 中 `platform`（C# Orleans 控制面）、
> `gateway`（C++ 网关）相对只读参考项目 `sub2api` 的剩余工作，不要求复制
> Sub2API 的 API、数据库、ID、密钥、状态值或迁移历史。

## 0. 使用规则

### 0.1 证据基线

- 审计基线：Platform 文档提交 `c8a59d7`（生产代码截至 `651a786`）、Gateway
  `04ec18c`、Sub2API 参考 `origin/main@fbfdcef`；三者都必须在开始任务时重新检查
  当前提交和工作树。
- 当前审计是静态复核；`platform/docs/rewrite/verification.md` 中的历史通过结果
  不能当作本次运行结果。历史数据库门禁为 `292 passed / 2 failed`，普通
  `294/294` 会因缺少数据库而让 33 个测试提前返回。
- 总库存为 65 个域：2 `implemented`、56 `partial`、5 `skeleton`、2 `missing`。
  只有“契约 + 持久化状态机 + 自动测试 + 当前源码运行证据”齐全时才可改为
  `implemented`。

### 0.2 小模型循环协议

每一轮只领取一个未完成任务，并严格执行以下步骤：

1. 读取任务的“证据、范围、依赖”，检查相关文件是否已经被其他改动覆盖。
2. 先写失败测试或可复现检查，再实现最小完整闭环；不要只添加 DTO、路由或假数据。
3. 运行任务列出的命令；命令失败时保留日志并修复，不能把失败改写为“跳过”。
4. 检查数据库/缓存/日志中没有泄露密钥、重复扣费、重复对象或越权数据。
5. 在本文件对应任务的状态、提交/文件、验证日期和剩余风险中留下证据；若仍有
   未完成分支，保持 `PARTIAL`，不要宣称完成。
6. 一个任务完成后再领取其依赖已满足的下一个任务。跨仓库改 Cap'n Proto 时，
   `platform/contracts/capnp` 与 `gateway/proto` 必须同一提交更新并通过生成物比较。

状态值：`TODO`（未开始）、`DOING`（当前领取）、`BLOCKED`（同一外部阻塞连续三轮）、
`PARTIAL`（已有实现但验收未闭合）、`DONE`（本任务验收全部通过）。

### 0.3 禁止事项

- 不引入 Redis、CDC、Debezium、Sub2API 数据/旧密钥/旧 ID 或兼容性路由。
- 不以手工 Admin 确认、内存字典、Mock 200、静态路由或“测试提前返回”作为完成证据。
- 不在 Gateway 重复实现账务；PostgreSQL 是金额权威，Orleans 只协调并投影。
- 不把不确定的 Provider 结果当作无费用；转发后、部分输出、断流、超时和对象存储
  不确定性必须保留 hold 并进入 reconciliation。

## 1. 优先级与依赖图

执行顺序：`GATE-01 -> GATE-02 -> P0-01..P0-09 -> P1-01..P1-09 -> P2-01..P2-04 -> REL-01..REL-05`。
同一阶段中，满足依赖的任务可以并行，但每个任务仍要单独验收。

| 阶段 | 任务 | 目的 |
| --- | --- | --- |
| 门禁修复 | GATE-01, GATE-02 | 让测试结果可信，防止小模型在假绿上继续堆功能 |
| P0 产品闭环 | P0-01..P0-09 | 账务、调度、Provider、媒体和安全的生产必需路径 |
| P1 功能补齐 | P1-01..P1-09 | 新 Provider、搜索、语音、身份、商业和运维功能 |
| P2 体验补齐 | P2-01..P2-04 | 公告、导出、UI 与保留策略 |
| 发布门禁 | REL-01..REL-05 | HA、备份、长压测、CI 和跨仓库证据 |

## 2. 任务卡

### GATE-01 修复数据库测试假绿与两项确定性失败

- **状态**：`DONE`；**优先级**：P0；**依赖**：无；**负责人范围**：`platform/test/Host.Tests`、测试启动脚本/CI。
- **证据**：`platform/docs/rewrite/verification.md:31-45`；失败测试为
  `ContentPolicyPropagationTests.ConcurrentWorkersSerializeClaimsAndPublishEachRevisionOnce`
  与 `MediaOperationStoreTests.BatchListIsOwnerScopedAndReturnsDurableOperations`。
- **实现步骤**：
  1. 为 Host 测试创建唯一数据库 schema/前缀和清理策略；禁止共享测试残留。
  2. 将 Content Policy 断言改为“每个 revision 恰好一次、总 claim 数等于 revision 数、
     worker 分配可为 2+0/1+1”，不要错误要求每个 worker 恰好一行。
  3. 修复 MediaOperation 测试使用真实生成的 operation/item ID 清理，并验证外键顺序。
  4. 缺少 `GREENFIELD_SCHEMA_CONNECTION`、PostgreSQL、Garnet 时明确失败或显式 skip，
     输出原因和 skip 数；普通无数据库运行不能冒充集成通过。
- **验收**：`dotnet test platform/test/Host.Tests/Host.Tests.csproj`（真实 schema）；
  `dotnet test platform/ScalaAPI.Platform.slnx`；连续运行两次结果一致，
  0 failed，且没有 early-return 假通过。
- **完成证据**：测试日志含真实迁移/数据库连接、0 failed；记录命令、提交和时间。

### GATE-02 建立当前工作树的可重复基线

- **状态**：`DONE`；**优先级**：P0；**依赖**：GATE-01；**范围**：`platform/scripts`、`platform/.github`、`gateway/.github`、文档。
- **实现步骤**：固定 Platform/Gateway/Sub2API commit，执行 Release build、C++ CTest、
  C# tests、Cap'n Proto digest/generation、Web build、迁移双跑和依赖扫描；所有子命令
  非零必须向顶层传播；记录外部工具不可用时的明确环境错误。
- **验收**：同一 checkout 可从空卷重跑，失败子任务让顶层退出非零；不能引用旧的
  `BenchmarkDotNet.Artifacts` 或历史 smoke 名称作为当前结果。

### P0-01 将调度并发与账户健康变成持久、分布式状态

- **状态**：`DONE`（步骤 1-3）；`PARTIAL`（步骤 4 quota/tier 归入 P1-04）；**优先级**：P0；**依赖**：GATE-01；**范围**：
  `platform/src/Grains/{AccountGrain,UserGrain,SchedulerGrain}.cs`、Grains.Interfaces、SQL migration、Host/Grains tests。
- **当前缺口**：~~`AccountGrain`/`UserGrain` 的 `_activeSlots` 是 activation-memory 字典~~（已迁移到 PostgreSQL）；
  ~~`AccountGrain.ReportSuccess()` 是 no-op~~（已实现健康更新）。Scheduler 仍无 provider tier/quota/freshness/cooldown（P1-04）。
- **实现步骤**：
  1. ✅ 定义可序列化 account/user concurrency window、lease owner、expires_at、generation、
     success/failure/cooldown 状态；SQL 是跨 Silo 争用权威，Grain 只缓存带版本投影。
  2. ✅ 用 `SELECT ... FOR UPDATE`/唯一 token 实现 acquire/release/reclaim；进程崩溃、重复
     release、旧 generation 必须幂等且不能超卖。
  3. ✅ 实现 `ReportSuccess` 清除短期错误/cooldown，429/401/5xx 按策略设置退避和永久禁用。
  4. ⏳ 新增 provider quota/tier snapshot（归入 P1-04）。
- **验收**：两 Silo 并发 acquire 不能超过上限 ✅；重启后 lease 可 reclaim ✅；成功报告恢复
  可调度 ✅；过期 quota 不会绕过限制（P1-04）；Grain/Host 测试覆盖 ✅。

### P0-02 修复 UsageGrain 误导实现并统一用量权威

- **状态**：`DONE`；**优先级**：P0；**依赖**：P0-01；**范围**：
  `platform/src/Grains/UsageGrain.cs`、`Grains.Interfaces`、`Data/Accounting`、调用方和测试。
- **ADR**：选择方案 A——删除死 Grain/接口。`UsageGrain` 零生产调用者；`Record` 只改内存计数，
  `Flush` 清除非持久化。实际结算已由 `RequestLeaseStore.CompleteAsync()` 在单事务中完成
  （`usage_events` + `usage_logs` + `accounting.AppendEffectAsync` + `usage_outbox`），
  以 `usage:{leaseToken}` 为幂等 key。保留 Grain 会成为第二个账务权威。
- **实现**：删除 `UsageGrain.cs` 和 `IUsageGrain.cs`（含 `UsageEventData`）；更新 data-mapping 文档。
- **验收**：源码搜索无 `IUsageGrain`/`UsageGrain` 引用 ✅；重复/崩溃/重放由 RequestLeaseStore
  幂等保证 ✅；账本/hold/usage/outbox 一致性已由现有事务覆盖 ✅。

### P0-03 完成价格/响应模型/媒体计费契约

- **状态**：`DONE`；**优先级**：P0；**依赖**：P0-01；**范围**：
  `platform/src/{Data,Platform.Host,Grains.Interfaces}`、`gateway/src/dispatch`、migrations、协议 schema。
- **实现步骤**：
  1. ✅ 在 lease/usage 中分离 requested/mapped/upstream/observed model，保存 price source/checksum。
  2. ✅ 定义 search/audio/character/long-context 单位；全部 NUMERIC(20,8) 或 integer，无 double。
  3. ✅ 实现 response-model mismatch 保守计费：更贵不升级、无价不零元、不绕过 Admin price。
  4. ✅ 媒体结算使用 lease 真实 PricingVersion（不再硬编码 “v1”）。
- **验收**：9 个新测试覆盖各单位 golden + mismatch 场景 ✅；价格版本可追溯 ✅；
  媒体不再固定 v1 ✅。Cap'n Proto schema 扩展留待后续（需特定编译器版本）。

### P0-04 补齐 Provider fidelity：代理凭证、TLS fingerprint、实时请求头

- **状态**：`PARTIAL`；**优先级**：P0；**依赖**：P0-03；**范围**：
  `platform/src/Platform.Host/Services/CapnpRpcHostedService.cs`、`gateway/src/forwarder`、
  `gateway/src/server/gateway_handler.cpp`、Cap'n Proto schema、Platform/Gateway tests。
- **当前缺口**：Platform 仅发送 ProxyUrl/TLS bool；Gateway HTTP/realtime 只 `set_proxy`，
  不解密代理用户名密码、不应用 TLS fingerprint，realtime 目标请求头绕过 HTTP validator。
- **实现步骤**：定义不泄露 secret 的 proxy credential envelope；Platform 解密仅在出站边界，
  Gateway 不记录明文；为 HTTP 与 WebSocket 共用 header validator；实现并验证 JA3/JA4/cipher
  profile（不支持时明确 fail closed）；支持轮换、失效和错误分类。
- **验收**：Provider mock/本地 TLS server 验证代理认证、TLS server-name/profile、realtime
  headers；日志/metrics/Cap'n Proto dump 无 secret；错误不可重试时保留正确账务状态。

### P0-05 完成 OpenAI/Anthropic/Gemini/Responses 的剩余故障与转换矩阵

- **状态**：`PARTIAL`；**优先级**：P0；**依赖**：P0-03、P0-04；**范围**：`gateway/src/protocol`、
  `gateway/test/fixtures`、Provider.Mock、Platform smoke。
- **实现步骤**：补工具调用、多模态、多候选、identifier、finish reason、未知字段策略；
  完成 Responses mutation lifecycle、跨协议 fixtures、live-like provider faults；保持
  429/5xx 才可 release/failover，断流/超时/ malformed/partial output 保留 unknown-charge。
- **验收**：JSON/SSE golden、错误 envelope、usage-before-EOF、client cancellation、
  non-SSE 2xx 拒绝、重放和多 Gateway exactly-once 全通过；C++ CTest 和源构建 smoke 为当前运行结果。

### P0-06 完成媒体/视频生命周期与长 HA worker 验证

- **状态**：`PARTIAL`；**优先级**：P0；**依赖**：P0-03、GATE-01；**范围**：
  `platform/src/Platform.Host/Services/MediaOperationHostedService.cs`、媒体 store/migrations、Gateway media routes、stack smoke。
- **实现步骤**：实现 video cancel/delete、price/unit settlement、restart/restore、retention/orphan
  cleanup、HEAD mismatch recopy、archive/item claim；修复测试 FK；跑 3600 秒重复 due-work、
  两 Silo rejoin、MinIO/PostgreSQL partition。
- **验收**：零 duplicate final object、零 duplicate debit、无 premature deletion；所有不确定
  对象状态可重试/可 reconciliation；完整 gate 清理 containers/volumes/networks。

### P0-07 将内容策略变成跨进程可证明的安全边界

- **状态**：`PARTIAL`；**优先级**：P0；**依赖**：GATE-01、P0-05；**范围**：
  content-policy service/migrations/Garnet、Gateway streaming、Admin/User Web。
- **实现步骤**：修复 GATE-01 后补 separate-process ordering/reclaim、credential rotation/redaction、
  p95/unavailable budget、response/stream buffer bound、User error UX 和权限矩阵；OpenAI/local/
  external classifier 均 fail closed，策略版本不因 TTL 丢失。
- **验收**：请求阻断不创建 lease；响应阻断不向客户端写入；stream 按 event 边界阻断并保留
  unknown-charge；两进程每 revision 一次发布；audit/alert/metric 无内容和 secret 标签。

### P0-08 完成账务/配额/操作员恢复的长期边界

- **状态**：`PARTIAL`；**优先级**：P0；**依赖**：P0-01..P0-07；**范围**：accounting、reconciliation、Admin endpoints、Gateway retry。
- **实现步骤**：覆盖每个 crash hook（dispatch、forward、provider completion、settlement commit、
  outbox claim/ack、Gateway replacement）和 Garnet loss/TLS outage；扩展 subscription quota、
  payment/refund/media effects；所有 settle/release 使用同一 idempotency fingerprint。
- **验收**：每个请求最终且仅最终为一次 debit、证据支持的 release 或一个 open incident；
  多 Gateway/Silo、缓存重建、操作员 replay 后不重复扣费。

### P0-09 实现 Grok/xAI 专用 Provider 垂直切片

- **状态**：`TODO`；**优先级**：P0；**依赖**：P0-03..P0-05；**范围**：`gateway/src`、Platform grains/services、Provider.Mock、migrations、Web。
- **实现步骤**：冻结 provider capability matrix；实现模型目录/版本、至少一个文本 JSON/SSE
  transform、OAuth/account refresh/revoke、quota/tier probe、image/video native routes、
  provider error mapping、price snapshots、Admin account UI 和 feature flag。
- **验收**：无 generic Bearer 冒充；真实 source-owned fixture 覆盖成功、429、401/revoked、
  malformed、timeout、disconnect；quota 过期不调度；账务和审计证据完整。

### P1-01 实现 Web Search/X Search

- **状态**：`TODO`；**优先级**：P1；**依赖**：P0-03、P0-09（可先用独立 mock）；**范围**：Gateway capability/schema、Platform adapter/mock、Admin/User routes/UI。
- **实现步骤**：替换 generic `/alpha/search` 为版本化能力；定义 bounded query/domain/recency、
  source/result/citation schema、provider failure/account penalty、per-search unit/pricing、
  redaction/audit、idempotency；补 Web/X provider fixture 和模型/权限配置。
- **验收**：成功、空结果、429/5xx、超时、部分结果的公开错误和账务状态确定；不得按 token
  假计费；用户只能访问自己的历史/额度，Admin 能审计 Provider 状态。

### P1-02 实现 TTS/STT/自定义声音

- **状态**：`TODO`；**优先级**：P1；**依赖**：P0-03、P0-06；**范围**：Gateway protocol/stream、Platform media/auth/storage、Provider.Mock、两套 Web。
- **实现步骤**：增加 bounded audio input/output、voice CRUD/授权、S3 metadata/retention、
  provider adapters；分别定义 character、audio-minute、storage 计费和失败/取消状态；加入
  文件类型/大小/时长校验、下载签名和删除。
- **验收**：音频字节不经日志；越权 voice/object 404/403；取消/断流不重复扣费；重启、
  retention、对象缺失 reconciliation 通过。

### P1-03 实现 captcha 与邮箱域注册额度

- **状态**：`TODO`；**优先级**：P1；**依赖**：P0-08；**范围**：`AuthAbuseService`、UserAuthEndpoints、migration、Admin/User Web、Provider.Mock。
- **实现步骤**：定义 Turnstile/Tencent/Aliyun-like provider interface、challenge TTL/nonce、
  score/error policy；注册、OAuth、Passkey 等入口统一校验；按规范化 email domain 做原子
  日/窗口计数；配置 CSP、Admin 规则、audit/metrics、accessible failure UX。
- **验收**：provider timeout/invalid/重复 challenge fail closed；同域并发不超额；不泄露账户
  是否存在；测试覆盖注册、OAuth、Passkey 和 reset 公共入口。

### P1-04 实现 Provider tier/quota-aware scheduling

- **状态**：`TODO`；**优先级**：P1；**依赖**：P0-01、P0-09；**范围**：SchedulerGrain、quota store/migrations、refresh worker、Admin UI。
- **实现步骤**：持久化 tier、剩余配额、窗口、来源、fetched_at、expires_at、generation；
  用 lease/CAS/advisory lock 刷新；定义 stale/unknown/free-tier/cooldown 策略；调度前原子
  预留，完成/拒绝/未知结果分别结算或保留。
- **验收**：两 Silo 同时刷新只产生一个有效 generation；过期快照不会放行高价请求；429
  会退避并影响健康；重启恢复不重复消耗 quota。

### P1-05 让运行时配置真正传播并可回滚

- **状态**：`PARTIAL`；**优先级**：P1；**依赖**：P0-01、P0-07；**范围**：ConfigGrain、revision outbox/Garnet、所有动态消费者、Admin Web。
- **实现步骤**：配置写入生成 revision/outbox；消费者订阅/轮询后按版本原子 reload；secret
  只允许引用外部 secret；加入 stale-write、rollback、逐节点观察和 actor audit。
- **验收**：两进程最终收敛且不倒退；更新失败可重试/回滚；旧配置不会在 lease 中间改变；
  Admin 能看到版本、失败原因和生效节点。

### P1-06 把支付完成从手工 force-credit 改为 Provider 权威

- **状态**：`PARTIAL`；**优先级**：P1；**依赖**：P0-08；**范围**：`PlatformEndpoints.cs` 支付路由、payment provider interfaces、webhooks/refunds、User/Admin Web。
- **当前缺口**：`/admin/payments/{id}/confirm` 直接把 pending 改成 paid 并写 credit，不能代表外部支付完成。
- **实现步骤**：将 confirm 改为受限的 reconciliation/retry 操作；订单只由已验签 webhook 或
  provider 查询转 paid；校验 amount/currency/provider payment ID；退款、部分退款、重放、
  pending claim、secret rotation 和浏览器 checkout 全部走同一状态机。
- **验收**：伪造/金额不符/重复 webhook 不产生 credit；真实 mock/Stripe-shaped provider 完成
  checkout -> webhook -> ledger；退款累计不超过 paid；Admin 操作仅审计和触发查询。

### P1-07 实现主动 Channel Monitor 与 OPS metrics pipeline

- **状态**：`PARTIAL`；**优先级**：P1；**依赖**：P0-01、P0-07；**范围**：ChannelMonitorStore、OpsMetricsStore、hosted workers、migrations、Admin Web。
- **当前缺口**：现有 `/admin/channel-monitors/check` 与 `/admin/ops-metrics/ingest` 主要是手工写入。
- **实现步骤**：增加模板、schedule、leader fencing、bounded runner、retry/history、告警 delivery；
  metrics collector 从 Gateway/Platform/Provider 自动采集并关联 request/lease IDs，固定 label，
  p95/unavailable/error budgets，窗口恢复和 retention；所有 worker claim 可 reclaim。
- **验收**：重复 worker 不重复 check/alert；Provider outage 形成可查询 incident；恢复关闭
  alert；指标不含 prompt、secret、用户敏感值；Admin filter/refresh/browser 流程通过。

### P1-08 实现 passive Channel Monitor V2

- **状态**：`TODO`；**优先级**：P1；**依赖**：P1-07、P0-03；**范围**：新 migration、rollup worker、Admin/User views、privacy config。
- **实现步骤**：定义 V1/V2 隔离、watermark/backfill、platform/group/model/user/error 维度、
  latency histogram、privacy default、retention、leader lock；从已结算 usage/response 事件
  被动聚合，禁止重复计费和回写业务状态。
- **验收**：乱序/重复事件按 event ID 去重；watermark 重启后单调；bounded backfill 不阻塞
  billable path；Admin 与 User 视图按权限聚合并可解释来源。

### P1-09 完成代理/TLS/秘密与审计安全强化

- **状态**：`PARTIAL`；**优先级**：P1；**依赖**：P0-04、P0-07；**范围**：SecretProtector、NetworkProfileStore、AuditLogStore、deployment secrets、security tests。
- **实现步骤**：master-key rotation/rewrap、step-up auth、最小权限、session/CSRF/rate policy、
  immutable audit retention/export authorization、供应链/secret scan；所有错误和 metrics 做
  递归 redaction；证书/代理轮换必须有过期拒绝和恢复。
- **验收**：旧 key 轮换窗口行为确定；任何 API/日志/Cap'n Proto dump 无 secret；越权 CRUD/导出
  失败；安全扫描、TLS wrong-name/expired/recovery 通过。

### P2-01 完成订阅、兑换、推荐和公告的完整生命周期

- **状态**：`PARTIAL`；**优先级**：P1/P2；**依赖**：P1-06、P0-08；**范围**：subscription/redeem/referral/announcement stores、migrations、Web。
- **实现步骤**：支付确认驱动 purchase；expiry/renew/reconcile；兑换码并发/过期/限次/promotions；
  signup referral attribution、anti-abuse、rebate/transfer；公告 targeting/schedule/read state。
- **验收**：并发只产生一次 entitlement/reward/read；额度预留与 usage settlement 一致；用户
  不能读取他人订单/推荐/公告目标；浏览器流程和审计完整。

### P2-02 完成用户导出、维护和保留策略

- **状态**：`PARTIAL`；**优先级**：P2；**依赖**：P1-09、P1-02；**范围**：MaintenanceStore、media/object retention、User/Admin Web、hosted worker。
- **实现步骤**：定时 cleanup、immutable retention policy、media/orphan cleanup、bounded export
  artifact、download authorization、metrics；导出不含密码、refresh token、API key hash、音频正文。
- **验收**：dry-run 与 apply 的 row/object 计数可解释；重复 key 幂等；保留期内对象不删；
  下载链接过期/越权失败；worker crash 可 reclaim。

### P2-03 补齐 Admin/User Web 的授权和失败流程

- **状态**：`PARTIAL`；**优先级**：P1；**依赖**：P1-03、P1-06、P1-07；**范围**：`platform/admin-web/src`、`platform/user-web/src`、Playwright tests。
- **实现步骤**：为 key、usage export、billing/subscription/order、recovery/passkey、monitor、
  backup、policy、audit 添加真实 API mutation、loading/error/retry、跨用户授权测试；不以
  intercepted response 代替后端状态证据。
- **验收**：Chromium 从空栈登录并完成关键工作流；刷新/重放/过期 session 行为正确；
  UI 不显示 secret，错误码与 API 合同一致。

### P2-04 公共模型、状态、法律页与可访问性

- **状态**：`PARTIAL`；**优先级**：P2；**依赖**：P0-05、P0-09、P1-01；**范围**：User Web public routes、Gateway catalog/readiness、legal config。
- **实现步骤**：模型/状态失败态和版本化法律文本；部署域名/CSP/ingress 配置；无 session
  浏览；表格/键盘/ARIA/accessibility scan。
- **验收**：Gateway catalog authority 与 UI 一致；Provider unavailable 显示可恢复错误；
  条款版本可追溯，匿名路由不读取用户数据。

### REL-01 默认多 Silo/Gateway 拓扑与滚动替换

- **状态**：`PARTIAL`；**优先级**：P0；**依赖**：P0-01、P0-06、P0-08；**范围**：`platform/deploy/stack/docker-compose*.yml`、smoke scripts、readiness/drain。
- **当前缺口**：默认 Compose 只有一个 `platform-silo`；secondary 由 smoke 脚本临时创建。
- **实现步骤**：定义至少两 Silo/两 Gateway 的正式拓扑、placement/version、graceful drain、
  readiness、leader locks；滚动替换 primary/secondary、Garnet/PostgreSQL/MinIO 失败和 rejoin。
- **验收**：任意单节点停止时 billable 请求仍按策略完成；rejoin 无重复 lease/debit/object；
  正式 compose 与 smoke 使用同一配置，不依赖临时容器名。

### REL-02 备份、恢复、签名、异地与 RPO/RTO

- **状态**：`PARTIAL`；**优先级**：P0；**依赖**：P1-09、REL-01；**范围**：BackupStore、hosted scheduler、object storage、Admin UI、deploy。
- **当前缺口**：本地 `/var/lib/scalaapi/backups` + 手工 pg_dump/restore，无集群 scheduler/offsite/signing。
- **实现步骤**：cluster-singleton schedule/claim；加密+签名+key rotation；S3/offsite retention；
  独立 restore target、失败注入、checksum；记录 RPO/RTO 和 runbook；禁止恢复到 live authority。
- **验收**：空库 restore 后迁移/用户/账务可读；伪造/篡改/错误目标被拒；重复 create/restore
  幂等；测得并记录 RPO/RTO；Admin 操作有双语状态和 audit。

### REL-03 一小时长压测与资源清理

- **状态**：`TODO`；**优先级**：P0；**依赖**：P0-06、P0-08、REL-01；**范围**：stack smoke、realtime/media/load clients、metrics。
- **实现步骤**：运行 3600 秒媒体 due-work/stream/realtime/backpressure 混合负载；注入 Provider/
  Garnet/PostgreSQL/MinIO/TLS/进程替换；采集 p95、连接、buffer、lease、hold、outbox backlog。
- **验收**：无重复 financial effect、无泄漏连接/容器/临时网络、unknown-charge incident 可解释；
  顶层命令传播任何失败，结束 `podman ps -a` 无项目残留。

### REL-04 跨仓库 CI 与发布制品

- **状态**：`PARTIAL`；**优先级**：P0；**依赖**：GATE-02、REL-01..REL-03；**范围**：两个仓库 CI、release scripts、镜像构建。
- **实现步骤**：使用 immutable reviewed refs 或专用 release repo；阻塞式执行 build/test/contract/
  migrations/Web/C++/smoke/bench/security；镜像记录 source commit/digest；不允许 sibling checkout
  缺失导致假绿。
- **验收**：任一子测试/benchmark 非零即 job 失败；产物可由 clean checkout 重建；报告当前
  commit、镜像 digest、迁移版本、测试总数和 skip 原因。

### REL-05 更新差距矩阵并关闭任务

- **状态**：`TODO`；**优先级**：P1；**依赖**：所有任务；**范围**：
  `feature-gap-report.md`、`feature-inventory.csv`、`current-state.md`、本文件。
- **实现步骤**：每个域只在四项证据齐全后提升状态；保留历史证据与当前运行日期的区别；
  对新 Sub2API upstream commit 做静态 delta 审计；记录明确 out-of-scope 项。
- **验收**：65 域总数、状态汇总、任务卡 ID、风险登记和验证文档互相一致；没有“路由存在
  = 功能完成”“历史通过 = 当前通过”的表述。

## 3. 每项任务的完成记录模板

复制以下模板追加到任务卡或对应 PR 描述中：

```text
任务 ID: P0-xx
状态: DONE | PARTIAL | BLOCKED
当前提交:
变更文件:
数据/状态机变化:
新增或修复的测试:
执行命令及退出码:
运行环境（PostgreSQL/Garnet/Provider/MinIO/Silo 数）:
关键结果（lease/hold/usage/debit/object/audit）:
失败注入与恢复结果:
未完成分支/风险:
下一依赖任务:
```

## 4. 最低最终出口条件

不能只因为所有任务卡被勾选就宣布完成。最终必须同时满足：

1. GATE-01 的真实数据库测试为绿，缺少依赖不会假绿。
2. 每个 P0/P1 域都有当前源码、自动测试和 source-built runtime 证据；历史 smoke 只作补充。
3. 两 Silo/两 Gateway、缓存/数据库/对象存储故障、进程替换、长压测都满足 exactly-once
   账务与可解释 reconciliation。
4. 当前 `feature-gap-report.md`、`feature-inventory.csv`、`verification.md`、风险登记和
   本清单的状态/提交/日期一致。
5. clean checkout 的阻塞 CI 通过，所有项目容器、网络、临时卷按脚本清理。
