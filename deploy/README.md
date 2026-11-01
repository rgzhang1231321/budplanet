# MedBox 部署手册（腾讯云 CVM）

> ⚠️ **这份文档会进公开仓库**：不要在这里填真实服务器 IP、口令、密钥、AppSecret。
> 需要记真实值就另存 `deploy/README.local.md`（已在 `.gitignore` 里）。
> 下文所有 `<你的服务器公网IP>` 都是占位符，替换成自己的值再执行。

自建 .NET 10 API + PostgreSQL 18，全部跑在 Docker 里。**数据库 / 应用 / 反代三层分开编排**：
更新 API 的任何命令都不会碰数据库容器，也不会碰证书。

```
                    ┌──────────────── 腾讯云 CVM ────────────────┐
  微信小程序 ──443──▶│ Caddy(medbox-edge) ─▶ medbox-api:5080      │
                    │   自动签证书           │                    │
                    │                        │ medbox-net         │
                    │                        ▼                    │
                    │   127.0.0.1:5432 ◀── medbox-db(postgres:18) │
                    │                        │                    │
                    │              /data/medbox/pgdata            │
                    └─────────────────────────────────────────────┘
```

| 组件 | compose project | 容器名 | 说明 |
|---|---|---|---|
| 数据库 | `medbox-infra` | `medbox-db` | 只在首次创建；之后只 start，不 recreate |
| API | `medbox-api` | `medbox-api` | 每次更新 `--force-recreate`，与数据库 project 完全隔离 |
| HTTPS 反代 | `medbox-edge` | `medbox-caddy` | 用腾讯云签发的固定证书（不走 ACME）；只有换域名/换证书才重建 |

## 0. 文件一览

```
deploy/
  setup-host.sh          一次性：装 docker、配镜像加速、建 swap/目录/网络、生成 .env（可重复跑）
  infra-up.sh            只装/启动数据库（幂等，已存在就不动）
  migrate.sh             只跑 db/migrations 的 SQL（不动容器）
  api-up.sh              只替换 API 容器（--no-deps；小内存机器会拒绝在服务器构建）
  edge-up.sh             只更新 HTTPS 反代（改 Caddyfile 后热加载，不重建）
  deploy.sh              一键：infra → migrate → api → edge
  release.sh             【本地执行】构建镜像 → 传到服务器 → 只换 API（1G 机器走这条）
  status.sh              容器/内存/健康/HTTPS/磁盘/迁移账本
  logs.sh                看 api / db / edge 日志
  backup.sh              pg_dump 逻辑备份，保留 7 天
  push.sh                本地把代码同步到服务器（tar+ssh，不依赖 rsync）
  docker-compose.infra.yml   数据库（含 1G 小内存调优参数）
  docker-compose.api.yml     API
  docker-compose.edge.yml    Caddy
  Caddyfile                  HTTPS / 反代规则
  api.apiyun.net_nginx/      证书原地放这里：*_bundle.crt（白名单放行进 git）+ *.key（挡死）+ .csr（挡死）
  .env.example           变量模板（真实 .env 不进 git）
```

## 1. 服务器准备

**当前目标机**：2 核 1G（腾讯云 CVM，`<你的服务器公网IP>`），系统盘 38.7G。
1G 内存跑 PG + API + Caddy 是**紧但可行**的，前提是：
- 必须开 2G swap（`setup-host.sh` 会自动建）
- **镜像在本地构建**，不在服务器上 `docker build`（.NET SDK 编译单进程就要 1G+）
- **配日志轮转**（1G 机器不配，容器日志几天就能把 38G 盘写满）：

```bash
sudo tee /etc/docker/daemon.json >/dev/null <<'JSON'
{
  "registry-mirrors": [
    "https://mirror.ccs.tencentyun.com",
    "https://docker.m.daocloud.io"
  ],
  "log-driver": "json-file",
  "log-opts": { "max-size": "10m", "max-file": "3" },
  "live-restore": true
}
JSON
sudo systemctl restart docker
```

`live-restore: true` 让 docker 守护进程重启时容器不停机；日志按 10MB × 3 个文件轮转，单容器最多占 30MB。
- PG 参数已按 1G 收紧（见 `docker-compose.infra.yml` 的 `command`）

系统：Ubuntu 22.04/24.04、TencentOS Server 3.x。
> CentOS 7 已 EOL，docker 官方安装脚本不再支持，不建议。


**数据盘**（强烈建议单独挂一块云硬盘到 `/data`，数据库落在上面）：

```bash
lsblk                                  # 确认新盘，例如 /dev/vdb
mkfs.ext4 /dev/vdb
mkdir -p /data
blkid /dev/vdb                         # 取 UUID
echo 'UUID=<上面的UUID> /data ext4 defaults,nofail 0 2' >> /etc/fstab
mount -a && df -h /data
```

**安全组**：只放行
- `22`（来源填你自己的 IP）
- `80` / `443`（HTTPS 反代用）
- `5080` 仅临时自测时开，正式环境关掉

**不要**放行 5432 —— 数据库只绑 `127.0.0.1`。

## 2. 首次部署

### 2.1 上传代码（二选一）

```bash
# A. 从本地推送（无需服务器装 git / 配密钥）
MEDBOX_HOST=root@1.2.3.4 bash deploy/push.sh

# B. 服务器上直接 clone（公开仓库）
ssh root@1.2.3.4
git clone https://github.com/rgzhang1231321/budplanet.git /opt/medbox
```

### 2.2 初始化宿主机

```bash
cd /opt/medbox
bash deploy/setup-host.sh
```

做的事：装 docker + compose 插件 → 建 `/data/medbox/{pgdata,backup}` → 建网络 `medbox-net`
→ 生成 `deploy/.env`（48 位随机数据库口令 + JWT 秘钥，权限 600）。
已存在的东西一律跳过，可以放心重跑。

> 数据盘没挂上时脚本会明确警告：此时数据其实在系统盘，能跑但不建议。

### 2.3 起数据库（基础设施，只做这一次）

```bash
bash deploy/infra-up.sh
```

首次会创建 `medbox-db`，并把 `${DATA_ROOT}/pgdata` 挂进容器（数据实际在 `pgdata/data`）。
之后再执行这个脚本，输出是「已存在 —— 不做任何改动」，连 `docker compose up` 都不会调用。

### 2.4 建表 + 种子数据

```bash
bash deploy/migrate.sh
```

按版本顺序执行 `db/migrations/V001..V00N.sql`，`schema_migrations` 表记账 + 校验和防篡改；
已执行过的自动跳过。**只执行 SQL，不碰容器。**

### 2.5 起 API（1G 机器：在本地执行）

```bash
# 在你自己的电脑上（不是服务器）
MEDBOX_HOST=root@<你的服务器公网IP> bash deploy/release.sh
```

它做四件事：本地 `docker build` → `docker save | gzip | ssh docker load`（约 100MB）
→ 同步 `deploy/`、`db/` → 远程 `api-up.sh --use <tag>`（`--no-deps`，不碰数据库）。

直接在服务器上跑 `api-up.sh` 会被拦住并提示走这条路，因为 1G 内存编译必然 OOM。

### 2.6 起 HTTPS 反代

```bash
cd /opt/medbox && bash deploy/edge-up.sh
```

用**腾讯云签发的固定证书**（不是 Caddy 自动 ACME 签发，见 4.1），Caddy 只负责反代和加载证书。
私钥必须先放好，`edge-up.sh` 会先检查文件再启动，缺文件会直接报错并告诉你去哪拷。

前置条件缺一不可：域名 A 记录指向本机 → 安全组放行 80/443 → 域名已备案。

### 2.7 一条命令跑完

```bash
bash deploy/deploy.sh
```

内部仍然走上面的幂等脚本：**数据库已存在就只 start（或什么都不做）**，
反代只做配置热加载。注意 API 那一步在 1G 机器上会被拦下，仍需本地 `release.sh`。

## 3. 日常更新 API（重点）

1G 机器改完代码后，只做这一件事（本地执行）：

```bash
MEDBOX_HOST=root@<你的服务器公网IP> bash deploy/release.sh
# 只重发镜像、不同步代码：加 --no-sync
# 指定版本号：--tag v1.0.1
```

它只 load 镜像 + `api-up.sh --use`，**全程不调用任何 infra / edge 相关命令**。

改了 Caddyfile 或域名，才单独跑 `bash deploy/edge-up.sh`（热加载，不重建、证书不丢）。

为什么数据库一定不会被碰：

1. 三个 compose **project 名不同**（`medbox-api` / `medbox-infra` / `medbox-edge`），`up` 的作用域天然隔离；
2. API 的 `up` 带 `--no-deps`，不会顺带处理依赖服务；
3. `api-up.sh` 开头检查数据库是否在跑，**不在就报错退出**，不会"顺手帮你启动/重建"；
4. `infra-up.sh` 对已存在的容器只 `docker start`，永不 `compose up`。

## 4. HTTPS 证书与域名

### 4.1 证书：原地放、只挡 .key、不进仓库

证书**不搬家**，就放在 `deploy/api.apiyun.net_nginx/` 原地（搬位置要改一堆脚本引用，容易踩坑），
靠 `.gitignore` 把私钥挡死。四个文件的真实性质（`openssl` 只读查的，2026-11-01）：

| 文件 | 类型 / 主体 / 有效期 | 进 git？ | 为什么 |
|---|---|---|---|
| `api.apiyun.net_bundle.crt` | 证书链 **3 张**（叶子 `CN=api.apiyun.net` → 中间 `TrustAsia DV TLS RSA CA 2024` → 根 `TrustAsia TLS RSA Root CA`），**2026-11-01 → 2027-01-30** | ✅ 进 | 公开证书链，本来就是发给任何访客的东西，Caddy 要读它（Caddyfile 第 42/48 行） |
| `api.apiyun.net_bundle.pem` | 与 `.crt` **内容完全相同**（md5 一致 `b28896df…`） | ❌ 挡 | `.pem` 一律挡死：`.pem` 经常就是私钥，赌不起。Caddy 不用 `.pem`，挡掉不影响部署 |
| `api.apiyun.net.csr` | 签发请求（`O=qcloud`，含企业主体与序列号） | ❌ 挡 | 部署不需要，还带着主体信息 |
| `api.apiyun.net.key` | **RSA 2048 私钥，未加密（无口令）**，`openssl rsa -check` = ok（模数与上面叶子证书一致，配对 ✓） | ❌ **绝不** | 见下面"为什么要挡" |

`.gitignore` 的写法是**先全挡、再白名单放行**（新证书、改过名的证书、别人随手丢的临时私钥
全都默认进不来，只有明确放行的那一个能提交）：

```
*.key
*.pem
*.csr
*.crt
*.pfx
*.p12
!deploy/api.apiyun.net_nginx/api.apiyun.net_bundle.crt
```

**① 为什么私钥绝不能进仓库**
这是个**公开仓库**（GitHub 上任何人能读，爬虫和密钥扫描服务会主动抓）。
私钥 + 公开证书链凑齐 = 可以完整冒充 `api.apiyun.net`：做中间人、伪造小程序接口响应、
窃取用户数据与登录态。更贵的是——**私钥一旦进过 commit，后面删文件也没用**，历史还在，
只能重签证书止损。**本项目私钥从未进入任何 commit**（核实命令，全为空即从未入库）：

```bash
git log --all --oneline -- deploy/api.apiyun.net_nginx/        # 空
git log --all --diff-filter=A -- "*.key" "*.pem" "*.crt" "*.csr"  # 空
git rev-list --all | while read c; do git ls-tree -r --name-only "$c"; done \
  | grep -Ei '\.(key|pem|crt|csr|pfx)$'                        # 零命中
```

既然历史里没有，就**不需要重签证书**——只要保证从现在起它进不去。
（真进过历史的处理方式：必须重签，删文件无效。）

**② 换机器 / 新克隆的目录怎么把证书拿过去**
git 里只有公开的 `.crt`，私钥和 `.csr` 必须手工放一次：

```bash
# 从腾讯云 SSL 证书控制台下载「Nginx 版」，解压得到 4 个文件，全部拷进证书目录
scp api.apiyun.net_bundle.crt api.apiyun.net_bundle.pem \
    api.apiyun.net.csr api.apiyun.net.key \
    root@<服务器IP>:/opt/medbox/deploy/api.apiyun.net_nginx/
# 私钥权限收紧（未加密私钥，只有属主可读）
ssh root@<服务器IP> 'chmod 600 /opt/medbox/deploy/api.apiyun.net_nginx/api.apiyun.net.key'
```

> 用 `deploy/push.sh` / `release.sh` 推代码时，tar 会把整个 `deploy/`（含 `.key`）一起传上去，
> 私钥会自动到服务器；**但用 `git clone` 方式部署时不会有私钥**，必须按上面手工放。
> 缺私钥不用猜：`edge-up.sh` 会在启动容器之前就报错，并打印上面这段放法。

**③ 部署时证书在服务器上的哪个路径**
就是仓库里的相对位置，不需要额外拷到别处：

```
/opt/medbox/deploy/api.apiyun.net_nginx/api.apiyun.net_bundle.crt   ← Caddy 读这个
/opt/medbox/deploy/api.apiyun.net_nginx/api.apiyun.net.key          ← 和它配对，权限 600
```

`docker-compose.edge.yml` 把整个目录只读挂进容器：`./api.apiyun.net_nginx:/etc/caddy/certs:ro`，
所以容器内路径固定是 `/etc/caddy/certs/api.apiyun.net_bundle.crt` 与 `…/api.apiyun.net.key`
（Caddyfile 里写的就是这两个容器内路径）。

**到期怎么换**：腾讯云续签 → 4 个文件覆盖 `deploy/api.apiyun.net_nginx/` → 重跑
`bash deploy/edge-up.sh`（热加载，容器不用重建）。证书 3 个月一轮，忘了换会全站 502。

### 4.2 小程序域名登记

微信要求 `request` 域名：**HTTPS + 已备案 + 域名不能用 IP/localhost + 不配端口时请求 URL 也不能带端口**。

- 依据：微信官方文档《网络》 https://developers.weixin.qq.com/minigame/dev/guide/base-ability/network.html （查证日期 2026-11-01）
- `deploy/Caddyfile`：`{$DOMAIN}` → `reverse_proxy medbox-api:5080`
- 换域名：`deploy/.env` 改 `DOMAIN` → `bash deploy/edge-up.sh --recreate`

然后在小程序后台「开发管理 → 开发设置 → 服务器域名」把 `https://<DOMAIN>`
加进 **request 合法域名**。`miniprogram/config/index.ts` 里的 `PROD_API_BASE`
只在**正式版**（envVersion=release）生效，开发版/体验版仍连本地，
真机想连线上时控制台 `wx.setStorageSync('api_base','https://<DOMAIN>')` 覆盖。


## 5. 备份与恢复

```bash
bash deploy/backup.sh          # 生成 /data/medbox/backup/medbox-<时间>.dump
```

建议加 crontab（每天 3 点，保留 7 天，脚本自己清理）：

```
0 3 * * * cd /opt/medbox && bash deploy/backup.sh >> /var/log/medbox-backup.log 2>&1
```

恢复：

```bash
docker exec -i medbox-db pg_restore -U medbox -d medbox --clean --if-exists \
  < /data/medbox/backup/medbox-20261101-030000.dump
```

## 6. 回滚

镜像按 git sha 打 tag，直接切回旧镜像（不重新构建）：

```bash
docker images medbox-api                      # 找到上一个 tag
bash deploy/api-up.sh --use medbox-api:6d09011
```

数据库回滚不在脚本里做 —— 迁移是 append-only，需要回退时写新的 `V00N__fix_*.sql`。

## 7. 排查

```bash
bash deploy/status.sh          # 容器状态、健康、磁盘、迁移账本
bash deploy/logs.sh api 200    # API 最近 200 行
bash deploy/logs.sh db 100     # 数据库日志
```

| 现象 | 原因 / 处理 |
|---|---|
| `JWT__SECRET 未配置` 启动即崩 | `.env` 没生成或为空 → 重跑 `setup-host.sh`；上线后改这个值会让所有旧 token 失效 |
| `WX__SECRET 未配置` 启动即崩（容器起来就 exit 1） | `.env` 的 `WX__SECRET=` 是空的。生产强制要求（否则所有用户塌成同一个 openid）→ 填小程序 AppSecret。`api-up.sh` 会在构建前就拦住，不会白等健康检查 |
| API 起不来，日志 `Connection refused medbox-db` | 数据库没起 → `bash deploy/infra-up.sh`（它只 start，不重建） |
| `502` / 连接超时 | 安全组没开 443/80，或 Nginx 没起来 |
| 手机真机连不上、开发者工具正常 | 小程序要求 HTTPS + 备案域名；临时自测可把 `.env` 的 `API_BIND` 改 `0.0.0.0:5080` 并在工具里勾「不校验合法域名」 |
| 磁盘涨得快 | `du -sh /data/medbox/pgdata`；`app_logs` 由 ArchiveWorker 保留 30 天，备份保留 7 天 |

## 8. 与本地开发的差异

- `db/apply.sh` 现在支持 `CONTAINER` / `PGUSER` / `DB` 环境变量覆盖，本地默认值（`tigerrag-postgres-1` / `tigerrag` / `medbox`）保持不变。
- 生产连接串由 compose 注入（`Host=medbox-db`），不读 `appsettings.json` 里的本地串。
- 生产 `ASPNETCORE_ENVIRONMENT=Production`：JWT 秘钥必须显式注入，否则直接拒绝启动（这是有意为之）。
