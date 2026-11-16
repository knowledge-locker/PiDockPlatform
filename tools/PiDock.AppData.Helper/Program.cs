using PiDock.Core;
try
{
    if(args.Length!=2||args[0] is not("apply" or "finish")||!Guid.TryParseExact(args[1],"N",out var operation))throw new ArgumentException("Invalid helper operation.");
    ApplicationDataRestore.Apply("/data",operation,args[0]=="finish");return 0;
}
catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException)
{Console.Error.WriteLine("App data operation failed; restore remains paused.");return 1;}
