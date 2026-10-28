namespace GlDrive.Downloads;

public partial class WishlistStore
{
    /// <summary>Patch only explicitly selected fields, preserving concurrent ownership and acquisition state.</summary>
    public int UpdateMany(IEnumerable<string> ids, QualityProfile? quality = null, WishlistStatus? status = null)
    {
        if (quality.HasValue && !Enum.IsDefined(quality.Value) || status.HasValue && !Enum.IsDefined(status.Value))
            throw new ArgumentException("Invalid wishlist quality or status");
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        int changed = 0;
        lock (_state.Sync)
        {
            EnsureLoaded();
            foreach (var item in _state.Items.Where(item => selected.Contains(item.Id)))
            {
                if ((!quality.HasValue || item.Quality == quality) && (!status.HasValue || item.Status == status)) continue;
                if (quality.HasValue)
                {
                    if (item.ProfileId == null || item.ProfileId == ScoredQualityProfile.Legacy(item.Quality).Id)
                        item.ProfileId = ScoredQualityProfile.Legacy(quality.Value).Id;
                    item.Quality = quality.Value;
                }
                if (status.HasValue) item.Status = status.Value;
                _state.ReleaseEvaluations.ForgetItem(item.Id);
                changed++;
            }
            if (changed > 0) Save();
        }
        if (changed > 0) _state.NotifyChanged();
        return changed;
    }

    /// <summary>Remove selected identities in one save, retaining imported identities as inactive tombstones.</summary>
    public int RemoveMany(IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        int removed;
        lock (_state.Sync)
        {
            EnsureLoaded();
            var items = _state.Items.Where(item => selected.Contains(item.Id)).ToArray();
            removed = items.Length;
            foreach (var item in items)
            {
                if (item.Source == GlDrive.Plex.PlexWatchlistSync.Source) item.Dead = true;
                else _state.Items.Remove(item);
                _state.ReleaseEvaluations.ForgetItem(item.Id);
            }
            if (removed > 0) Save();
        }
        if (removed > 0) _state.NotifyChanged();
        return removed;
    }
}
