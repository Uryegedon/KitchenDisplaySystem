# Dockerfile for .NET 8.0 Application
# This Dockerfile fixes the "dotnet: command not found" error on Render
# Use with Root Directory: SelfOrderingSystemKiosk, Dockerfile Path: Dockerfile

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj file and restore dependencies
COPY *.csproj ./
RUN dotnet restore

# Copy everything else and build
COPY . ./
RUN dotnet publish -c Release -o /app/publish

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Copy published app from build stage
COPY --from=build /app/publish .

# EXPOSE 8080 matches Fly.io internal_port; Render uses dynamic PORT (see Program.cs).
EXPOSE 8080

# Do not set ASPNETCORE_URLS here — Render injects PORT; Fly sets URLs in fly.toml [env].
ENV ASPNETCORE_ENVIRONMENT=Production

# Start the application
ENTRYPOINT ["dotnet", "SelfOrderingSystemKiosk.dll"]

