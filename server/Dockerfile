# syntax=docker/dockerfile:1.7
#
# MedBox API 生产镜像（多阶段构建，构建上下文 = server/）
#   docker build -t medbox-api:dev -f server/Dockerfile server/
#
# 注意：不复制 global.json —— 它把 SDK 精确锁在 10.0.402（rollForward=disable），
# 而官方 sdk:10.0 镜像里的补丁号会随镜像更新漂移，复制进去会直接把构建卡死。
# SDK 版本只影响编译工具链，不影响产物行为，所以这里交给镜像自带的 10.0.x。
#
# 基础镜像 tag 策略：钉到 10.0（主.次），**不用 latest**。
# 不钉到补丁号（如 10.0.4xx）是故意的：补丁号 tag 需要人工跟版，忘了跟就永远拿不到安全更新；
# 主.次 tag 每次构建自动带最新补丁。需要完全可复现时再改成 digest（见部署文档「支线待办」）。

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先只复制工程文件做 restore：源码改动不会让 NuGet 层失效
COPY Directory.Build.props Directory.Packages.props ./
COPY src/MedBox.Domain/MedBox.Domain.csproj src/MedBox.Domain/
COPY src/MedBox.Application/MedBox.Application.csproj src/MedBox.Application/
COPY src/MedBox.Infrastructure/MedBox.Infrastructure.csproj src/MedBox.Infrastructure/
COPY src/MedBox.Api/MedBox.Api.csproj src/MedBox.Api/
RUN dotnet restore src/MedBox.Api/MedBox.Api.csproj

COPY src/ src/
RUN dotnet publish src/MedBox.Api/MedBox.Api.csproj \
      -c Release -o /out --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# 端口跟 appsettings.json 的 "Urls" 保持一致（5080）：
# ASP.NET Core 的 urls 配置键优先级高于 ASPNETCORE_URLS，硬改成 8080 会被它覆盖，
# 容器里实际监听 5080、映射 8080 就成了"端口通但没服务"。统一 5080 最省心。
ENV ASPNETCORE_URLS=http://+:5080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    TZ=Asia/Shanghai

# Npgsql 启动时会探测 GSSAPI（Kerberos）：slim 镜像缺 libgssapi-krb5 会在日志里刷一行
# "Cannot load library libgssapi_krb5.so.2"。我们用密码认证，不装这个库也能正常连库；
# 装它需要 apt 访问外网源，会让构建在受限网络/内网环境下失败，不值得。
COPY --from=build /out ./

# 非 root 运行（aspnet 镜像内置 APP_UID = 1654）
USER $APP_UID
EXPOSE 5080

# 健康检查：aspnet 精简镜像里没有 curl / wget（实测 Ubuntu 24.04 基础镜像两者都没有），
# 装 curl 要 apt 访问外网源，会把构建卡在受限网络里 → 用 bash 的 /dev/tcp 手写一个裸 HTTP 请求。
# 判定标准与 deploy/lib.sh 的 api_health()、status.sh 完全一致：GET /health 必须回 200。
# 已验证三种情形（正例 200 / 连接被拒 / 返回 404 → 依次退出 0 / 1 / 1）。
HEALTHCHECK --interval=30s --timeout=5s --start-period=25s --retries=3 \
    CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/5080 || exit 1; printf "GET /health HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n" >&3; head -n1 <&3 | grep -q " 200"'

ENTRYPOINT ["dotnet", "MedBox.Api.dll"]
