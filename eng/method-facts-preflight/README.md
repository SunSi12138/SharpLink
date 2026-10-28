# Verify a network request's cancellation owner

This **non-timing diagnostic** blocks one real synchronous stream request, then looks up its interceptor request id in the server's live cancellation map. It checks the state's request id and business-argument token against the interceptor token, plus an untimed/no-state control. It runs TCP and SharedMemory. It never calls the performance campaign's service or timing loop.

Build against one frozen runtime arm and its matching generator (both absolute paths):

```sh
dotnet build eng/method-facts-preflight/Preflight.csproj -c Release \
  -p:ArmPath=/absolute/frozen-arm \
  -p:GeneratorPath=/absolute/SharpLink.Generator.dll /bl:preflight-{}.binlog
dotnet eng/method-facts-preflight/bin/Release/net10.0/Preflight.dll
```

Use separate source/output directories per ABI arm. Do not replace DLLs in a frozen evidence directory. The output identifies the actual server MVID and ABI and contains four assertion-checked rows.

The gate intentionally blocks synchronously. An async gate causes the normal dispatch continuation to allocate tracking state even for the untimed case, changing the state under inspection. The service's cancellation argument is captured before enumeration: a generated stream pump can pass a different linked lifetime token to `GetAsyncEnumerator`.
