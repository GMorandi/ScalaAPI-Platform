# ScalaAPI Platform(平台)

[English](README.md) | **简体中文**

[ScalaAPI](https://github.com/GMorandi/ScalaAPI) LLM API 平台的 business 与资金
权威:一个 .NET 10 服务集群,掌管账号、凭证、路由、配额、租约、结算、计费与运维,
由 C++ 网关通过 Cap'n Proto IPC 契约接入。

> 状态:积极开发中,尚未通过发布认证。请仅作为
> [ScalaAPI 超项目](https://github.com/GMorandi/ScalaAPI)配对发布的一部分部署。

## 架构一览

- **PostgreSQL** 是唯一的 business/资金权威(68 个有序绿地带迁移,不导入历史)。
- **Orleans** 虚拟 Actor 在平台 silo 内协调聚合执行。
- **Garnet** 承载可重建的投影/缓存(目录、配置)。
- **S3 兼容存储(MinIO)** 掌管媒体与备份字节。
- **Cap'n Proto RPC** 经 Unix 域套接字服务网关 dispatch:请求租约、余额冻结、
  用量结算、中止、内容策略,以及大请求体的分块 blob 上传。
- 栈运行两个 silo + 两个网关;后台工作者(备份调度、provider 配额刷新、频道监控、
  对账)通过数据库 claim 与 advisory lock 选出唯一 leader。

完整文档见 [docs/architecture.md](docs/architecture.md)。

## 目录结构

```
src/
  Platform.Host/     Orleans silo + Cap'n Proto RPC host + 后台工作者
  Admin.Api/         管理/用户 HTTP API、备份恢复、first-admin 初始化
  Data/              持久化:记账、配额、备份、provider 状态
  Grains(.Interfaces)/ Orleans grain 契约与实现
  Security/          加密、JWT、脱敏、主密钥操作
  Db.Migrator/       有序迁移执行器
  Provider.Mock/     确定性上游 provider mock(四种协议)
  ObjectStorage.FaultProxy/  对象存储故障注入代理
  admin-web/         管理控制台(Vue)
  user-web/          用户门户(Vue)
contracts/capnp/     权威 Cap'n Proto 契约(网关仓库持有字节一致的副本)
deploy/migrations/   001–068 绿地带 schema 迁移
deploy/stack/        Compose 拓扑、smoke/stress/fault 门禁
test/                Host/Admin/Grains/Provider-Mock 测试套件
```

## 构建与测试

需要 .NET 10 SDK。

```sh
dotnet build
dotnet test test/Host.Tests            # 单元测试无需数据库
```

数据库测试需要已迁移的 schema,连接串取自 `GREENFIELD_SCHEMA_CONNECTION`:

```sh
export GREENFIELD_SCHEMA_CONNECTION="Host=localhost;Database=platform;Username=platform;Password=..."
dotnet test test/Host.Tests test/Admin.Tests
```

生成的 Cap'n Proto C# 输出必须与 pinned 编译器完全一致:

```sh
CAPNP_COMPILER=/path/to/capnp-1.0.2 scripts/verify-generated-contracts.sh
```

## 运行完整栈

参考拓扑(PostgreSQL、Garnet、MinIO、provider mock、两个平台 silo、两个网关、
管理/用户 Web)定义在
[deploy/stack/docker-compose.yml](deploy/stack/docker-compose.yml)。需要提供若干
环境变量(密钥、端口)——权威供给方式见
[deploy/stack/README.md](deploy/stack/README.md) 与 `smoke.sh`。变量导出后:

```sh
docker compose -p scalaapi-dev --env-file dev.env -f deploy/stack/docker-compose.yml up -d --build
```

管理控制台:`http://localhost:3000` · 用户门户:`http://localhost:3001` ·
网关:`http://localhost:8080`。

## 契约纪律

`contracts/capnp/` 为权威;网关仓库持有字节一致的副本,超项目的
`validate-pair.sh` 会拒绝漂移的配对。schema 变更必须是跨两个仓库的原子配对变更。
