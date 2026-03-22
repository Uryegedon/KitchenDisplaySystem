# Monorepo entrypoint: build context is the repo root (Render default).
# SelfOrderingSystemKiosk/Dockerfile is unchanged for Fly.io when deploying from that folder.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY SelfOrderingSystemKiosk/*.csproj ./SelfOrderingSystemKiosk/
RUN dotnet restore SelfOrderingSystemKiosk/SelfOrderingSystemKiosk.csproj

COPY SelfOrderingSystemKiosk/ ./SelfOrderingSystemKiosk/
RUN dotnet publish SelfOrderingSystemKiosk/SelfOrderingSystemKiosk.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "SelfOrderingSystemKiosk.dll"]
