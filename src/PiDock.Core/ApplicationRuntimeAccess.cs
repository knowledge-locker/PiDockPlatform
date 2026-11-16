namespace PiDock.Core;

public sealed record ApplicationRuntimeAccessSupport(int SchemaVersion, bool Enabled, string BindingHeader,
    int ObservationSeconds, string[] Limitations);
// Private broker/host transport. DeliveryId is startup authority: never publish in discovery, schemas, frames or logs.
public sealed record ApplicationRuntimePermission(string AppId, Guid? DeliveryId, bool Allowed, string Reason);
public sealed record ApplicationRuntimeAccessSnapshot(Guid CoordinatorId, bool Paused, ApplicationRuntimePermission[] Apps);
// Internal host binding, not an app response DTO.
public sealed record ApplicationRuntimeBinding(Guid DeliveryId, Guid AdmissionId);
public static class ApplicationRuntimeAccess
{
    public const string Header = "PiDock-Runtime-Delivery";
    public const int ObservationSeconds = 3;
    public static void Bind(HttpClient client, ApplicationBootstrapPacket packet) => Bind(client, packet.DeliveryId);
    public static void Bind(HttpClient client, Guid deliveryId)
    {
        if (deliveryId == Guid.Empty) throw new ArgumentException("Current private startup identity required.");
        client.DefaultRequestHeaders.Remove(Header);
        client.DefaultRequestHeaders.Add(Header, deliveryId.ToString("N"));
    }
}
