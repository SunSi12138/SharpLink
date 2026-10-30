using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    public async Task CooperativeMaintenanceCertificateStorageDiagnostic()
    {
        using var original = CreateMaintenanceCertificate();
        var pkcs12 = original.Export(X509ContentType.Pkcs12);
        X509Certificate2 reloaded;
        try { reloaded = X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.DefaultKeySet); }
        finally { CryptographicOperations.ZeroMemory(pkcs12); }
        using (reloaded)
        {
            Ensure(original.RawData.AsSpan().SequenceEqual(reloaded.RawData),
                "the certificate identity, validity, extensions and public key must be identical");
            List<Exception> failures = [];
            foreach (var (variant, certificate) in new[] { ("original", original), ("pkcs12-default", reloaded) })
            {
                Console.Error.WriteLine($"MAINTENANCE_CERTIFICATE_VARIANT_BEGIN {variant}");
                try
                {
                    await using var harness = await MaintenanceHarness.CreateAsync(certificate: certificate);
                    await harness.VerifyIdentityAndShapesAsync();
                    var result = await harness.Begin().WaitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    Ensure(result.Stage == SharpLinkMaintenanceStage.Succeeded, $"maintenance success: {result.Failure}");
                    await harness.VerifyIdentityAndShapesAsync();
                    Console.Error.WriteLine($"MAINTENANCE_CERTIFICATE_VARIANT_RESULT {variant} PASS four-same-TLS-sessions-maintenance-and-RPC");
                }
                catch (Exception failure)
                {
                    Console.Error.WriteLine($"MAINTENANCE_CERTIFICATE_VARIANT_RESULT {variant} FAIL {failure}");
                    failures.Add(failure);
                }
            }
            // Diagnostic observation never converts the original failure into a pass.
            if (failures.Count != 0) throw new AggregateException(failures);
        }
    }

    // Preserve the server's original handshake exception rather than diagnosing the
    // client-side EOF that results when the rejected transport is closed.
    private sealed class MaintenanceDiagnosticLoggerFactory : ILoggerFactory
    {
        internal static readonly MaintenanceDiagnosticLoggerFactory Instance = new();
        public ILogger CreateLogger(string categoryName) => new DiagnosticLogger(categoryName);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class DiagnosticLogger(string categoryName) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                Console.Error.WriteLine($"MAINTENANCE_SERVER {categoryName} {eventId}: {formatter(state, exception)}");
                if (exception is not null) Console.Error.WriteLine(exception.ToString());
            }
        }
    }
}
