using System.Security.Cryptography;
using PiDock.Core;
namespace PiDock.Cloud.Web;

public static class EnrollmentEndpoints
{
    public static void MapDeviceEnrollment(this WebApplication app)
    {
        app.MapPost("/api/enrollment/redeem", (HttpContext context, EnrollmentRequest request, EnrollmentStore store) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps) return Results.BadRequest(new { error = "HTTPS required." });
            try
            {
                var result = store.Redeem(request);
                return result is null ? Results.BadRequest(new { error = "Invalid, expired or already used enrollment code." }) : Results.Ok(result);
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException) { return Results.BadRequest(new { error = "Invalid device request." }); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            { return Results.Problem("Enrollment is temporarily unavailable. Retry with the same device.", statusCode: 503); }
        }).AllowAnonymous().RequireRateLimiting("enrollment");
    }
}
