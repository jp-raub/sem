FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
# Restore must see the Razor sources: the SDK derives implicit Blazor web assets
# during restore. Restoring the csproj alone omits framework JavaScript.
COPY src/Sem.Web/ src/Sem.Web/
RUN dotnet restore src/Sem.Web/Sem.Web.csproj
RUN dotnet publish src/Sem.Web/Sem.Web.csproj -c Release --no-restore -o /out
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/ ./
RUN mkdir -p /data/keys && chown -R app:app /data
ENV ASPNETCORE_HTTP_PORTS=8080
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Sem.Web.dll"]
