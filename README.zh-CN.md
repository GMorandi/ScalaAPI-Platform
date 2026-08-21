# ScalaAPI Platform(平台)

[English](README.md) | **简体中文**

[![ScalaAPI Release](https://github.com/GMorandi/ScalaAPI-Platform/actions/workflows/release.yml/badge.svg)](https://github.com/GMorandi/ScalaAPI-Platform/actions/workflows/release.yml)
[![.NET Build](https://github.com/GMorandi/ScalaAPI-Platform/actions/workflows/dotnet.yml/badge.svg)](https://github.com/GMorandi/ScalaAPI-Platform/actions/workflows/dotnet.yml)
[![Gateway Build](https://github.com/GMorandi/ScalaAPI-Platform/actions/workflows/gateway.yml/badge.svg)](https://github.com/GMorandi/ScalaAPI-Platform/actions/workflows/gateway.yml)
[![Image Build and Integration](https://github.com/GMorandi/ScalaAPI-Platform/actions/workflows/stack.yml/badge.svg)](https://github.com/GMorandi/ScalaAPI-Platform/actions/workflows/stack.yml)

[ScalaAPI](https://github.com/GMorandi/ScalaAPI) LLM API 平台的 business 与资金
权威:一个 .NET 10 服务集群,掌管账号、凭证、路由、配额、租约、结算、计费与运维,
由 C++23 边缘网关通过权威 Cap'n Proto IPC 契约接入。

> **状态**:积极开发中,尚未通过发布认证。网关已迁入本仓库 `gateway/` 目录,
> 发布由本仓库以单一 tag 完成。

## 为什么选择 ScalaAPI

API 中转产品的生死取决于计费正确性。ScalaAPI 围绕一条铁律构建:
**PostgreSQL 是 business/资金状态的唯一权威**。其余一切——Orleans Actor、
Garnet 缓存、网关本身——都只是可重建的投影或不受信任的边缘。

- **恰好一次的结算**——每个可计费请求对应一个幂等租约,附带不可变价格快照
  与有界余额冻结。上游扣费状态未知时,会产生运维可见的对账工作,而不是静默丢失。
- **证据驱动的 dispatch**——网关必须先拿到持久的 `forwarded` 确认才能联系上游,
  并不晚于首次写客户端时记录 `output_started`。
- **边缘协议转换**——OpenAI Chat Completions / Responses、Anthropic Messages、
  Gemini generateContent(JSON 与 SSE),另有嵌入、音频(TTS/STT)、实时
  WebSocket 会话与媒体生成。
- **失败即关闭的内容策略**——在 pre-provider 与 pre-client 两个有界点求值,
  规则可审计,分类器宕机行为显式定义。
- **内建运维能力**——带异地验证的备份/恢复、provider 配额刷新、频道监控,
  以及源码构建的 smoke/stress/fault 门禁套件。

## 系统架构

```text
Clients
  │  product-native HTTP / SSE / WebSocket
  ▼
┌────────────────────────────────────────────────┐
│ Gateway (C++23, Photon coroutines)             │
│ protocol validation · translation · streaming  │
└────────────────────────────────────────────────┘
  │  Cap'n Proto RPC over a Unix domain socket
  ▼
┌────────────────────────────────────────────────┐
│ Platform (.NET 10, Orleans virtual actors)     │
│ identity · policy · scheduling · leases ·      │
│ pricing · accounting                           │
└────────────────────────────────────────────────┘
  ├──▶ PostgreSQL 17   持久的 business/账务权威
  ├──▶ Garnet          可重建的投影/缓存,永不为权威
  ├──▶ S3 (MinIO)      媒体与备份字节
  └──▶ Providers       目录、凭证、配额、推理
```

参考部署运行**两个平台 silo + 两个网关**;后台工作者(备份调度、provider 配额
刷新、频道监控、对账)通过 PostgreSQL claim 与 advisory lock 选出唯一 leader。
完整架构文档见 [docs/architecture.md](docs/architecture.md)。

## 仓库结构

```
src/
  Platform.Host/     Orleans silo + Cap'n Proto RPC host + 后台工作者
  Admin.Api/         管理/用户 HTTP API、备份恢复、first-admin 初始化
  Data/              持久化:记账、配额、备份、provider 状态
  Grains(.Interfaces)/ Orleans grain 契约与实现
  Security/          脱敏与证书跟踪
  Db.Migrator/       有序迁移执行器
  Provider.Mock/     确定性上游 provider mock(四种协议)
  ObjectStorage.FaultProxy/  对象存储故障注入代理
admin-web/           管理控制台(SolidJS)
user-web/            用户门户(SolidJS)
gateway/             C++23 边缘网关(树内 subtree,保留完整历史)
contracts/capnp/     权威 Cap'n Proto 契约(网关构建直接消费)
deploy/migrations/   绿地带 schema 迁移 001–068(无 054)
deploy/stack/        Compose 拓扑、smoke/stress/fault 门禁
test/                Host/Admin/Grains/Provider-Mock 测试套件及基准测试
```

## 技术栈

| 层次 | 技术 |
| --- | --- |
| 后端 | .NET 10、ASP.NET Core、Orleans(ADO.NET 集群/存储/Reminder) |
| 网关 | C++23、PhotonLibOS 协程、Cap'n Proto 1.0.2 |
| 数据 | PostgreSQL 17(权威)、Garnet(投影)、S3 兼容对象存储 |
| 前端 | SolidJS、Vite、Tailwind CSS、Playwright e2e |
| 契约 | Cap'n Proto schema,pinned 摘要与生成的绑定 |
| 发布 | 单 tag 发布、固定容器镜像、证据清单 |

## 快速开始

### 前置要求

- .NET 10 SDK
- Docker 或 Podman(含 Compose,用于完整栈)
- Cap'n Proto 1.0.2 工具链(仅在重新生成契约绑定时需要)

### 构建与测试

```sh
dotnet build
```

大多数测试依赖数据库:需要已迁移的 schema,连接串取自
`GREENFIELD_SCHEMA_CONNECTION`(未设置会直接失败):

```sh
export GREENFIELD_SCHEMA_CONNECTION="Host=localhost;Database=platform;Username=platform;Password=..."
dotnet test test/Host.Tests test/Admin.Tests
```

网关使用 CMake/CTest 在仓库根构建与测试:

```sh
cmake -S gateway -B gateway/build -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.5
cmake --build gateway/build -j"$(nproc)"
ctest --test-dir gateway/build --output-on-failure
```

### 运行完整栈

参考拓扑(PostgreSQL、Garnet、MinIO、provider mock、两个平台 silo、两个网关、
Admin API、管理/用户 Web)定义在
[deploy/stack/docker-compose.yml](deploy/stack/docker-compose.yml)。需要提供若干
环境变量(密钥、端口)——权威供给方式见
[deploy/stack/README.md](deploy/stack/README.md) 与 `smoke.sh`。变量导出后(或在
Compose 文件旁放置 `dev.env`):

```sh
deploy/stack/start.sh
```

该脚本自动探测容器运行时(Docker Compose 或 Podman Compose,可用
`CONTAINER_CLI` 覆盖),并从源码构建全部组件。

| 服务 | 地址 |
| --- | --- |
| 管理控制台 | `http://localhost:3000` |
| 用户门户 | `http://localhost:3001` |
| 网关 | `http://localhost:8080` |

生产部署通过 `GATEWAY_IMAGE`、`PLATFORM_SILO_IMAGE`、`ADMIN_API_IMAGE`、
`MIGRATOR_IMAGE`、`PROVIDER_MOCK_IMAGE` 固定发布镜像,例如
`GATEWAY_IMAGE=ghcr.io/gmorandi/scalaapi-platform/gateway:<tag>`(详见
[deploy/stack/README.md](deploy/stack/README.md))。

### 验证部署门禁

`deploy/stack/smoke.sh` 将一切从源码构建进一次性的 Compose 项目,并执行完整验收
契约:迁移(先全部 apply、再全部 skip)、chat 结算、幂等重放、实时会话、provider
故障矩阵、故障注入下的媒体存储,以及跨进程重启。
`deploy/stack/garnet_tls_smoke.sh` 以启用 Garnet TLS 的方式运行同一门禁。

## 契约纪律

`contracts/capnp/` 是网关↔平台契约的唯一权威副本;网关直接编译这些 schema
(`gateway/CMakeLists.txt` 引用 `../contracts/capnp`)。任何 schema 变更必须在同一
提交内更新 schemas、`SHA256SUMS`、生成的 C# 输出以及网关协议 fixtures。仓库根的
`scripts/verify-contracts.sh` 校验记录的摘要,
`scripts/verify-generated-contracts.sh` 用 pinned 编译器重新生成 C# 输出并逐字节
比对:

```sh
CAPNP_COMPILER=/path/to/capnp-1.0.2 scripts/verify-generated-contracts.sh
```

## 文档

- [docs/architecture.md](docs/architecture.md)——系统边界、所有权规则、可计费
  请求生命周期、持久数据规则、发布纪律
- [gateway/README.zh-CN.md](gateway/README.zh-CN.md)——网关构建、运行与环境变量
- [contracts/capnp/README.md](contracts/capnp/README.md)——契约布局与验证
- [deploy/stack/README.md](deploy/stack/README.md)——拓扑、环境供给与验收门禁
