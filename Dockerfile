# ---- Stage 1: Build ----
# Full SDK image (large) — used only to compile; never shipped.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy just the project file first so Docker caches "dotnet restore" and only
# re-runs it when dependencies actually change, not on every code edit.
COPY src/GoRide.Trip/GoRide.Trip.csproj src/GoRide.Trip/
RUN dotnet restore src/GoRide.Trip/GoRide.Trip.csproj

COPY src/GoRide.Trip/ src/GoRide.Trip/
WORKDIR /src/src/GoRide.Trip
RUN dotnet publish -c Release -o /app/publish --no-restore

# ---- Stage 2: Run ----
# Small runtime-only image — no SDK, no source code, no build tools.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "GoRide.Trip.dll"]
