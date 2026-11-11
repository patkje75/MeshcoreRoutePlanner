## Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY MeshRoutePlanner.sln ./
COPY MeshRoutePlanner.Core/MeshRoutePlanner.Core.csproj MeshRoutePlanner.Core/
COPY MeshRoutePlanner.Web/MeshRoutePlanner.Web.csproj MeshRoutePlanner.Web/
RUN dotnet restore MeshRoutePlanner.sln

COPY MeshRoutePlanner.Core/ MeshRoutePlanner.Core/
COPY MeshRoutePlanner.Web/ MeshRoutePlanner.Web/
RUN dotnet publish MeshRoutePlanner.Web/MeshRoutePlanner.Web.csproj -c Release -o /app --no-restore

## Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "MeshRoutePlanner.Web.dll"]
