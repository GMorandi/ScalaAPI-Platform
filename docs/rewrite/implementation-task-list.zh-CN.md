# ScalaAPI 全新重写实施任务清单

> 审计基线：Platform `bc083d1`、Gateway `b6e4e02`、只读参考
> Sub2API `origin/main@fbfdcef`；日期 2026-08-14。
>
> 目标是用 Orleans + C++ 实现一个全新的 ScalaAPI 产品。Sub2API 只作为非规范性
> 研究输入；候选能力必须经 ScalaAPI 产品决策才进入范围。不兼容它的 API、错误体、
> 数据库、migration、ID、密钥、状态、Redis、部署或数据；不做升级、迁移、双写、
> 别名或兼容分支。

## 0. 当前结论

旧版清单把 GATE-01..REL-05 全部标为 `DONE`，并据此宣称 65 个域全部完成。
本次在当前提交上重新验证后，该结论撤销：

- Gateway Release build 和 159/159 CTest 通过；
- Platform 无数据库运行报告 502/502，但至少 123 个数据库测试直接 `return`；
- 空 PostgreSQL 17 已提交 000 与 001-053，随后 055 因不存在 `users` 表失败；
- Platform/Gateway 的 `dispatch.capnp` 在 TTS/STT 枚举上不一致；
- Scheduler benchmark 四个 case 均因 benchmark Silo 缺少 `ISlotLeaseStore` 而无报告并退出 1；
- monitor、quota refresh、scheduled backup、offsite backup 仍含明确模拟/占位实现；
- stress verifier 查询了不存在的 PostgreSQL 表，不能作为已通过门禁。

因此所有完成状态必须由下面的新任务重新闭合，不继承旧勾选。

## 1. 执行规则

状态：`TODO`、`DOING`、`PARTIAL`、`SCAFFOLD`、`BLOCKED`、`DONE`。

一个任务只能在以下证据同时存在时标 `DONE`：

1. 当前 immutable Platform/Gateway commit；
2. 生产实现和产品原生状态机；
3. 自动测试实际执行，不能因缺环境直接返回；
4. 需要数据库/跨仓/浏览器/运行时的任务有对应当前证据；
5. 命令、退出码、环境、持久状态断言和清理结果已记录；
6. 文档、CSV、风险登记和验证结果一致。

禁止：

- 引入 Sub2API schema/data/key/ID/status/Redis/CDC/Debezium/migration；
- 为未发布的内部协议保留旧字段、旧路由、双读写或版本协商；
- 用 route/table/mock 200/脚本存在/历史日志代替运行证据；
- 用无数据库 502/502 宣称集成通过；
- 在 Gateway 建第二套账务权威；
- 对 forwarded/partial/timeout/disconnect 等未知费用结果自动 release。

## 2. P0 基础门禁

### G0-01 修复全新空库 migration

- **状态**：`DONE`；**依赖**：无；**阻塞**：所有数据库与运行时任务。
- **现状证据**：Orleans + 001-053 已提交；055 报 PostgreSQL `42P01`，因为引用
  `users`；055/056 也引用 `api_keys`，而本产品表为 `user_accounts` /
  `user_api_keys`。产品 migration 共 65 个，编号跳过 054。
- **实现**：修正 055/056，并静态/运行审计 057-066 的所有表、列、FK、约束和
  idempotency；只使用产品原生表名，不启动 ORM CodeFirst 补表。
- **验收**：空 PostgreSQL 17 首跑 66 个记录（Orleans + 65），二跑 66 个 skip；
  `MigrationSchemaTests` 和全 solution DB 测试随后在同一 schema 通过。

### G0-02 修复跨仓 Cap'n Proto 漂移

- **状态**：`DONE`；**依赖**：无；**阻塞**：TTS/STT、跨仓发布。
- **现状证据**：Platform canonical `dispatch.capnp` 有 `audioTts @12` /
  `audioStt @13`，Gateway vendor 没有；Gateway 手写 C++ enum 仍写了 12/13。
- **CI 缺口**：greenfield workflow 没有 checkout Gateway，且调用脚本时不传路径，实际只验
  Platform 本地 digest，不会执行跨仓 `cmp`。
- **实现**：一次原子跨仓变更 canonical/vendor/generated/digests；删除手写数值作为
  独立权威或增加 compile-time equality gate。全新项目直接替换协议，不保留旧版本。
- **验收**：`verify-contracts.sh ../gateway`、generated C# 比较、两个 Release build、
  audio dispatch 测试全部通过；release manifest 记录配对 SHA。

### G0-03 让测试和 CI 结果可信

- **状态**：`DONE`；**依赖**：G0-01；**阻塞**：所有完成声明。
- **现状证据**：46 个测试文件读取 `GREENFIELD_SCHEMA_CONNECTION`，123 个测试直接
  return；普通 CI/release 无 PostgreSQL；重复 Admin Web workflow 允许 typecheck 失败；
  Scheduler benchmark 因未注册 `ISlotLeaseStore` 四个 case 均无有效报告并退出 1。
- **实现**：数据库 fixture/trait 显式 skip 或 integration job 缺依赖直接失败；合并普通
  与 greenfield 必需门禁；补齐 benchmark Silo 的 production-equivalent scheduler 依赖；
  删除/纳管无门禁的 tag `docker.yml`；release 的 clean build 与配对验证必须早于任何 push；
  Gateway 自身 `v*` publisher 也必须删除或接入同一配对门禁；
  cross-repo contract、benchmark、Web E2E、smoke/security 必须阻塞发布。
- **验收**：报告真实 executed/skipped 数和原因；故意破坏 DB/contract/benchmark/
  typecheck 时顶层 job 非零；修复后当前 DB-enabled suite 全绿。

### G0-04 修复 smoke/stress 验证器

- **状态**：`DONE`；**依赖**：G0-01。
- **现状证据**：脚本查询不存在的 PostgreSQL `gateway_usage_outbox` 和
  `reconciliation_incidents`；Gateway outbox 实际为本地 SQLite；后台 child 退出与
  settlement timeout 可能只打印日志/警告。
- **实现**：按真实 ownership 查询 Gateway/Platform；所有 child/SQL/settlement 失败必须
  累计并返回非零；为脚本 SQL 增加 schema contract 测试。
- **验收**：120 秒正常 fault smoke 通过；坏表名、提前退出 child、settlement timeout
  三个故意失败探针均使顶层失败并清理所有资源。

### G0-05 关闭首启 setup 与 Gateway readiness

- **状态**：`DONE`；**依赖**：G0-01；**阻塞**：DEP-03、REL-01。
- **现状证据**：Gateway 初始化可在依赖或 bind/listen 失败后保留存活进程，`/ready` 只验
  dispatch UDS；原 DEP-02 只证明无参考系统依赖，并未证明空栈 bootstrap。
- **实现**：任一每核 listener、dispatch、Garnet、usage SQLite 不可用时启动/ready 失败；在
  deployment command 与一次性 setup API/UI 中选择一个产品原生首管理员流程，不复制
  Sub2API contract/default/state。
- **验收**：依赖失败、并发/replay 初始化、默认 secret、完成后再 setup 均有负向测试；空卷
  只能创建一个经授权的首管理员。

## 3. P0 核心闭环

### P0-01 账务、lease、调度和 exactly-once

- **状态**：`PARTIAL`；**依赖**：G0-01、G0-03、G0-04。
- **已有**：PostgreSQL lease/hold/idempotency/usage/ledger/outbox/reconciliation，持久 slot
  lease、account health、Gateway SQLite outbox 和保守 unknown-charge 状态。
- **本轮补齐**：`output_started` 持久证据（Gateway evidence_outbox + reporter 重试）；未确认
  non-retryable usage 改为 dead-letter 保留而非删除；HTTP/realtime 共用 trusted-proxy
  identity 和 query 验证；media observed model 从 provider 响应传入 settlement；
  `after_output_started` / `during_cancellation` fault hook 和 smoke 用例。
- **剩余**：两 Silo chat/stream dispatch contention 长跑；全 crash matrix 在完整
  docker-compose 环境执行验证。
- **验收**：每个 request 只能得到一次 debit、一次安全 release 或一个可解释 incident；
  进程替换后无重复 Provider dispatch/usage/object。

### P0-02 Provider 协议和转换矩阵

- **状态**：`PARTIAL`；**依赖**：G0-02、P0-01。
- **已有**：OpenAI Chat/Responses、Anthropic、Gemini JSON/SSE，错误、terminal、usage、
  finish reason、tool-call response 和 pairwise text conversion。
- **补齐**：tool result、multimodal、multiple candidates、identifier、未知 native field
  策略和全 pair runtime provider group；未知 method/path/general header/TLS profile 必须在
  Provider I/O 前拒绝，不能静默改成 POST 或只过滤 hop-by-hop header；明确是否提供上游
  error pass-through/rewrite/monitor suppression，接受时必须有 bounded/redacted 产品规则，
  不接受时明确 unsupported。
- **验收**：versioned request/response/SSE/error goldens + source-built E2E；不静默只取第一
  个文本 candidate。

### P0-03 目录、tokenizer、价格和 Provider quota 权威

- **状态**：`PARTIAL`；**依赖**：P0-01。
- **已有**：NUMERIC immutable price snapshot、catalog/token count shape、provider quota
  store/CAS 和 scheduler 输入。
- **缺口**：Quota worker 仅从已 seed 的 quota 表读账户并改 generation，不调用 Provider；
  catalog/tokenizer/price 的生产适配器不完整；匿名 models 在 Garnet 故障时返回空 200。
- **验收**：真实账户发现、fenced refresh、stale/unknown policy、Provider mock 全故障矩阵，
  每种生产 adapter 至少一个受控 live contract 证据。

### P0-04 xAI/Grok 专用 Provider

- **状态**：`PARTIAL`；**依赖**：P0-02、P0-03。
- **已有**：provider identity、OpenAI-compatible text fixtures、credential/quota/catalog source。
- **原则**：generic Bearer/OpenAI shape 不是完整 Grok 支持。
- **补齐**：明确 catalogue/text/Responses/OAuth/quota/media/Search/X Search/realtime/voice/
  pricing capability matrix；未实现能力返回稳定产品原生 unsupported。
- **验收**：native success、401/revoked、429、malformed、timeout、disconnect、terminal usage、
  Admin/scheduler/billing/current runtime 证据。

## 4. P1 专用能力

### P1-01 Web Search / X Search

- **状态**：`BLOCKED`；**依赖**：G0-01、P0-03/P0-04。
- 修复 search history FK；定义 bounded query/domain/recency/source/result/redaction；分别实现
  Web/X adapter、per-query 计费、account penalty、owner history；让声明的 streaming 走真实
  bounded stream/policy/usage 路径，或取消该能力声明。
- 验收空/部分结果、401/429/5xx/timeout/malformed/replay/权限/账务。

### P1-02 TTS / STT / 自定义声音

- **状态**：`BLOCKED`；**依赖**：G0-01、G0-02、P0-03。
- 修复 voice/audio FK 和 contract；实现 multipart/audio 字节、object metadata、签名下载、
  owner auth、取消、retention/repair、character/time/storage price snapshot。
- 验收字节不进日志、越权失败、重启/对象缺失/断流不重复计费。

### P1-03 Images / video 全生命周期

- **状态**：`PARTIAL`；**依赖**：P0-01、P0-03。
- 从空栈重跑 sync/async/batch/item/cancel/delete/ZIP/retention；补齐 video provider/fault/
  restore；注入 MinIO partition、partial PUT、committed response loss、两 Silo claim。
- 统一 Gateway 32 MiB HTTP 与 Platform 1 MiB RPC 上限，或改为 bounded metadata/object
  reference；oversize 必须在 lease/dispatch 前返回稳定产品错误。
- 验收一个 operation/lease 对应唯一 owner/object/financial effect。

### P1-04 身份与公开防滥用

- **状态**：`PARTIAL`；**依赖**：G0-01、G0-03。
- 将 captcha/domain/rate/anti-enumeration 覆盖 register/recovery/verify/OAuth/Passkey；补
  multi-device refresh、TOTP backup sign-in、真实 WebAuthn、SMTP TLS/receipt/expiry。
- 验收 hash/encrypted token、secret-free log/metric/audit 和浏览器失败流程。

### P1-05 商业生命周期

- **状态**：`PARTIAL`；**依赖**：P0-01、P1-04。
- checkout -> signed webhook/provider query -> ledger -> subscription 一套状态机；补 refund
  crash/replay、promo limits、signup referral/anti-abuse/rebate/transfer、announcement target。
- Mock 只冻结契约，生产完成必须有真实 provider/secret rotation/browser 证据。

## 5. P1/P2 运维和体验

### OPS-01 Active Channel Monitor

- **状态**：`SCAFFOLD`；**依赖**：G0-01。
- 当前每个进程 `IsLeader=true` 且 check 为模拟成功。改为 PostgreSQL fencing + 实际 bounded
  Provider/channel probe + retry/incident/recovery。
- 两进程同时运行只产生一次 check/alert，故障恢复关闭 incident。

### OPS-02 Passive Monitor V2 / metrics

- **状态**：`SCAFFOLD`；**依赖**：OPS-01、P0-01。
- 替换 leader placeholder；验证 event dedup、乱序、水位、bounded backfill、restart 和隐私；
  Gateway/Platform/Provider 用 bounded request/lease ID 关联，补 alert delivery/recovery。

### SEC-01 Realtime Content Policy

- **状态**：`PARTIAL`；**依赖**：P0-01、P0-02。
- 当前 WebSocket dispatch 未带首帧 body，后续双向帧原样转发，且跳过普通 query 校验与
  trusted-proxy IP 解析；HTTP response policy 也只覆盖 chat-classified 能力。实现共享身份/
  query 规则和 bounded text frame 双向 evaluate；binary/audio 及其他能力必须明确 block、
  classifier 或允许策略，禁止静默绕过。
- 验收 block/fail-closed/audit/unknown-charge/重连时只有一个 lease 与可解释账务结果。

### OPS-03 Backup / offsite / restore

- **状态**：`SCAFFOLD`；**依赖**：G0-01、G0-04。
- 当前 scheduler 只 retention + 标记完成，未 create backup；offsite 只记 completed，未传字节。
- 实现 singleton due schedule -> pg_dump -> encrypt/sign -> S3 PUT/HEAD/readback checksum ->
  retention；restore 只到 isolated target，注入 corruption/wrong key/partial failure。
- 验收真实 RPO/RTO、rolling/rollback 和 audit，不恢复 Sub2API 数据。

### UI-01 Admin/User Web 全流程

- **状态**：`PARTIAL`；**依赖**：上述 API/state 任务。
- 当前 typecheck/build 通过；用 source-built backend 跑 authenticated mutation/authorization/
  loading/error/retry/session expiry/payment/policy/monitor/backup/export/public accessibility。
- intercepted response 不能代替持久后端证据。

## 6. 发布任务

### REL-01 当前空栈短门禁

- **状态**：`BLOCKED`；**依赖**：G0-01..G0-05、P0-01..P0-05。
- migration 双跑、DB tests、Gateway tests、Web build/E2E、contract generate/compare、完整 mock
  matrix、Gateway startup/readiness/target/evidence 负向探针、2 Silo/2 Gateway
  replacement/fault/cleanup 一次执行，任一失败顶层非零。

### REL-02 3600 秒混合 fault/load

- **状态**：`BLOCKED`；**依赖**：REL-01、P1-01..P1-05、OPS-01..OPS-03。
- stream/realtime/media/backpressure + Provider/Garnet/PostgreSQL/MinIO/TLS/process faults。
- 验收无重复 debit/usage/object、无 terminal active hold、unknown 均有 incident、资源 backlog
  有界、最终无项目 container/network/volume。

### REL-03 配对 immutable release

- **状态**：`BLOCKED`；**依赖**：REL-02、UI-01。
- 一个 manifest 固定 Platform SHA、Gateway SHA、contract digest、migration manifest、image
  digest、executed/skipped tests 和证据 artifact；全部通过后才能 tag/publish `latest`。
- 删除/禁用当前只查 manifest 就声称 “downloaded” 的 `/admin/system/update`；rolling/rollback
  属于外部配对部署控制器，除非端点接入并验证该真实事务。
- `deploy/release.sh` 必须实际运行并捕获 test/benchmark/migration/clean build 结果，禁止固定
  输出未执行的 “all passed / no skips”。
- manifest 不包含 Sub2API commit/data，因为这不是升级路径。

### REL-04 同步最终文档

- **状态**：`DOING`（本次调查文档已更新；最终实现后需再次关闭）；**依赖**：REL-03。
- `current-state.md`、gap report、CSV、risk、verification、本清单必须同一 paired ref 一致。
- 65 域只能在各自当前证据完整后变为 `verified`；out-of-scope 必须明确记录。

## 7. 每次完成记录

```text
任务 ID:
状态:
Platform/Gateway commit:
变更文件:
产品原生 contract/state 变化:
执行命令及退出码:
测试 executed/skipped（含原因）:
运行环境和镜像 digest:
持久状态断言（lease/hold/usage/debit/object/audit）:
故障注入与恢复:
资源清理结果:
剩余风险:
```

## 8. 最终出口条件

只有以下条件全部成立才能宣布项目完成：

1. 65 域为 `verified` 或经明确决策排除；
2. 空库、数据库测试、跨仓 contract、浏览器、短 smoke、3600 秒 fault/load 全部是当前证据；
3. scaffold/placeholder/no-I/O 路径已实现或不再宣传；
4. paired clean checkout 可重复构建发布，失败子任务不能被吞掉；
5. 八份 Markdown 与 CSV 的提交、计数、状态、风险和日期一致；
6. 全程没有引入任何 Sub2API 兼容、迁移或运行依赖。
