using GhProxy.Api.Data;
using GhProxy.Api.Domain;

namespace GhProxy.Api.Services;

public sealed class AuditService(AppDbContext db, IClock clock)
{
    public async Task WriteAsync(string eventType, string message, Guid? nodeId = null, CancellationToken cancellationToken = default)
    {
        db.AuditLogs.Add(new AuditLog
        {
            NodeId = nodeId,
            EventType = eventType,
            Message = message,
            Timestamp = clock.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
