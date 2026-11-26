using FlowLedger.Api.Middleware;
using FlowLedger.Infrastructure.DataSafety;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLedger.Api.Tests.Endpoints;

public sealed class AuditMiddlewareUnitTests
{
    private sealed class CapturingAudit : IAuditLog
    {
        public List<AuditEvent> Events { get; } = [];

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 31, 23, 59, 59, TimeSpan.Zero);
    }

    private static DefaultHttpContext AuditedContext()
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new EndpointNameMetadata("BackupNow")), "BackupNow"));
        context.Request.Path = "/api/data/backup-now";
        return context;
    }

    [Fact]
    public async Task A_throwing_handler_is_audited_as_500_and_the_exception_still_propagates()
    {
        var audit = new CapturingAudit();
        var middleware = new AuditMiddleware(_ => throw new InvalidOperationException("boom"), NullLogger<AuditMiddleware>.Instance);

        var act = () => middleware.InvokeAsync(AuditedContext(), audit, new FixedTime());

        await act.Should().ThrowAsync<InvalidOperationException>();
        audit.Events.Should().ContainSingle().Which.Should().Match<AuditEvent>(e => e.Action == "data.backup_now" && e.StatusCode == 500);
    }

    [Fact]
    public async Task A_failing_audit_write_never_breaks_the_request()
    {
        var middleware = new AuditMiddleware(_ => Task.CompletedTask, NullLogger<AuditMiddleware>.Instance);
        var act = () => middleware.InvokeAsync(AuditedContext(), new ThrowingAudit(), new FixedTime());
        await act.Should().NotThrowAsync();
    }

    private sealed class ThrowingAudit : IAuditLog
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default) => throw new InvalidOperationException("db down");
    }
}
