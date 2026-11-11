FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src ./src
ARG PROJECT=PiDock.Cloud.Web
RUN dotnet restore src/${PROJECT}/${PROJECT}.csproj --locked-mode
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM build AS tests
COPY PiDock.sln ./
COPY tests ./tests
COPY tools ./tools
RUN dotnet restore PiDock.sln --locked-mode
RUN dotnet test PiDock.sln --no-restore --configuration Release

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e
WORKDIR /app
COPY --from=build /out .
ARG WEB_URLS=https://+:8443
ARG WEB_CERTIFICATE_PATH=/certs/web.crt
ARG WEB_CERTIFICATE_KEY=/certs/web.key
ENV ASPNETCORE_URLS=${WEB_URLS} \
    ASPNETCORE_ENVIRONMENT=Production \
    PiDock__StateDirectory=/var/lib/pidock \
    ASPNETCORE_Kestrel__Certificates__Default__Path=${WEB_CERTIFICATE_PATH} \
    ASPNETCORE_Kestrel__Certificates__Default__KeyPath=${WEB_CERTIFICATE_KEY}
USER $APP_UID
EXPOSE 8443 8080
ENTRYPOINT ["dotnet", "PiDock.Cloud.Web.dll"]
