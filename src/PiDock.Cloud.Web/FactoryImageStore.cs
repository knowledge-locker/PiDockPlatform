using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using PiDock.Core;
using PiDock.Web.Shared;
namespace PiDock.Cloud.Web;

public sealed record PublishedFactory(string Tenant, FactoryImageInfo Image, string Actor, DateTimeOffset At);
public sealed record FactoryAudit(string Tenant, string Actor, string Action, string Digest, DateTimeOffset At);
public sealed record FactoryCatalog(PublishedFactory[] Images, FactoryAudit[] Audit);
public sealed class FactoryImageStore
{
    private readonly object gate = new();
    private readonly SemaphoreSlim uploads = new(1,1);
    private readonly string root, path, trust;
    private readonly FleetStore fleet;
    private readonly TimeProvider clock;
    private FactoryCatalog catalog;
    private readonly Dictionary<string,(string User,string Tenant,string Digest,DateTimeOffset Until)> tickets = [];
    public FactoryImageStore(StateDirectory directory, FleetStore fleet, TimeProvider clock, IConfiguration settings)
    {
        this.fleet=fleet;this.clock=clock;
        root=StateFiles.PrepareDirectory(Path.Combine(directory.Path,"factory-images"));path=Path.Combine(root,"catalog.json");
        trust=settings["Updates:TrustDirectory"]??"/etc/pidock/update-trust";
        catalog=File.Exists(path)?JsonSerializer.Deserialize<FactoryCatalog>(File.ReadAllText(path),StateFiles.Json)??throw new IOException("Invalid factory catalog."):new([],[]);
    }
    private string Tenant(ClaimsPrincipal user) => fleet.HasAccess(user)&&EnrollmentStore.CanManage(user)&&IdentityRules.IsId(user.FindFirstValue("tenant")) ? user.FindFirstValue("tenant")! : throw new UnauthorizedAccessException();
    public PublishedFactory[] List(ClaimsPrincipal user) { var tenant=Tenant(user);lock(gate)return catalog.Images.Where(x=>x.Tenant==tenant).OrderByDescending(x=>x.At).ToArray(); }
    private void Save(FactoryCatalog value) { StateFiles.WriteAtomic(path,JsonSerializer.Serialize(value,StateFiles.Json));catalog=value; }
    private FactoryCatalog Audit(FactoryCatalog value,ClaimsPrincipal user,string action,string digest) => value with { Audit=value.Audit.Append(new(Tenant(user),user.Identity!.Name!,action,digest,clock.GetUtcNow())).TakeLast(1000).ToArray() };
    public async Task Publish(ClaimsPrincipal user,Stream input,CancellationToken ct=default)
    {
        var tenant=Tenant(user);
        if(!await uploads.WaitAsync(0,ct))throw new InvalidOperationException("Another factory upload is running.");
        string incoming=Path.Combine(root,"incoming.zip"), compressed=Path.Combine(root,"incoming.gz");
        try {
            lock(gate)if(catalog.Images.Length>=32||new DirectoryInfo(root).GetFiles("*.gz").Sum(f=>f.Length)>(8L<<30)-FactoryImages.MaximumCompressed)throw new IOException("Factory catalog quota reached.");
            if(new PiDock.Agent.DriveCapacityProbe().Read(root,0).FreeBytes<(1L<<30)+FactoryImages.MaximumCompressed*2)throw new IOException("Factory storage reserve reached.");
            await using(var output=new FileStream(incoming,FileMode.Create,FileAccess.Write,FileShare.None)) {
                var buffer=new byte[1<<20];long length=0;int count;
                while((count=await input.ReadAsync(buffer,ct))>0) { length+=count;if(length>FactoryImages.MaximumCompressed)throw new InvalidDataException("Factory package too large.");await output.WriteAsync(buffer.AsMemory(0,count),ct); }
                output.Flush(true);
            }
            File.Delete(compressed);
            var image=await Task.Run(()=>FactoryImages.Verify(incoming,trust,compressed),ct);
            lock(gate) {
                Tenant(user);
                var previous=catalog.Images.FirstOrDefault(x=>x.Tenant==tenant&&x.Image.Manifest.Version==image.Manifest.Version);
                if(previous is not null) { if(previous.Image.Digest!=image.Digest)throw new InvalidOperationException("Factory versions are immutable.");return; }
                var destination=Path.Combine(root,image.Digest+".gz");if(!File.Exists(destination))File.Move(compressed,destination);
                Save(Audit(catalog with { Images=[..catalog.Images,new(tenant,image,user.Identity!.Name!,clock.GetUtcNow())] },user,"Published",image.Digest));
            }
        } finally { File.Delete(incoming);File.Delete(compressed);uploads.Release(); }
    }
    // Called only after the shared action-specific MFA dialog succeeds. Tickets cannot move between users/workspaces.
    public string AuthorizeDownload(ClaimsPrincipal user,string digest)
    {
        var tenant=Tenant(user);lock(gate) {
            if(!catalog.Images.Any(x=>x.Tenant==tenant&&x.Image.Digest==digest))throw new UnauthorizedAccessException();
            foreach(var expired in tickets.Where(x=>x.Value.Until<=clock.GetUtcNow()).Select(x=>x.Key).ToArray())tickets.Remove(expired);
            if(tickets.Count>=128)throw new InvalidOperationException("Too many image downloads.");
            var token=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));tickets[token]=(user.Identity!.Name!,tenant,digest,clock.GetUtcNow().AddMinutes(5));
            Save(Audit(catalog,user,"DownloadAuthorized",digest));return token;
        }
    }
    public string? Download(ClaimsPrincipal user,string digest,string token)
    {
        var tenant=Tenant(user);lock(gate) {
            if(!tickets.TryGetValue(token,out var ticket)||ticket.Until<=clock.GetUtcNow()||ticket.User!=user.Identity!.Name||ticket.Tenant!=tenant||ticket.Digest!=digest)return null;
            tickets.Remove(token);
            var file=Path.Combine(root,digest+".gz");return File.Exists(file)&&!File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)?file:null;
        }
    }
}
public static class FactoryImageEndpoints
{
    public static void MapFactoryImages(this WebApplication app) => app.MapGet("/api/factory-images/{digest}",(string digest,HttpContext context,FactoryImageStore store,UserStore users)=> {
        context.Response.Headers.CacheControl="no-store";
        if(!users.IsCurrent(context.User)||!OsUpdateRules.Digest(digest))return Results.Unauthorized();
        try { var path=store.Download(context.User,digest,context.Request.Headers["X-PiDock-Download"].ToString());return path is null?Results.NotFound():Results.File(path,"application/gzip"); }
        catch(UnauthorizedAccessException) { return Results.Forbid(); }
    }).RequireAuthorization();
}
