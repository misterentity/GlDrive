using System.Text.Json;
using GlDrive.Config;
using GlDrive.Downloads;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class WishlistPruneValidator : IChangeValidator
{
    public string Category => AgentCategories.WishlistPrune;

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        // target: /wishlist/items/{id}
        const string prefix = "/wishlist/items/";
        if (!change.Target.StartsWith(prefix))
            return new(false, "target-shape-unsupported", null);
        var id = change.Target[prefix.Length..];
        if (string.IsNullOrWhiteSpace(id))
            return new(false, "missing-id", null);

        bool hardRemove = change.After is null;
        bool softMark = false;

        if (change.After is not null)
        {
            try
            {
                var afterJson = JsonSerializer.Serialize(change.After);
                using var doc = JsonDocument.Parse(afterJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("dead", out var dead)
                    && dead.ValueKind == JsonValueKind.True)
                    softMark = true;
            }
            catch { return new(false, "after-parse-failed", null); }
        }

        if (!hardRemove && !softMark)
            return new(false, "action-unclear", null);

        return new(true, null, _ =>
        {
            try
            {
                var store = new WishlistStore();
                store.Load();
                var item = store.GetById(id);
                if (item is null) return;

                if (hardRemove)
                {
                    store.Remove(id);
                }
                else
                {
                    item.Dead = true;
                    store.Update(item);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WishlistPrune mutation failed for id={Id}", id);
            }
        });
    }
}
