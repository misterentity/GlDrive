using GlDrive.Services.Operations;

namespace GlDrive.Downloads;

public partial class WishlistStore
{
    // Does not initialize, normalize, save or claim anything in the live store.
    internal (IReadOnlyList<WishlistItem> Items, bool Partial) ReadDiagnosticSnapshot()
    {
        lock (_state.Sync)
        {
            if (!_state.Loaded) return ([], true);
            var rows = new List<WishlistItem>();
            var budget = 512 * 1024;
            var partial = _state.Items.Count > 128;
            foreach (var item in _state.Items.Take(128))
            {
                var copy = DossierWishlistSnapshot.Copy(item, ref budget);
                if (copy == null) partial = true; else rows.Add(copy);
            }
            return (rows, partial);
        }
    }
}
