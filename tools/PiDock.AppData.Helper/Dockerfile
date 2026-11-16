FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish tools/PiDock.AppData.Helper/PiDock.AppData.Helper.csproj -c Release -o /out
FROM mcr.microsoft.com/dotnet/runtime:10.0
COPY --from=build /out /app
USER 1654:1654
ENTRYPOINT ["dotnet", "/app/PiDock.AppData.Helper.dll"]
