# ---- Build stage -----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first for layer caching
COPY src/FactoryLine/FactoryLine.csproj src/FactoryLine/
COPY src/FactoryLine.Domain/FactoryLine.Domain.csproj src/FactoryLine.Domain/
RUN dotnet restore src/FactoryLine/FactoryLine.csproj

# Build and publish
COPY src/ src/
RUN dotnet publish src/FactoryLine/FactoryLine.csproj -c Release -o /app/publish

# ---- Runtime stage ---------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80
ENTRYPOINT ["dotnet", "FactoryLine.dll"]
