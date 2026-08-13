# 安全

SharpLink 把传输机密性、连接认证和逐调用授权分开。生产部署通常同时使用 TLS 与认证；只启用认证不会加密凭据。

## Client 凭据

`ISharpLinkClientAuthenticator.CreatePayloadAsync` 为每次物理连接尝试返回一个有界 opaque payload。回调可读取短期 token，但不得记录 token 或长期缓存已过期凭据。

```csharp
builder.UseAuthenticator(SharpLinkAuthenticator.CreateClient(
    cancellationToken => ValueTask.FromResult<ReadOnlyMemory<byte>>(tokenBytes)));
```

payload 与 metadata 共享独立的配置上限概念，默认认证 payload 上限由 `MaxMetadataBytes` 的 16 KiB 默认值约束。凭据过大或 provider 失败会终止握手，不会进入业务协议。

## Server 认证

```csharp
serverBuilder
    .UseAuthenticator(SharpLinkAuthenticator.CreateServer((request, cancellationToken) =>
        ValueTask.FromResult(Validate(request.Payload))))
    .RequireAuthentication();
```

若调用 `RequireAuthentication` 却未注册 provider，`Build()` 立即失败。provider 返回：

- `Authenticate(context)`：成功并保存 immutable identity context。
- `Success`：成功但没有 identity；不适合后续授权。
- `Reject(code, message)`：使用具体错误 code 拒绝。不要把内部异常、SQL、证书或 token 内容放入 peer-facing message。

认证 context 会规范化 scope、按 ordinal 比较 claim/tenant，并可带 `ExpiresAt`。它在连接握手时建立；长连接不会自动刷新 token，过期策略应结合连接期限或服务端逐调用检查。

## 服务端授权

服务实现或 Server Interceptor 可使用：

- `SharpLinkAuthorization.GetRequiredAuthentication()`
- `RequireActiveToken()`
- `RequireScope("orders.read")`
- `RequireTenant("tenant-a")`

失败分别映射到 `AuthenticationRejected`、`AuthenticationExpired` 或 `AuthorizationDenied`。授权必须在业务副作用前完成。`demo/Security` 同时证明 token、subject、tenant、scope 和 expiry 检查。

## TLS

TCP TLS 使用 .NET `SslClientAuthenticationOptions`/`SslServerAuthenticationOptions`。默认客户端证书验证保持启用。双向 TLS 由标准选项配置；SharpLink authentication payload 可在 mTLS 之上承载应用身份，但不要重复信任未经绑定的两个身份源。

TLS handshake timeout 与 RPC handshake timeout 独立。前者保护证书/加密协商，后者保护 SharpLink capability/authentication 协商。

`UseTcp(port)` 默认只绑定 loopback。需要监听其他网卡时，先显式调用
`ListenOnAnyAddress()` 或 `ListenOn(IPAddress)`；非 loopback 的明文 TCP 会被 `Build()` 拒绝，
必须通过 `AllowUnencrypted()` opt-in。不要把这类扩大暴露范围、降低传输保护
的配置隐藏在默认参数中。

## 日志与遥测安全

- AnonymousPipe handles、authentication payload、原始 token 不得记录。
- endpoint attributes、metadata、claim 和异常消息进入日志/Activity 前做 allowlist 和低基数处理。
- 默认业务异常只返回 `Internal` 安全消息；只有明确 `EnableDetailedErrors` 才包含服务异常详情，生产默认不要启用。
- 自定义 `IRpcExceptionMapper` 必须返回具体、已定义且非 `Unknown` 的 code。
