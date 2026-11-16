namespace PiDock.Core;

public sealed record ApplicationStoragePolicy(long MinimumReserveBytes = 536870912, int ReservePercent = 5, long AdmissionBytes = 33554432)
{
    public void Validate()
    {
        if (MinimumReserveBytes is < 536870912 or > 1099511627776 || ReservePercent is < 5 or > 80 || AdmissionBytes is < 1048576 or > 1073741824)
            throw new ArgumentException("App storage requires at least 512 MiB and 5% reserve, plus a 1–1024 MiB admission allowance.");
    }
    public ApplicationStorageBudget Evaluate(long totalBytes, long freeBytes)
    {
        Validate();
        if (totalBytes <= 0 || freeBytes < 0 || freeBytes > totalBytes) throw new ArgumentException("Invalid app storage capacity measurement.");
        long reserve = Math.Max(MinimumReserveBytes, checked(totalBytes / 100 * ReservePercent + totalBytes % 100 * ReservePercent / 100));
        long available = Math.Max(0, freeBytes - reserve);
        return new(totalBytes, freeBytes, reserve, AdmissionBytes, available >= AdmissionBytes, available);
    }
}
public sealed record ApplicationStorageBudget(long TotalBytes, long FreeBytes, long ReserveBytes, long AdmissionBytes, bool CanAllocate, long AllocatableBytes);
public sealed record ApplicationStorageSupport(int SchemaVersion, bool Enabled, bool HardQuotas, string RetentionPolicy, string[] Limitations);
public sealed record ApplicationVolumeUsage(string AppId, long? UsedBytes, long? ContainerReferences, string Quality);
public sealed record ApplicationStorageStatus(DateTimeOffset ObservedAt, ApplicationStorageBudget? Capacity, ApplicationVolumeUsage[] Volumes);
