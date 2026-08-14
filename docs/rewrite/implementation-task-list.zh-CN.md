# ScalaAPI 全新重写实施任务清单

> 审计日期：2026-08-14。代码基线：Platform `30d82d0`、Gateway `98c62fd`、
> ScalaAPI 超级项目 `032721b`；只读参考 Sub2API `origin/main@fbfdcef`。
>
> ScalaAPI 是 Orleans + C++ 的全新产品。Sub2API 只用于发现候选能力，不兼容其
> API、错误体、数据库、migration、ID、密钥、状态、Redis、部署或数据；不做升级、
> 迁移、双写、旧别名、协议协商或兼容分支。

## 0. 当前结论

2026-08-13 的“65 个域全部完成”结论仍然撤销。此后基础门禁已经实质修复，但产品
完成度不能由基础门禁代替：

- Platform Release restore/build 通过，0 warning / 0 error；
- 隔离 PostgreSQL 17 首跑 66 个 migration 记录，二跑 66 个 skip；数据库测试
  502/502 通过；
- 无 `GREENFIELD_SCHEMA_CONNECTION` 的负向运行明确失败：Host Tests 113 failed、
  145 passed，共 258，不再静默跳过数据库测试；
- Platform 六个 Dry benchmark 通过，包括四个 Scheduler case；
- Platform/Gateway 三份 contract 字节一致，checksum、generated C# 和 retired dependency
  检查通过；
- Admin Web、User Web 的 clean install、typecheck、production build 均通过，但两棵
  依赖都报告 `nanoid <3.3.18` high advisory；
- ScalaAPI `032721b` 固定的旧配对 Platform `e73a5d8` + Gateway `777278e` 验证通过，
  但最新独立 head 尚未作为一对推进；
- 共享 Gateway worktree 有用户 WIP，因引用不存在的
  `LeaseAbortDisposition::Safe` 而不能编译，不能用于发布证据。

因此 G0 表示基础实现和聚焦门禁已经修好；P0/P1/OPS/REL 仍必须用当前配对的
Provider、浏览器、多进程、故障和长跑证据逐项关闭。

## 1. 执行规则

状态：`TODO`、`DOING`、`PARTIAL`、`SCAFFOLD`、`BLOCKED`、`DONE`。

任务只有在以下证据同时存在时才可标 `DONE`：

1. 当前 immutable Platform/Gateway 配对 commit；
2. 产品原生生产实现和持久状态机；
3. 自动测试实际执行，缺环境必须显式失败或明确 skip 原因；
4. 数据库、跨仓、浏览器、Provider、对象存储或多进程边界有对应证据；
5. 命令、退出码、环境、executed/skipped 数、持久断言和清理结果可追溯；
6. 本清单、gap report、CSV、risk、verification 与 release manifest 一致。

禁止：

- 引入 Sub2API schema/data/key/ID/status/Redis/CDC/Debezium/migration；
- 为未发布的内部协议保留旧字段、旧路由、双读写或版本协商；
- 用 route/table/mock 200、脚本存在、历史日志或前端 build 代替端到端证据；
- 在 Gateway 建第二套账务权威；
- 对 forwarded/partial/timeout/disconnect 等未知费用结果自动 release；
- 从独立组件 tag 发布未经 ScalaAPI 配对门禁证明的镜像。

## 2. G0 基础门禁

### G0-01 全新空库 migration

- **状态**：`DONE`；**依赖**：无。
- **当前证据**：Platform `30d82d0` 在隔离 PostgreSQL 17 上首跑 Orleans 000 加 65 个
  产品 migration，66/66 apply；二跑 66/66 skip；同一 schema 上 solution 502/502。
- **保持条件**：固定 migration manifest；只使用产品原生表名；禁止 ORM CodeFirst、
  参考系统表或历史数据补洞；每个 paired CI 必跑空库双跑和 schema tests。

### G0-02 跨仓 Cap'n Proto 单一权威

- **状态**：`DONE`；**依赖**：无。
- **当前证据**：最新 Platform/Gateway 的三份 contract checksum 和字节一致；generated C#
  与固定 compiler 输出一致；ScalaAPI 当前旧配对的 `validate-pair.sh` 通过。
- **保持条件**：每次协议变更原子更新 canonical、vendor、generated、digest 和两端测试；
  manifest 记录成对 SHA 与 contract digest，不保留旧协议兼容层。

### G0-03 测试、benchmark 与 CI 可信度

- **状态**：`DONE`；**依赖**：G0-01。
- **当前证据**：数据库 suite 502/502；缺 DB 的 Host negative control 113 failed / 145
  passed；六个 Platform Dry benchmark 有有效结果；两个 Web build 通过；中央 pair workflow
  执行 migration、tests、benchmark、contract 和 Web gate。
- **保持条件**：故意破坏 DB、contract、benchmark、typecheck 或 evidence 时顶层必须非零；
  不允许独立 tag publisher 绕开 pair workflow。

### G0-04 smoke/stress 验证器所有权与失败传播

- **状态**：`DONE`；**依赖**：G0-01。
- **当前证据**：stress SQL 已按真实 Gateway SQLite / Platform PostgreSQL 所有权修正，
  后台 child、SQL 和 settlement timeout 会传播为非零。
- **保持条件**：脚本 schema contract test 常驻；坏表名、提前退出 child、settlement
  timeout 三个负向探针都必须失败并清理资源。实际短跑与 3600 秒运行归 REL-01/02。

### G0-05 首启 setup 与 Gateway readiness

- **状态**：`DONE`；**依赖**：G0-01。
- **当前实现**：Gateway clean head 包含依赖/listener 启动失败传播及 dispatch、Garnet、
  SQLite、listener readiness；Platform 有一次性首管理员锁定和默认 secret 拒绝。
- **保持条件**：产品原生 setup，不复制参考系统 contract/state；clean empty-volume、并发/
  replay、默认 secret 和每个依赖/listener 的运行时负向证据归 DEP-03/REL-01。

## 3. P0 核心闭环

### P0-01 账务、lease、调度和 exactly-once

- **状态**：`PARTIAL`；**依赖**：G0-01、G0-03、G0-04。
- **已有**：PostgreSQL lease/hold/idempotency/usage/ledger/outbox/reconciliation，持久 slot
  lease、account health、Gateway SQLite usage/evidence outbox 和保守 unknown-charge 状态。
- **已补齐**：durable `output_started`；未确认 non-retryable usage 保留而非删除；WebSocket
  trusted-proxy/query；media observed model；output-started/cancellation fault hooks。
- **剩余**：当前 clean pair 上跑 HTTP/realtime 全 crash matrix、两 Silo contention、进程
  replacement 和长时间 backlog；每 request 只能有一次 debit、安全 release 或 incident。

### P0-02 Provider 协议和转换矩阵

- **状态**：`PARTIAL`；**依赖**：G0-02、P0-01。
- **已有**：OpenAI Chat/Responses、Anthropic、Gemini JSON/SSE、terminal/usage/error、
  finish reason 和 pairwise text conversion。
- **已补齐**：unsupported multimodal/multi-candidate 显式拒绝；tool-result image 检测；
  Anthropic SSE event 修复；Gemini duplicate tool ID；跨格式 response ID 保留；未知 method
  返回 405；TLS profile 不再被静默忽略。
- **剩余**：支持的 tool-result/media matrix、path/general header bounds、Provider-owned live
  goldens，以及 upstream error exposure/rewrite/monitor suppression 的安全产品决策。

### P0-03 catalogue、tokenizer、价格和 Provider quota 权威

- **状态**：`PARTIAL`；**依赖**：P0-01。
- **已有**：NUMERIC immutable price snapshot；active entity model aggregation；匿名 catalogue
  在 Garnet 不可用时返回 503；quota CAS/store/scheduler 输入；OpenAI、Anthropic、Gemini、
  xAI HTTP quota clients。
- **缺口**：quota worker 仍从已 seed 的 `provider_quota_state` 枚举账户；Provider
  catalogue/tokenizer/price authority、stale/unknown policy 和 fenced multi-Silo refresh 尚未
  以运行时故障矩阵证明。

### P0-04 xAI/Grok 专用 Provider

- **状态**：`PARTIAL`；**依赖**：P0-02、P0-03。
- **已有**：provider identity、OpenAI-compatible text fixtures、credential state、catalogue
  source 和 xAI quota client。
- **剩余**：明确 catalogue/text/Responses/OAuth/quota/media/Search/X Search/realtime/voice/
  pricing matrix；未实现能力稳定返回产品原生 unsupported；完成 401/429/malformed/timeout/
  disconnect/terminal usage 和账务证据。

### P0-05 Provider target、proxy 和 TLS

- **状态**：`PARTIAL`；**依赖**：P0-02。
- **已有**：target/auth header bounds、加密 proxy credential、unknown method 405、TLS profile
  显式拒绝。
- **剩余**：path/general header bounds；实现真正 TLS fingerprint transport 或保持明确
  unsupported；验证 wrong-name、expiry、rotation 和 proxy credential isolation。

## 4. P1 专用能力

### P1-01 Web Search / X Search

- **状态**：`PARTIAL`；**依赖**：P0-03、P0-04。
- **已有**：fresh-schema history/state、routes、mock/status、price units 和 authorization source。
- **剩余**：真实 Web/X adapters、bounded query/domain/recency/source/result/redaction、
  per-query settlement；让 advertised streaming 进入真实 bounded policy/usage path，或取消声明。

### P1-02 TTS / STT / 自定义声音

- **状态**：`PARTIAL`；**依赖**：P0-03、P0-05。
- **已有**：fresh-schema voice/audio state、同步 contract、routes、validation、mock、object/
  pricing source。
- **剩余**：multipart/audio bytes、owner auth、签名下载、取消、retention/repair、character/
  time/storage snapshot；证明字节不进日志且断流/重启不重复计费。

### P1-03 Images / video 全生命周期

- **状态**：`PARTIAL`；**依赖**：P0-01、P0-03。
- **已有**：sync/async/batch/item/cancel/delete、durable metadata、S3 signing、repair/retention、
  video create/edit/extend/control source和 fresh-schema tests。
- **剩余**：统一 Gateway 32 MiB HTTP 与 Platform 1 MiB RPC 上限，或改用 bounded object
  reference；运行 MinIO partition、partial PUT、committed-response loss 和两 Silo claim。

### P1-04 身份与公开防滥用

- **状态**：`PARTIAL`；**依赖**：G0-01、G0-03。
- 将 captcha/domain/rate/anti-enumeration 覆盖 register/recovery/verify/OAuth/Passkey；补
  multi-device refresh、TOTP backup sign-in、真实 WebAuthn、SMTP TLS/receipt/expiry 和浏览器
  失败流程。

### P1-05 商业生命周期

- **状态**：`PARTIAL`；**依赖**：P0-01、P1-04。
- checkout -> signed webhook/provider query -> ledger -> subscription 作为一个状态机；补
  refund crash/replay、secret rotation、promo limits、signup referral/anti-abuse/rebate/transfer、
  announcement targeting 和真实浏览器/Provider 证据。

## 5. 运维、安全和体验

### OPS-01 Active Channel Monitor

- **状态**：`PARTIAL`；**依赖**：G0-01。
- **已有**：template/claim/retry/incident/UI、PostgreSQL advisory-lock leadership、bounded HTTP
  channel probe。
- **剩余**：两进程 fencing、Provider-specific auth/request、timeout/retry、incident open/close
  和 leader restart 运行证据。

### OPS-02 Passive Monitor V2 / metrics

- **状态**：`PARTIAL`；**依赖**：OPS-01、P0-01。
- **已有**：rollup/watermark/privacy、PostgreSQL advisory-lock leadership、latency percentile
  dimension 修复、metrics/alerts/UI。
- **剩余**：event dedup、乱序、水位、bounded backfill、restart、privacy、跨进程关联和 alert
  delivery/recovery。

### OPS-03 Backup / offsite / restore

- **状态**：`PARTIAL`；**依赖**：G0-01、G0-04。
- **已有**：schedule claim、due backup job、checksum/encrypt/sign、HTTP PUT offsite upload、
  retention 和 isolated restore primitives。
- **缺口**：scheduler 当前创建 `running` job 后仍标注实际 `pg_dump` 需另一个 worker，尚无
  已证明的 job -> artifact -> encrypt/sign -> PUT -> HEAD/readback -> retention 完整链；需运行
  corruption/wrong key/partial restore，记录 RPO/RTO 和 audit。

### SEC-01 Content Policy 与 Realtime

- **状态**：`PARTIAL`；**依赖**：P0-01、P0-02。
- **已有**：WebSocket 首 body、trusted-proxy/query 和 initial non-chat capability policy 已进入
  clean head；HTTP capability selection 更明确。
- **缺口**：后续 frame 仍原样 relay；为 text/binary/audio/media/search/embeddings 明确
  request/response/binary 策略，验证 fail-closed、order、reconnect 和唯一账务结果。

### UI-01 Admin/User Web 全流程与依赖安全

- **状态**：`PARTIAL`；**依赖**：上述 API/state 任务。
- **已有**：两端 `npm ci`、typecheck、production build 通过。
- **剩余**：修复两端 `nanoid <3.3.18` high advisory；运行 source-built authenticated
  mutation/authorization/loading/error/retry/session expiry/payment/policy/monitor/backup/export
  和 public accessibility browser matrix。

## 6. 发布任务

### REL-01 当前配对短门禁

- **状态**：`PARTIAL`；**依赖**：G0-01..G0-05、P0-01..P0-05。
- Platform migration、DB tests、benchmark、contracts 和 Web build 已有当前独立证据；中央
  workflow 可验证旧支持配对。
- 剩余：修复/提交或撤销 Gateway WIP，选定最新两个 gitlink，在同一次 clean paired run 中
  执行 Gateway build/tests/benchmark、Platform gates、浏览器 E2E、startup/readiness/target/
  evidence 负向探针和 2 Silo/2 Gateway fault/cleanup。

### REL-02 3600 秒混合 fault/load

- **状态**：`BLOCKED`；**依赖**：REL-01、P1-01..P1-05、OPS-01..OPS-03。
- 执行 stream/realtime/media/backpressure + Provider/Garnet/PostgreSQL/MinIO/TLS/process faults。
- 验收无重复 debit/usage/object、无 terminal active hold、unknown 均有 incident、资源 backlog
  有界且最终无项目 container/network/volume。

### REL-03 配对 immutable release

- **状态**：`PARTIAL`；**依赖**：REL-02、UI-01。
- 中央 release 已具备 pair validation、exact tags、migration/contract manifest 和 evidence
  生成，不发布 `latest`；当前支持配对仍旧于 standalone heads。
- 一个最终 manifest 必须固定 Platform SHA、Gateway SHA、contract digest、migration manifest、
  image digest、executed/skipped tests 和 artifact；当前 evidence generator 仍需从上传的
  TRX/Gateway/Web 结果解析 totals，不能固定写 `status: passed` / `skipped: []`；删除/禁用
  虚假的 `/admin/system/update`，rolling/rollback 由外部配对部署控制器负责。

### REL-04 同步最终文档

- **状态**：`DOING`；**依赖**：REL-03。
- `current-state.md`、gap report、CSV、risk、verification、本清单必须使用同一 paired ref；
  65 域只能在各自边界证据完整后变为 `verified`。

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

1. 65 域为 `verified` 或经明确产品决策排除；
2. 空库、DB tests、跨仓 contract、Provider、浏览器、短 smoke、3600 秒 fault/load 都是
   当前 paired evidence；
3. scaffold/placeholder/no-I/O 路径已完成或不再宣传；
4. paired clean checkout 可重复构建发布，失败子任务不能被吞掉；
5. Markdown、CSV、提交、计数、风险、日期和 release manifest 一致；
6. 两个 Web dependency tree 没有未处理的 high/critical advisory；
7. 全程没有引入任何 Sub2API 兼容、迁移或运行依赖。
