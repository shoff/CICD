using Cicd.Core.Entities;
using Cicd.Core.Persistence;
using Cicd.Plugins.Sdk;
using Microsoft.EntityFrameworkCore;

namespace Cicd.Core.Triggers;

internal sealed class DbTriggerState(CicdDbContext db, Guid configurationId, int triggerIndex) : ITriggerState
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
        await db.TriggerState
            .Where(t => t.BuildConfigurationId == configurationId && t.TriggerIndex == triggerIndex && t.Key == key)
            .Select(t => t.Value)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        var entry = await db.TriggerState.FirstOrDefaultAsync(t => t.BuildConfigurationId == configurationId && t.TriggerIndex == triggerIndex && t.Key == key, cancellationToken);
        if (entry is null)
        {
            db.TriggerState.Add(new TriggerStateEntry { BuildConfigurationId = configurationId, TriggerIndex = triggerIndex, Key = key, Value = value });
        }
        else
        {
            entry.Value = value;
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
